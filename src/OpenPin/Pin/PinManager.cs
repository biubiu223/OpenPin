using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenPin.Core;
using OpenPin.Core.Actions;
using OpenPin.Core.Imaging;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.Pin.Logic;
using OpenPin.Pin.Model;
using OpenPin.Pin.Native;
using OpenPin.Pin.Storage;
using OpenPin.Pin.Views;
using OpenPin.Services;

namespace OpenPin.Pin;

/// <summary>
/// PIN MODULE entry point (implements <see cref="IPinManager"/>): pin windows, pin groups, pin history and their
/// persistence under <see cref="AppPaths.PinsDir"/>. UI thread only.
/// <para>
/// State model: every pin is a <see cref="PinManifest"/>. Open pins of the current group (and pins moved away from it)
/// are live <see cref="PinWindow"/>s; open pins of other groups that were never shown in this session stay "dormant"
/// (no window, no pixels loaded) until their group is switched to; closed pins are the history.
/// </para>
/// </summary>
public sealed class PinManager : IPinManager, IPinGroupHost
{
    private const string EditedFile = PinStore.EditedFile;
    private const string OriginalFile = PinStore.OriginalFile;

    private readonly List<PinWindow> _windows = new();
    private readonly List<PinManifest> _dormant = new();
    private readonly List<PinManifest> _history = new();
    private readonly HashSet<PinWindow> _dirty = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private PinStore? _store;
    private PinGroupBook _groups = new();
    private GroupSwitchOverlay? _overlay;
    private bool _hiddenAll;
    private bool _shuttingDown;

    private static readonly PropertyInfo? AppShuttingDownProp =
        typeof(Application).GetProperty("IsShuttingDown", BindingFlags.NonPublic | BindingFlags.Static);

    public PinManager()
    {
        _saveTimer.Tick += (_, _) => FlushDirty();
        // the tray menu may be built before Initialize(): start with a valid (default-only) group book
        _groups.EnsureValid(Loc.Get("Pin.Group.DefaultName"));
    }

    internal PinSettings Settings => CurrentSettings;

    /// <summary>Pin settings (defaults when settings are not loaded, e.g. in tests).</summary>
    internal static PinSettings CurrentSettings => AppServices.Settings?.Current.Pin ?? new PinSettings();

    internal PinGroupBook Groups => _groups;

    PinGroupBook IPinGroupHost.Groups => _groups;

    private PinStore Store => _store ??= new PinStore(AppPaths.PinsDir);

    private string CurrentGroupId => _groups.CurrentId;

    private IEnumerable<PinWindow> CurrentWindows => _windows.Where(w => w.M.GroupId == CurrentGroupId);

    public int VisibleCount => CurrentWindows.Count(w => w.IsVisible);

    internal static bool AppIsShuttingDown
    {
        get
        {
            try { return AppShuttingDownProp?.GetValue(null) is true; }
            catch (Exception) { return false; }
        }
    }

    // ============================================================================ startup / shutdown

    public void Initialize()
    {
        try
        {
            _groups = PinGroupBook.FromJsonOrNew(Store.LoadGroupsJson(), Loc.Get("Pin.Group.DefaultName"));
            var all = Store.LoadAll().OrderBy(m => m.CreatedAt).ToList();
            bool restore = Settings.RestoreOnStartup;
            foreach (var m in all)
            {
                if (_groups.Find(m.GroupId) == null) m.GroupId = PinGroupBook.DefaultGroupId;
                if (m.IsOpen && restore)
                {
                    if (m.GroupId == CurrentGroupId) Materialize(m, activate: false);
                    else _dormant.Add(m);
                }
                else
                {
                    if (m.IsOpen)
                    {
                        m.IsOpen = false;
                        m.ClosedAt ??= DateTime.Now;
                        Store.SaveManifest(m);
                    }
                    _history.Add(m);
                }
            }
            EvictHistory();
            Log.Info($"Pins restored: {_windows.Count} open, {_dormant.Count} in other groups, {_history.Count} in history");
        }
        catch (Exception ex)
        {
            Log.Error("Pin initialization failed", ex);
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        if (AppServices.Settings != null) AppServices.Settings.Changed += (_, _) => OnSettingsChanged();
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _saveTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_store == null) return;
        Store.Flush();
        foreach (var w in _windows)
        {
            try
            {
                w.M.IsOpen = true;
                Store.SaveManifestNow(w.M);
            }
            catch (Exception ex) { Log.Error($"Saving pin {w.M.Id} failed", ex); }
        }
        _dirty.Clear();
        try { Store.SaveGroupsNow(_groups); }
        catch (Exception ex) { Log.Error("Saving pin groups failed", ex); }
        Store.Flush();
    }

    private void OnSettingsChanged()
    {
        foreach (var w in _windows)
        {
            try
            {
                w.UpdateTitle();
                w.ApplyLayout();
            }
            catch (Exception ex) { Log.Error("Applying pin settings failed", ex); }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // monitors changed: after things settle, make sure no pin is stranded off-screen
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1000);
            BringAllIntoView();
        });
    }

    // ============================================================================ creating pins

    public void PinImage(BitmapSource image, Int32Rect? screenRectPx = null, string? title = null) =>
        PinImageCore(image, screenRectPx, title, null, null);

    private PinWindow? PinImageCore(BitmapSource image, Int32Rect? rect, string? title, string? sourcePath, Point? offset)
    {
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0) return null;
        var m = NewManifest(PinKind.Image);
        m.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        m.SourcePath = sourcePath;
        var body = new ImageBody(image, null, m.Adjust);
        return CreatePin(m, body, rect, activate: true, offset);
    }

    /// <summary>
    /// Pin an image derived from <paramref name="source"/> (same pixel size as its image, e.g. a translated copy, PRO-01)
    /// exactly over it: same content origin and zoom.
    /// </summary>
    internal void PinImageOver(PinWindow source, BitmapSource image)
    {
        var rect = new Int32Rect(source.M.OriginX, source.M.OriginY, image.PixelWidth, image.PixelHeight);
        var win = PinImageCore(image, rect, null, null, null);
        if (win == null) return;
        double scale = source.Scale;
        if (Math.Abs(scale - 1) > 1e-6) win.SetZoom(scale / win.M.BaseScale, new Point(win.M.OriginX, win.M.OriginY));
    }

    public void PinContent(PinContent content, Int32Rect? screenRectPx = null)
    {
        try
        {
            if (content is ImagePinContent img)
            {
                PinImageCore(img.Image, screenRectPx, null, img.SourcePath, null);
                return;
            }
            var (m, body) = BuildPin(content);
            if (body != null) CreatePin(m, body, screenRectPx, activate: true, null);
        }
        catch (Exception ex)
        {
            Log.Error("Pinning content failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }

    /// <summary>Manifest + body for a content (base scale is set later by the placement).</summary>
    private (PinManifest M, PinBody? Body) BuildPin(PinContent content)
    {
        switch (content)
        {
            case ImagePinContent img:
            {
                var m = NewManifest(PinKind.Image);
                m.SourcePath = img.SourcePath;
                return (m, new ImageBody(img.Image, null, m.Adjust));
            }
            case TextPinContent text:
            {
                var m = NewManifest(PinKind.Text);
                return (m, new TextBody(text.PlainText, text.Html, false));
            }
            case FilePinContent files when files.Paths.Count > 0:
            {
                var m = NewManifest(PinKind.Files);
                return (m, new FilesBody(files.Paths));
            }
            case ColorPinContent color:
            {
                var m = NewManifest(PinKind.Color);
                return (m, new ColorBody(color.Color, color.OriginalText));
            }
        }
        return (NewManifest(PinKind.Text), null);
    }

    private PinManifest NewManifest(PinKind kind)
    {
        var s = Settings;
        return new PinManifest
        {
            Id = PinManifest.NewId(),
            Kind = kind,
            GroupId = CurrentGroupId,
            Opacity = Math.Clamp(s.DefaultOpacity, 10, 100) / 100.0,
            Topmost = s.Topmost,
            Shadow = s.ShowShadow,
            CreatedAt = DateTime.Now,
        };
    }

    private PinWindow? CreatePin(PinManifest m, PinBody body, Int32Rect? rect, bool activate, Point? offset)
    {
        m.Kind = body.Kind;
        m.GroupId = CurrentGroupId;
        body.SaveTo(m);
        try { m.ContentHash = body.ContentHash(); }
        catch (Exception ex) { Log.Warn($"Content hash failed: {ex.Message}"); }

        Place(m, body, rect);
        if (offset is { } o)
        {
            m.OriginX += (int)o.X;
            m.OriginY += (int)o.Y;
        }

        var win = NewWindow(m, body);
        win.ShowPin(activate);
        if (rect == null) win.BringIntoView(WorkAreas());

        if (body is ImageBody ib) Store.SaveImage(m.Id, OriginalFile, ib.Original);
        Store.SaveManifest(m);
        if (Settings.TextSelectableByDefault) win.ScheduleOcr();
        return win;
    }

    /// <summary>Initial position / scale of a new pin (PIN-01 exact placement, otherwise <see cref="PinSettings.DefaultPosition"/>).</summary>
    private void Place(PinManifest m, PinBody body, Int32Rect? rect)
    {
        var size = body.ContentSize;
        if (rect is { } r)
        {
            var mon = Monitors.FromPointPx(r.X + r.Width / 2, r.Y + r.Height / 2);
            m.BaseScale = body.Kind == PinKind.Image ? 1 : mon.Scale;
            m.Zoom = 1;
            m.OriginX = r.X;
            m.OriginY = r.Y;
            return;
        }

        var s = Settings;
        var cursor = Monitors.CursorPx;
        MonitorInfo target = s.DefaultPosition == PinDefaultPosition.PrimaryCenter ? Monitors.Primary : Monitors.UnderCursor;
        var work = target.WorkAreaPx;
        m.BaseScale = body.Kind == PinKind.Image ? 1 : target.Scale;
        double w = size.Width * m.BaseScale, h = size.Height * m.BaseScale;
        m.Zoom = s.FitLargeImagesToScreen ? PinGeometry.FitZoom(w, h, work) : 1;
        m.Zoom = PinGeometry.ClampZoom(m.Zoom, size.Width, size.Height, m.BaseScale);
        w *= m.Zoom;
        h *= m.Zoom;

        Point center = s.DefaultPosition switch
        {
            PinDefaultPosition.PrimaryCenter or PinDefaultPosition.MouseScreenCenter =>
                new Point(work.X + work.Width / 2.0, work.Y + work.Height / 2.0),
            _ => cursor,
        };
        var tl = PinGeometry.CenterOn(center, w, h);
        var placed = PinGeometry.ClampInto(new Int32Rect((int)tl.X, (int)tl.Y, (int)Math.Ceiling(w), (int)Math.Ceiling(h)), work);
        m.OriginX = placed.X;
        m.OriginY = placed.Y;
    }

    private PinWindow NewWindow(PinManifest m, PinBody body)
    {
        var win = new PinWindow(this, m, body);
        _windows.Add(win);
        return win;
    }

    /// <summary>Creates the window of a persisted open pin. Returns null if its data is unusable.</summary>
    private PinWindow? Materialize(PinManifest m, bool activate)
    {
        try
        {
            var body = LoadBody(m);
            if (body == null)
            {
                Log.Warn($"Pin {m.Id} could not be loaded; moved to history");
                return null;
            }
            var win = NewWindow(m, body);
            win.ShowPin(activate);
            win.BringIntoView(WorkAreas());
            if (Settings.TextSelectableByDefault) win.ScheduleOcr();
            return win;
        }
        catch (Exception ex)
        {
            Log.Error($"Restoring pin {m.Id} failed", ex);
            return null;
        }
    }

    private PinBody? LoadBody(PinManifest m)
    {
        switch (m.Kind)
        {
            case PinKind.Image:
            {
                var original = Store.LoadImage(m.Id, OriginalFile);
                if (original == null) return null;
                var edited = m.HasEditedImage ? Store.LoadImage(m.Id, EditedFile) : null;
                if (m.HasEditedImage && edited == null) m.HasEditedImage = false;
                return new ImageBody(original, edited, m.Adjust);
            }
            case PinKind.Text:
                return new TextBody(m.Text ?? "", m.Html, m.IgnoreFormat);
            case PinKind.Files:
                return m.Files is { Count: > 0 } f ? new FilesBody(f) : null;
            case PinKind.Color:
                return ColorText.TryParse(m.Color, out var c) ? new ColorBody(c, m.ColorOriginalText) : null;
        }
        return null;
    }

    // ============================================================================ clipboard / file

    public void PinFromClipboard()
    {
        PinContent? content;
        try { content = ClipboardService.GetPinContent(); }
        catch (Exception ex)
        {
            Log.Error("Reading the clipboard failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
            return;
        }
        if (content == null)
        {
            Notify.Info(Loc.Get("Pin.ClipboardEmpty"));
            return;
        }
        try
        {
            var (m, body) = BuildPin(content);
            if (body == null)
            {
                Notify.Info(Loc.Get("Pin.ClipboardEmpty"));
                return;
            }
            if (HandleDuplicate(body)) return;
            CreatePin(m, body, null, activate: true, null);
        }
        catch (Exception ex)
        {
            Log.Error("Pin from clipboard failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }

    /// <summary>Applies <see cref="PinSettings.OnDuplicate"/>. Returns true if no new pin should be created.</summary>
    private bool HandleDuplicate(PinBody body)
    {
        var behavior = Settings.OnDuplicate;
        if (behavior == DuplicatePinBehavior.None) return false;
        string hash;
        try { hash = body.ContentHash(); }
        catch (Exception ex) { Log.Warn($"Hash failed: {ex.Message}"); return false; }
        if (hash.Length == 0) return false;

        var open = CurrentWindows.LastOrDefault(w => w.M.ContentHash == hash && w.IsVisible);
        switch (behavior)
        {
            case DuplicatePinBehavior.Shake when open != null:
                open.Shake();
                return true;
            case DuplicatePinBehavior.RestoreClosed:
                if (open != null) { open.Shake(); return true; }
                var closed = _history.Where(h => h.ContentHash == hash && h.GroupId == CurrentGroupId)
                                     .OrderByDescending(h => h.ClosedAt ?? DateTime.MinValue).FirstOrDefault();
                if (closed != null && Restore(closed) != null) return true;
                return false;
            case DuplicatePinBehavior.PinAgain when open != null:
                ClosePinCore(open);
                return false;
        }
        return false;
    }

    public void PinFromFile()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = Loc.Get("Pin.FromFile.Title"),
            Filter = Loc.Get("Pin.FromFile.Filter") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.ico;*.jxr|" + Loc.Get("Pin.FromFile.All") + "|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        int i = 0;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var img = BitmapUtil.LoadFile(file);
                PinImageCore(img, null, null, file, new Point(i * 24, i * 24));
                i++;
            }
            catch (Exception ex)
            {
                Log.Error($"Cannot pin file {file}", ex);
                Notify.Error(Loc.Format("Pin.FromFile.Failed", Path.GetFileName(file), ex.Message));
            }
        }
    }

    /// <summary>Best pinnable content of a drag-drop / data object (PIN-14).</summary>
    internal static PinContent? ContentFromData(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            if (files.Length == 1 && ClipboardService.IsImageFile(files[0]) && File.Exists(files[0]))
                return new ImagePinContent(BitmapUtil.LoadFile(files[0]), files[0]);
            return new FilePinContent(files);
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            return new ImagePinContent(BitmapUtil.Normalize96Dpi(bmp));
        var text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        if (!string.IsNullOrEmpty(text))
        {
            if (ColorText.TryParse(text, out var c)) return new ColorPinContent(c, text.Trim());
            var html = data.GetDataPresent(DataFormats.Html) ? data.GetData(DataFormats.Html) as string : null;
            return new TextPinContent(text, html);
        }
        return null;
    }

    internal void PasteFromClipboardOnto(PinWindow win)
    {
        if (Settings.PasteBehavior == PinPasteBehavior.Disabled) return;
        PinContent? content;
        try { content = ClipboardService.GetPinContent(); }
        catch (Exception ex)
        {
            Log.Error("Reading the clipboard failed", ex);
            return;
        }
        if (content == null)
        {
            Notify.Info(Loc.Get("Pin.ClipboardEmpty"));
            return;
        }
        PasteOnto(win, content);
    }

    /// <summary>Ctrl+V / drop onto a pin: replace its content or pin a new one (per settings).</summary>
    internal void PasteOnto(PinWindow win, PinContent content)
    {
        if (Settings.PasteBehavior == PinPasteBehavior.NewPin)
        {
            var (nm, nb) = BuildPin(content);
            if (nb == null) return;
            CreatePin(nm, nb, null, true, null);
            return;
        }

        var m = win.M;
        PinBody? body;
        m.Adjust.Reset();
        switch (content)
        {
            case ImagePinContent img: body = new ImageBody(img.Image, null, m.Adjust); break;
            case TextPinContent t: body = new TextBody(t.PlainText, t.Html, false); break;
            case FilePinContent f when f.Paths.Count > 0: body = new FilesBody(f.Paths); break;
            case ColorPinContent c: body = new ColorBody(c.Color, c.OriginalText); break;
            default: return;
        }
        // clear old content fields
        m.Text = null; m.Html = null; m.IgnoreFormat = false; m.Files = null; m.Color = null; m.ColorOriginalText = null;
        m.SourcePath = content is ImagePinContent ic ? ic.SourcePath : null;
        m.HasEditedImage = false;
        body.SaveTo(m);
        try { m.ContentHash = body.ContentHash(); }
        catch (Exception ex) { Log.Warn($"Content hash failed: {ex.Message}"); }

        double baseScale = body.Kind == PinKind.Image ? 1 : Monitors.FromPointPx(m.OriginX, m.OriginY).Scale;
        win.ReplaceBody(body, baseScale);
        Store.DeleteFile(m.Id, EditedFile);
        if (body is ImageBody ib) Store.SaveImage(m.Id, OriginalFile, ib.Original);
        else Store.DeleteFile(m.Id, OriginalFile);
        MarkDirty(win);
    }

    // ============================================================================ close / destroy / history

    internal void ClosePin(PinWindow win, PinCloseReason reason)
    {
        if (!_windows.Contains(win)) return;
        var s = Settings;
        bool confirm = reason switch
        {
            PinCloseReason.Esc => s.ConfirmEscClose,
            PinCloseReason.DoubleClick => s.ConfirmDoubleClickClose,
            _ => false,
        };
        if (confirm && !PinDialog.Confirm(Loc.Get("Pin.Close.ConfirmTitle"), Loc.Get("Pin.Close.ConfirmMessage"), Loc.Get("Pin.Menu.Close")))
            return;
        Log.Info($"Pin {win.M.Id} closed ({reason})");
        ClosePinCore(win);
    }

    /// <summary>Closes a pin into the history (Ctrl+3 restores it).</summary>
    private void ClosePinCore(PinWindow win)
    {
        if (win.IsAnnotating) win.EndAnnotation(true);
        _windows.Remove(win);
        _dirty.Remove(win);
        var m = win.M;
        m.IsOpen = false;
        m.ClosedAt = DateTime.Now;
        CloseWindow(win);
        _history.Add(m);
        Store.SaveManifest(m);
        EvictHistory();
    }

    /// <summary>Destroys a pin: no history, files deleted immediately.</summary>
    internal void DestroyPin(PinWindow win)
    {
        Log.Info($"Pin {win.M.Id} destroyed");
        _windows.Remove(win);
        _dirty.Remove(win);
        CloseWindow(win);
        Store.Delete(win.M.Id);
    }

    private static void CloseWindow(PinWindow win)
    {
        try
        {
            win.AllowClose = true;
            win.Close();
            if (win.IsVisible) win.Hide(); // close refused by WPF (re-entrancy): never leave a ghost pin on screen
        }
        catch (Exception ex) { Log.Error("Closing pin window failed", ex); }
    }

    /// <summary>Called when a pin window is closed by something other than the manager (e.g. Alt+F4).</summary>
    internal void OnWindowClosingExternally(PinWindow win)
    {
        if (_shuttingDown || AppIsShuttingDown) return;
        Log.Info($"Pin {win.M.Id} window closed externally");
        Application.Current?.Dispatcher.BeginInvoke(() => ClosePin(win, PinCloseReason.Command));
    }

    private void EvictHistory()
    {
        var evict = PinHistoryPolicy.SelectEvictions(_history.Select(h => (h.Id, h.ClosedAt)), Math.Max(0, Settings.HistoryCount));
        foreach (var id in evict)
        {
            _history.RemoveAll(h => h.Id == id);
            Store.Delete(id);
        }
    }

    public void RestoreLastClosed()
    {
        var id = PinHistoryPolicy.MostRecent(_history.Select(h => (h.Id, h.GroupId, h.ClosedAt)), CurrentGroupId);
        var m = id == null ? null : _history.FirstOrDefault(h => h.Id == id);
        if (m == null)
        {
            Notify.Info(Loc.Get("Pin.NothingToRestore"));
            return;
        }
        if (Restore(m) == null) Notify.Error(Loc.Get("Pin.RestoreFailed"));
    }

    private PinWindow? Restore(PinManifest m)
    {
        _history.Remove(m);
        m.IsOpen = true;
        m.ClosedAt = null;
        m.GroupId = CurrentGroupId;
        var win = Materialize(m, activate: true);
        if (win == null)
        {
            Store.Delete(m.Id);
            return null;
        }
        Store.SaveManifest(m);
        return win;
    }

    public void CloseAll()
    {
        foreach (var w in CurrentWindows.ToList()) ClosePinCore(w);
    }

    public void DestroyAll()
    {
        var list = CurrentWindows.ToList();
        if (list.Count == 0) return;
        if (Settings.ConfirmDestroyAll &&
            !PinDialog.Confirm(Loc.Get("Pin.DestroyAll.Title"), Loc.Format("Pin.DestroyAll.Message", list.Count), Loc.Get("Pin.Menu.Destroy")))
            return;
        foreach (var w in list) DestroyPin(w);
    }

    public void ToggleHideAll()
    {
        var list = CurrentWindows.ToList();
        bool anyVisible = list.Any(w => w.IsVisible);
        _hiddenAll = anyVisible;
        foreach (var w in list)
        {
            if (anyVisible)
            {
                if (w.IsAnnotating) w.EndAnnotation(true);
                w.Hide();
            }
            else
            {
                w.ShowActivated = false;
                w.Show();
                w.ApplyLayout();
            }
        }
    }

    public void ToggleClickThroughUnderCursor()
    {
        var p = Monitors.CursorPx;
        var hwnd = PinNative.RootWindowFromPoint((int)p.X, (int)p.Y);
        var hit = _windows.FirstOrDefault(w => w.IsVisible && WindowHelper.GetHandle(w) == hwnd);
        // click-through pins are invisible to WindowFromPoint: find them geometrically
        hit ??= _windows.LastOrDefault(w => w.IsVisible && w.M.ClickThrough && Contains(w.VisibleRectPx, p));
        if (hit == null)
        {
            Notify.Info(Loc.Get("Pin.NoPinUnderCursor"));
            return;
        }
        hit.SetClickThrough(!hit.M.ClickThrough);
    }

    private static bool Contains(Int32Rect r, Point p) => p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;

    private static IReadOnlyList<Int32Rect> WorkAreas()
    {
        try { return Monitors.GetAll().Select(m => m.WorkAreaPx).ToList(); }
        catch (Exception ex)
        {
            Log.Warn($"Monitor enumeration failed: {ex.Message}");
            return Array.Empty<Int32Rect>();
        }
    }

    private void BringAllIntoView()
    {
        var areas = WorkAreas();
        foreach (var w in _windows.Where(w => w.IsVisible)) w.BringIntoView(areas);
    }

    private void SetAllThumbnails(bool on)
    {
        foreach (var w in CurrentWindows.Where(w => w.IsVisible)) w.SetThumbnail(on);
    }

    private void SaveAllToFolder()
    {
        var list = CurrentWindows.ToList();
        if (list.Count == 0) return;
        var dlg = new OpenFolderDialog { Title = Loc.Get("Pin.SaveAll.Title") };
        if (dlg.ShowDialog() != true) return;
        int ok = 0;
        foreach (var w in list)
        {
            try
            {
                var baseName = PinWindow.SafeFileName(w.M.Title ?? $"OpenPin_{w.M.CreatedAt:yyyy-MM-dd_HH-mm-ss}");
                var path = Path.Combine(dlg.FolderName, baseName + ".png");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(dlg.FolderName, $"{baseName} ({i}).png");
                SaveService.SaveToFile(w.GetImage(false), path);
                ok++;
            }
            catch (Exception ex) { Log.Error($"Saving pin {w.M.Id} failed", ex); }
        }
        Notify.Info(Loc.Format("Pin.SaveAll.Done", ok, dlg.FolderName));
    }

    // ============================================================================ persistence helpers

    internal void MarkDirty(PinWindow win)
    {
        if (_shuttingDown || !_windows.Contains(win)) return;
        _dirty.Add(win);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushDirty()
    {
        _saveTimer.Stop();
        foreach (var w in _dirty.ToList())
            if (_windows.Contains(w)) Store.SaveManifest(w.M);
        _dirty.Clear();
    }

    /// <summary>The edited pixels of an image pin changed (annotation / crop / restore).</summary>
    internal void OnImageEdited(PinWindow win)
    {
        if (win.Body is not ImageBody ib) return;
        if (ib.HasEdits)
        {
            win.M.HasEditedImage = true;
            Store.SaveImage(win.M.Id, EditedFile, ib.Edited);
        }
        else
        {
            win.M.HasEditedImage = false;
            Store.DeleteFile(win.M.Id, EditedFile);
        }
        MarkDirty(win);
    }

    // ============================================================================ groups (PIN-11)

    internal Color? GroupColor(string groupId)
    {
        var g = _groups.Find(groupId);
        return g?.Color != null && ColorText.TryParse(g.Color, out var c) ? c : null;
    }

    public (int OnScreen, int Total) CountGroup(string groupId)
    {
        int on = _windows.Count(w => w.M.GroupId == groupId && w.IsVisible);
        int total = _windows.Count(w => w.M.GroupId == groupId) + _dormant.Count(m => m.GroupId == groupId);
        return (on, total);
    }

    public void SwitchToNextGroup() => SwitchGroup(_groups.NextId(), Settings.ShowGroupSwitchOverlay);

    public void SwitchGroup(string id, bool showOverlay)
    {
        if (_groups.Find(id) == null) return;
        if (id != CurrentGroupId)
        {
            foreach (var w in CurrentWindows.ToList())
            {
                if (w.IsAnnotating) w.EndAnnotation(true);
                w.Hide();
            }
            _groups.CurrentId = id;
            _hiddenAll = false;
            foreach (var w in CurrentWindows.ToList())
            {
                w.ShowActivated = false;
                w.Show();
                w.ApplyLayout();
            }
            foreach (var m in _dormant.Where(m => m.GroupId == id).ToList())
            {
                _dormant.Remove(m);
                if (Materialize(m, activate: false) == null)
                {
                    m.IsOpen = false;
                    m.ClosedAt = DateTime.Now;
                    _history.Add(m);
                    Store.SaveManifest(m);
                }
            }
            Store.SaveGroups(_groups);
        }
        if (showOverlay) ShowOverlay();
    }

    private void ShowOverlay()
    {
        try
        {
            _overlay ??= new GroupSwitchOverlay();
            _overlay.ShowGroups(this);
        }
        catch (Exception ex) { Log.Error("Group overlay failed", ex); }
    }

    public void CreateGroupInteractive() => CreateGroupInteractive(null);

    private PinGroup? CreateGroupInteractive(PinWindow? moveHere)
    {
        var d = new NewGroupDialog(_groups);
        if (d.ShowDialog() != true) return null;
        var g = _groups.Create(d.GroupName, d.GroupColor);
        if (g == null) return null;
        Store.SaveGroups(_groups);
        if (moveHere != null) MovePinToGroup(moveHere, g.Id);
        if (d.SwitchNow) SwitchGroup(g.Id, Settings.ShowGroupSwitchOverlay);
        return g;
    }

    internal void CreateGroupAndMove(PinWindow win) => CreateGroupInteractive(win);

    internal void MovePinToGroup(PinWindow win, string groupId)
    {
        if (_groups.Find(groupId) == null || win.M.GroupId == groupId) return;
        win.M.GroupId = groupId;
        if (groupId != CurrentGroupId)
        {
            if (win.IsAnnotating) win.EndAnnotation(true);
            win.Hide();
        }
        else if (!win.IsVisible)
        {
            win.Show();
            win.ApplyLayout();
        }
        win.UpdateGlow();
        MarkDirty(win);
    }

    public bool RenameGroup(string id, string name)
    {
        if (!_groups.Rename(id, name)) return false;
        Store.SaveGroups(_groups);
        return true;
    }

    public void SetGroupColor(string id, string? color)
    {
        if (!_groups.SetColor(id, color)) return;
        Store.SaveGroups(_groups);
        foreach (var w in _windows.Where(w => w.M.GroupId == id)) w.UpdateGlow();
    }

    public void MoveGroup(string id, int delta)
    {
        if (_groups.Move(id, delta)) Store.SaveGroups(_groups);
    }

    public void DeleteGroup(string id, string? moveToId)
    {
        if (id == PinGroupBook.DefaultGroupId || _groups.Find(id) == null) return;
        if (moveToId != null && (_groups.Find(moveToId) == null || moveToId == id)) moveToId = PinGroupBook.DefaultGroupId;
        if (CurrentGroupId == id) SwitchGroup(moveToId ?? PinGroupBook.DefaultGroupId, false);

        if (moveToId == null) ClearGroup(id);
        else
        {
            foreach (var w in _windows.Where(w => w.M.GroupId == id).ToList()) MovePinToGroup(w, moveToId);
            foreach (var m in _dormant.Where(m => m.GroupId == id).ToList())
            {
                m.GroupId = moveToId;
                if (moveToId == CurrentGroupId)
                {
                    _dormant.Remove(m);
                    Materialize(m, false);
                }
                Store.SaveManifest(m);
            }
            foreach (var m in _history.Where(m => m.GroupId == id))
            {
                m.GroupId = moveToId;
                Store.SaveManifest(m);
            }
        }
        _groups.Delete(id);
        Store.SaveGroups(_groups);
    }

    public void ClearGroup(string id)
    {
        foreach (var w in _windows.Where(w => w.M.GroupId == id).ToList()) DestroyPin(w);
        foreach (var m in _dormant.Where(m => m.GroupId == id).ToList())
        {
            _dormant.Remove(m);
            Store.Delete(m.Id);
        }
        foreach (var m in _history.Where(m => m.GroupId == id).ToList())
        {
            _history.Remove(m);
            Store.Delete(m.Id);
        }
    }

    private void ManageGroups()
    {
        var d = new ManageGroupsDialog(this);
        d.ShowDialog();
    }

    // ============================================================================ tray (PIN-12)

    public void PopulateTrayMenu(ItemCollection items)
    {
        items.Add(new Separator());
        var (on, total) = CountGroup(CurrentGroupId);
        items.Add(PinMenuFactory.Make(Loc.Format("Pin.Tray.Count", on, total), "", null, enabled: false));

        var groups = PinMenuFactory.Make(Loc.Format("Pin.Tray.Groups", _groups.Current.Name), "", null);
        foreach (var g in _groups.Groups)
        {
            var id = g.Id;
            var (gon, gtotal) = CountGroup(id);
            var mi = PinMenuFactory.Make(g.Name, null, () => SwitchGroup(id, Settings.ShowGroupSwitchOverlay), $"{gon}/{gtotal}");
            mi.IsChecked = id == CurrentGroupId;
            groups.Items.Add(mi);
        }
        groups.Items.Add(new Separator());
        var next = PinMenuFactory.Make(Loc.Get("Pin.Tray.NextGroup"), null, SwitchToNextGroup);
        if (AppServices.Hotkeys?.Bindings.TryGetValue(ActionIds.PinNextGroup, out var hk) == true && !hk.IsEmpty) next.InputGestureText = hk.ToString();
        groups.Items.Add(next);
        groups.Items.Add(PinMenuFactory.Make(Loc.Get("Pin.Group.NewEllipsis"), "", CreateGroupInteractive));
        groups.Items.Add(PinMenuFactory.Make(Loc.Get("Pin.Group.ManageEllipsis"), "", ManageGroups));
        items.Add(groups);

        bool any = total > 0;
        items.Add(PinMenuFactory.Make(Loc.Get("Pin.Tray.BringIntoView"), "", BringAllIntoView, enabled: on > 0));
        bool anyThumb = CurrentWindows.Any(w => w.IsVisible && w.M.IsThumbnail);
        bool anyFull = CurrentWindows.Any(w => w.IsVisible && !w.M.IsThumbnail);
        items.Add(PinMenuFactory.Make(Loc.Get("Pin.Tray.AllThumbnails"), "", () => SetAllThumbnails(true), enabled: anyFull));
        items.Add(PinMenuFactory.Make(Loc.Get("Pin.Tray.CancelThumbnails"), null, () => SetAllThumbnails(false), enabled: anyThumb));
        items.Add(PinMenuFactory.Make(Loc.Get("Pin.Tray.SaveAll"), "", SaveAllToFolder, enabled: any));
        items.Add(PinMenuFactory.Make(Loc.Get(on > 0 ? "Pin.Tray.HideAll" : "Pin.Tray.ShowAll"), on > 0 ? "" : "", ToggleHideAll,
            Gesture(ActionIds.PinToggleHideAll), enabled: any));
        items.Add(PinMenuFactory.Make(Loc.Get("Pin.Tray.CloseAll"), "", CloseAll, Gesture(ActionIds.PinCloseAll), enabled: any));
        items.Add(PinMenuFactory.Make(Loc.Get("Pin.Tray.DestroyAll"), "", DestroyAll, Gesture(ActionIds.PinDestroyAll), enabled: any));
    }

    private static string? Gesture(string actionId) =>
        AppServices.Hotkeys?.Bindings.TryGetValue(actionId, out var hk) == true && !hk.IsEmpty ? hk.ToString() : null;
}
