using System.Reflection;
using OpenPin.Core.Settings;

namespace OpenPin.SettingsUI.Logic;

internal enum SettingKind { Toggle, Number, Slider, Enum, Choice, Text, Color, Folder, Template, Font }

internal enum TemplateMode { FileName, FullPath }

/// <summary>A combo box option. <see cref="Label"/> is a resource key ("SettingsUI.…") or literal text.</summary>
internal sealed record ChoiceOption(object Value, string Label);

/// <summary>
/// Declarative description of one setting card bound to <c>AppSettings.{Section}.{Property}</c>.
/// Title = resource <c>SettingsUI.{Section}.{Property}</c>, description = <c>….Desc</c> (optional).
/// </summary>
internal sealed record SettingDef(string Section, string Property, SettingKind Kind, string Icon = "")
{
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    /// <summary>Resource key of the unit shown after number boxes / sliders.</summary>
    public string? UnitKey { get; init; }
    public IReadOnlyList<ChoiceOption>? Choices { get; init; }
    /// <summary>Color: empty text is allowed (meaning "default").</summary>
    public bool AllowEmpty { get; init; }
    /// <summary>Color: show/keep the alpha channel (#AARRGGBB).</summary>
    public bool AllowAlpha { get; init; }
    /// <summary>Name of a bool property of the same section that enables this card.</summary>
    public string? EnabledBy { get; init; }
    /// <summary>Template: how to preview. FullPath uses <see cref="FolderProperty"/> + <see cref="DefaultFolder"/>.</summary>
    public TemplateMode TemplateMode { get; init; }
    public string? FolderProperty { get; init; }
    /// <summary>Folder / template: "pictures" (Pictures\OpenPin) or "autosave" (Pictures\OpenPin\AutoSave).</summary>
    public string? DefaultFolder { get; init; }
    /// <summary>Template (FileName mode): property holding the extension used in the preview.</summary>
    public string? ExtensionProperty { get; init; }
    /// <summary>Overrides the title (fallback cards of properties without a resource key).</summary>
    public string? TitleOverride { get; init; }

    public string TitleKey => $"SettingsUI.{Section}.{Property}";
    public string DescKey => TitleKey + ".Desc";
    public string Path => $"{Section}.{Property}";
}

internal abstract record Block;

/// <summary>A single settings card.</summary>
internal sealed record SettingBlock(SettingDef Def) : Block;

/// <summary>A small section caption between cards.</summary>
internal sealed record HeaderBlock(string TitleKey) : Block;

/// <summary>An expander card. <see cref="HeaderToggle"/> (optional) is shown in the expander header.</summary>
internal sealed record GroupBlock(string TitleKey, string Icon, IReadOnlyList<SettingDef> Items, SettingDef? HeaderToggle = null) : Block
{
    public string DescKey => TitleKey + ".Desc";
}

/// <summary>A hand-written block (hotkey list, palette editor…). <see cref="Covers"/> = "Section.Property" paths it edits.</summary>
internal sealed record CustomBlock(string Id, params string[] Covers) : Block;

internal sealed record PageDef(string Id, string TitleKey, string Icon, string? Section, IReadOnlyList<Block> Blocks);

/// <summary>
/// The settings pages (SET-01/03). Pure data: the window renders it, tests check that it covers every property.
/// Properties of a page's section that are not listed here are rendered automatically ("其他" group), so options added
/// later by other modules show up without UI work.
/// </summary>
internal static class SettingsCatalog
{
    // Segoe Fluent Icons / Segoe MDL2 Assets code points
    public static class Icons
    {
        public const string General = "", Keyboard = "", Capture = "", Pin = "", Annotation = "",
            Save = "", Ocr = "", LongCapture = "", Recording = "", About = "",
            Theme = "", Language = "", Power = "", Mouse = "", Bell = "", Palette = "",
            Accent = "", Search = "", Timer = "", Cursor = "", Grid = "", Zoom = "",
            Warning = "", Keys = "", Qr = "", History = "", Copy = "", Color = "",
            Border = "", Ratio = "", Folder = "", Template = "", Image = "", Quality = "",
            Pdf = "", File = "", Text = "", Font = "", Opacity = "", Shadow = "",
            Position = "", Topmost = "", Close = "", Duplicate = "", Paste = "", Title = "",
            Thumbnail = "", Group = "", Wheel = "", Direction = "", Length = "", Fps = "",
            Format = "", Edit = "", Serial = "", Undo = "", Upscale = "", Space = "",
            Punctuation = "", Layout = "", Backup = "", Import = "", Export = "", Reset = "",
            Link = "", License = "", Code = "", Data = "", Log = "", Info = "",
            Translate = "", Recognition = "", Keyboard2 = "", Highlight = "", Delay = "", Presets = "", Lock = "";
        public const string Mic = "\uE720", Camera = "\uE722";
    }

    public static readonly IReadOnlyList<ChoiceOption> SaveExtensions = new[]
    {
        new ChoiceOption(".png", "PNG"), new ChoiceOption(".jpg", "JPG"), new ChoiceOption(".bmp", "BMP"),
        new ChoiceOption(".tiff", "TIFF"), new ChoiceOption(".webp", "WebP"), new ChoiceOption(".pdf", "PDF"),
    };

    public static readonly IReadOnlyList<PageDef> Pages = BuildPages();

    /// <summary>"Section.Property" paths deliberately not shown (runtime state written by modules).</summary>
    public static readonly IReadOnlySet<string> Skipped = new HashSet<string>(StringComparer.Ordinal)
    {
        "Capture.LastCustom",
        "Save.LastFolder",
        // Runtime state / adjusted live on the recording control bar.
        "Recording.LastSaveFolder",
        "Recording.MicrophoneDeviceId",
        "Recording.MicrophoneVolume",
        "Recording.SystemAudioVolume",
        "Recording.WebcamDeviceId",
        "Recording.WebcamSize",
        "Recognition.LastExportFolder",
    };

    private static IReadOnlyList<PageDef> BuildPages()
    {
        static SettingDef T(string s, string p, string icon = "") => new(s, p, SettingKind.Toggle, icon);
        static SettingDef E(string s, string p, string icon = "") => new(s, p, SettingKind.Enum, icon);
        static SettingDef N(string s, string p, double min, double max, string? unit = null, string icon = "") =>
            new(s, p, SettingKind.Number, icon) { Min = min, Max = max, UnitKey = unit };
        static SettingDef Sl(string s, string p, double min, double max, string? unit = null, string icon = "") =>
            new(s, p, SettingKind.Slider, icon) { Min = min, Max = max, UnitKey = unit };
        const string px = "SettingsUI.Unit.Px", pct = "SettingsUI.Unit.Percent", ms = "SettingsUI.Unit.Ms",
            sec = "SettingsUI.Unit.Seconds", items = "SettingsUI.Unit.Items", days = "SettingsUI.Unit.Days", times = "SettingsUI.Unit.Times",
            pt = "SettingsUI.Unit.Pt";

        const string G = "General", C = "Capture", P = "Pin", A = "Annotation", S = "Save", O = "Ocr", L = "LongCapture", R = "Recording";

        return new[]
        {
            new PageDef("general", "SettingsUI.Page.General", Icons.General, G, new Block[]
            {
                new HeaderBlock("SettingsUI.Header.Appearance"),
                new SettingBlock(E(G, "Theme", Icons.Theme)),
                new SettingBlock(new SettingDef(G, "AccentColor", SettingKind.Color, Icons.Accent)),
                new SettingBlock(new SettingDef(G, "Language", SettingKind.Choice, Icons.Language)
                    { Choices = new[] { new ChoiceOption("zh-CN", "简体中文") } }),
                new HeaderBlock("SettingsUI.Header.Startup"),
                new SettingBlock(T(G, "AutoStart", Icons.Power)),
                new SettingBlock(T(G, "ShowStartupNotification", Icons.Bell)),
                new SettingBlock(E(G, "TrayLeftClick", Icons.Mouse)),
                new HeaderBlock("SettingsUI.Header.Backup"),
                new CustomBlock("general.backup"),
            }),
            new PageDef("hotkeys", "SettingsUI.Page.Hotkeys", Icons.Keyboard, "Hotkeys", new Block[]
            {
                new SettingBlock(T("Hotkeys", "Disabled", Icons.Lock)),
                new CustomBlock("hotkeys.list", "Hotkeys.Bindings"),
            }),
            new PageDef("capture", "SettingsUI.Page.Capture", Icons.Capture, C, new Block[]
            {
                new HeaderBlock("SettingsUI.Header.Detection"),
                new SettingBlock(E(C, "UiDetect", Icons.Search)),
                new SettingBlock(N(C, "UiDetectTimeoutMs", 50, 5000, ms, Icons.Timer) with { Step = 50 }),
                new SettingBlock(E(C, "QrDetect", Icons.Qr)),
                new HeaderBlock("SettingsUI.Header.Display"),
                new SettingBlock(E(C, "GuideLines", Icons.Grid)),
                new SettingBlock(T(C, "ShowCursor", Icons.Cursor)),
                new SettingBlock(T(C, "ShowShortcutHints", Icons.Keys)),
                new SettingBlock(new SettingDef(C, "MaskColor", SettingKind.Color, Icons.Color) { AllowAlpha = true }),
                new SettingBlock(N(C, "SelectionBorderWidth", 1, 10, px, Icons.Border)),
                new SettingBlock(new SettingDef(C, "AspectRatio", SettingKind.Choice, Icons.Ratio)
                {
                    Choices = new[]
                    {
                        new ChoiceOption("", "SettingsUI.Choice.AspectFree"), new ChoiceOption("1:1", "1:1"),
                        new ChoiceOption("4:3", "4:3"), new ChoiceOption("3:2", "3:2"), new ChoiceOption("16:9", "16:9"),
                        new ChoiceOption("16:10", "16:10"), new ChoiceOption("21:9", "21:9"), new ChoiceOption("9:16", "9:16"),
                    },
                }),
                new GroupBlock("SettingsUI.Group.Magnifier", Icons.Zoom, new[]
                {
                    N(C, "MagnifierZoom", 2, 32, times) with { EnabledBy = "MagnifierEnabled" },
                    T(C, "MagnifierGrid") with { EnabledBy = "MagnifierEnabled" },
                    E(C, "MagnifierAltFormat") with { EnabledBy = "MagnifierEnabled" },
                }, T(C, "MagnifierEnabled")),
                new GroupBlock("SettingsUI.Group.OutputStyle", Icons.Image, new[]
                {
                    N(C, "OutputCornerRadius", 0, 64, px),
                    T(C, "OutputShadow"),
                    N(C, "OutputShadowSize", 2, 64, px) with { EnabledBy = "OutputShadow" },
                    T(C, "OutputBorder"),
                    N(C, "OutputBorderWidth", 1, 32, px) with { EnabledBy = "OutputBorder" },
                    new SettingDef(C, "OutputBorderColor", SettingKind.Color) { AllowEmpty = true, AllowAlpha = true, EnabledBy = "OutputBorder" },
                }),
                new HeaderBlock("SettingsUI.Header.Behavior"),
                new SettingBlock(T(C, "ConfirmEscClose", Icons.Close)),
                new SettingBlock(T(C, "PinAlsoCopies", Icons.Copy)),
                new SettingBlock(N(C, "HistoryImageCount", 0, 100, items, Icons.History)),
                new SettingBlock(N(C, "HistoryRegionCount", 0, 100, items, Icons.History)),
                new GroupBlock("SettingsUI.Group.CustomCapture", Icons.Presets, new[]
                {
                    T(C, "CustomDirectCopy"),
                }),
                new CustomBlock("capture.presets", "Capture.CustomPresets"),
            }),
            new PageDef("pin", "SettingsUI.Page.Pin", Icons.Pin, P, new Block[]
            {
                new HeaderBlock("SettingsUI.Header.General"),
                new SettingBlock(T(P, "RestoreOnStartup", Icons.History)),
                new SettingBlock(E(P, "DefaultPosition", Icons.Position)),
                new SettingBlock(T(P, "Topmost", Icons.Topmost)),
                new SettingBlock(T(P, "FitLargeImagesToScreen", Icons.Zoom)),
                new SettingBlock(T(P, "TextSelectableByDefault", Icons.Ocr)),
                new SettingBlock(E(P, "OnDuplicate", Icons.Duplicate)),
                new SettingBlock(E(P, "PasteBehavior", Icons.Paste)),
                new SettingBlock(E(P, "CopySource", Icons.Copy)),
                new SettingBlock(N(P, "HistoryCount", 0, 100, items, Icons.History)),
                new SettingBlock(T(P, "ShowGroupSwitchOverlay", Icons.Group)),
                new HeaderBlock("SettingsUI.Header.Appearance"),
                new SettingBlock(Sl(P, "DefaultOpacity", 10, 100, pct, Icons.Opacity)),
                new GroupBlock("SettingsUI.Group.Shadow", Icons.Shadow, new[]
                {
                    N(P, "ShadowSizePx", 2, 30, px) with { EnabledBy = "ShowShadow" },
                }, T(P, "ShowShadow")),
                new GroupBlock("SettingsUI.Group.PinTitle", Icons.Title, new[]
                {
                    E(P, "TitlePosition") with { EnabledBy = "ShowTitle" },
                }, T(P, "ShowTitle")),
                new GroupBlock("SettingsUI.Group.ZoomOpacity", Icons.Wheel, new[]
                {
                    E(P, "WheelZoom"),
                    N(P, "ZoomStepPercent", 1, 100, pct),
                    N(P, "FineZoomStepPercent", 1, 50, pct),
                    N(P, "OpacityStepPercent", 1, 50, pct),
                    N(P, "ClickThroughOpacity", 0, 100, pct),
                    T(P, "SmoothScaling"),
                    T(P, "ShowZoomIndicator"),
                    T(P, "ResizeByBorder"),
                    N(P, "ThumbnailSizePx", 48, 512, px),
                }),
                new GroupBlock("SettingsUI.Group.PinMagnifier", Icons.Zoom, new[]
                {
                    N(P, "MagnifierZoom", 2, 32, times),
                    E(P, "MagnifierAltFormat"),
                }),
                new GroupBlock("SettingsUI.Group.Confirm", Icons.Warning, new[]
                {
                    T(P, "ConfirmEscClose"),
                    T(P, "ConfirmDoubleClickClose"),
                    T(P, "ConfirmDestroyAll"),
                }),
                new GroupBlock("SettingsUI.Group.TextPin", Icons.Text, new[]
                {
                    new SettingDef(P, "TextFontFamily", SettingKind.Font),
                    N(P, "TextFontSize", 6, 96, pt),
                    new SettingDef(P, "TextForeground", SettingKind.Color) { AllowAlpha = true },
                    new SettingDef(P, "TextBackground", SettingKind.Color) { AllowAlpha = true },
                    N(P, "TextMaxWidth", 100, 4000, px) with { Step = 50 },
                    T(P, "TextShowHtml"),
                }),
            }),
            new PageDef("annotation", "SettingsUI.Page.Annotation", Icons.Annotation, A, new Block[]
            {
                new SettingBlock(T(A, "SharedColor", Icons.Color)),
                new CustomBlock("annotation.palette", "Annotation.Palette"),
                new SettingBlock(T(A, "AllowSecondaryEdit", Icons.Edit)),
                new SettingBlock(T(A, "SerialRenumberOnDelete", Icons.Serial)),
                new HeaderBlock("SettingsUI.Header.ToolMemory"),
                new SettingBlock(T(A, "RememberLastToolInCapture", Icons.Capture)),
                new SettingBlock(T(A, "RememberLastToolInPin", Icons.Pin)),
                new CustomBlock("annotation.toolstate", "Annotation.ToolState"),
            }),
            new PageDef("save", "SettingsUI.Page.Save", Icons.Save, S, new Block[]
            {
                new HeaderBlock("SettingsUI.Header.SaveDialog"),
                new SettingBlock(E(S, "SaveDialogStyle", Icons.Save)),
                new SettingBlock(new SettingDef(S, "ManualSaveFolder", SettingKind.Folder, Icons.Folder) { DefaultFolder = "pictures" }),
                new SettingBlock(new SettingDef(S, "FileNameTemplate", SettingKind.Template, Icons.Template)
                    { TemplateMode = TemplateMode.FileName, ExtensionProperty = "LastExtension" }),
                new SettingBlock(new SettingDef(S, "LastExtension", SettingKind.Choice, Icons.Format) { Choices = SaveExtensions }),
                new SettingBlock(T(S, "RememberLastExtension", Icons.Format)),
                new SettingBlock(T(S, "RememberLastFolder", Icons.Folder)),
                new SettingBlock(T(S, "CopyAfterSave", Icons.Copy)),
                new GroupBlock("SettingsUI.Group.QuickSave", Icons.Save, new[]
                {
                    new SettingDef(S, "QuickSaveFolder", SettingKind.Folder) { DefaultFolder = "pictures" },
                    new SettingDef(S, "QuickSavePathTemplate", SettingKind.Template)
                        { TemplateMode = TemplateMode.FullPath, FolderProperty = "QuickSaveFolder", DefaultFolder = "pictures" },
                    T(S, "QuickSaveNotify"),
                }),
                new GroupBlock("SettingsUI.Group.AutoSave", Icons.History, new[]
                {
                    new SettingDef(S, "AutoSaveFolder", SettingKind.Folder) { DefaultFolder = "autosave", EnabledBy = "AutoSaveEnabled" },
                    new SettingDef(S, "AutoSavePathTemplate", SettingKind.Template)
                    {
                        TemplateMode = TemplateMode.FullPath, FolderProperty = "AutoSaveFolder", DefaultFolder = "autosave",
                        EnabledBy = "AutoSaveEnabled",
                    },
                    N(S, "AutoSaveCleanupDays", 0, 3650, days) with { EnabledBy = "AutoSaveEnabled" },
                }, T(S, "AutoSaveEnabled")),
                new CustomBlock("save.variables"),
                new HeaderBlock("SettingsUI.Header.Format"),
                new SettingBlock(Sl(S, "Quality", 1, 100, null, Icons.Quality)),
                new GroupBlock("SettingsUI.Group.Pdf", Icons.Pdf, new[]
                {
                    E(S, "PdfPaper"),
                    E(S, "PdfOrientation"),
                    N(S, "PdfMarginMm", 0, 50, "SettingsUI.Unit.Mm"),
                    T(S, "PdfFitToPage"),
                    T(S, "PdfAutoPaginate"),
                    N(S, "PdfPageOverlapMm", 0, 50, "SettingsUI.Unit.Mm") with { EnabledBy = "PdfAutoPaginate" },
                    T(S, "PdfUseJpeg"),
                }),
                new SettingBlock(new SettingDef(S, "CopyImageAsFile", SettingKind.Choice, Icons.File)
                {
                    Choices = new[]
                    {
                        new ChoiceOption("Disabled", "SettingsUI.Choice.CopyAsFile.Disabled"),
                        new ChoiceOption("Auto", "SettingsUI.Choice.CopyAsFile.Auto"),
                        new ChoiceOption("Png", "PNG"), new ChoiceOption("Jpg", "JPG"), new ChoiceOption("Bmp", "BMP"),
                    },
                }),
            }),
            new PageDef("ocr", "SettingsUI.Page.Ocr", Icons.Ocr, O, new Block[]
            {
                new CustomBlock("ocr.language", "Ocr.Language"),
                new SettingBlock(E(O, "Layout", Icons.Layout)),
                new SettingBlock(E(O, "Punctuation", Icons.Punctuation)),
                new SettingBlock(T(O, "UpscaleSmallImages", Icons.Upscale)),
                new SettingBlock(T(O, "RemoveCjkSpaces", Icons.Space)),
            }),
            new PageDef("longcapture", "SettingsUI.Page.LongCapture", Icons.LongCapture, L, new Block[]
            {
                new SettingBlock(E(L, "DefaultDirection", Icons.Direction)),
                new SettingBlock(T(L, "AutoCrop", Icons.Edit)),
                new SettingBlock(N(L, "MaxLength", 1000, 200000, px, Icons.Length) with { Step = 1000 }),
                new SettingBlock(N(L, "FramesPerSecond", 5, 60, null, Icons.Fps)),
                new SettingBlock(T(L, "ShowPreview", Icons.Image)),
                new GroupBlock("SettingsUI.Group.AutoScroll", Icons.Wheel, new[]
                {
                    N(L, "AutoScrollIntervalMs", 50, 2000, ms) with { Step = 10 },
                    N(L, "AutoScrollWheelDelta", 30, 1200) with { Step = 30 },
                    N(L, "AutoScrollStopAfterIdleMs", 300, 10000, ms) with { Step = 100 },
                }),
            }),
            new PageDef("recording", "SettingsUI.Page.Recording", Icons.Recording, R, new Block[]
            {
                new SettingBlock(new SettingDef(R, "Fps", SettingKind.Choice, Icons.Fps)
                {
                    Choices = new[] { 5, 16, 24, 30, 60 }.Select(f => new ChoiceOption(f, $"{f} FPS")).ToArray(),
                }),
                new SettingBlock(N(R, "StartDelaySeconds", 0, 60, sec, Icons.Delay)),
                new SettingBlock(N(R, "MaxDurationMinutes", 1, 600, "SettingsUI.Unit.Minutes", Icons.Timer)),
                new SettingBlock(T(R, "ShowCursor", Icons.Cursor)),
                new SettingBlock(T(R, "ExcludeControlsFromCapture", Icons.Capture)),
                new GroupBlock("SettingsUI.Group.RecInput", Icons.Mouse, new[]
                {
                    T(R, "HighlightClicks"),
                    N(R, "ClickAnimationMs", 150, 2000, ms) with { Step = 50 },
                    T(R, "ShowWheel"),
                    T(R, "ShowKeys"),
                    E(R, "KeyFilter") with { EnabledBy = "ShowKeys" },
                }),
                new GroupBlock("SettingsUI.Group.RecAudio", Icons.Mic, new[]
                {
                    T(R, "RecordMicrophone"),
                    T(R, "RecordSystemAudio"),
                }),
                new GroupBlock("SettingsUI.Group.RecWebcam", Icons.Camera, new[]
                {
                    E(R, "WebcamShape") with { EnabledBy = "WebcamEnabled" },
                    E(R, "WebcamCorner") with { EnabledBy = "WebcamEnabled" },
                }, T(R, "WebcamEnabled")),
                new HeaderBlock("SettingsUI.Header.Export"),
                new SettingBlock(new SettingDef(R, "DefaultFormat", SettingKind.Choice, Icons.Format)
                {
                    Choices = new[] { new ChoiceOption("gif", "GIF"), new ChoiceOption("mp4", "MP4") },
                }),
                new SettingBlock(E(R, "Quality", Icons.Quality)),
                new SettingBlock(E(R, "GifDither", Icons.Image)),
                new SettingBlock(new SettingDef(R, "QuickSaveFolder", SettingKind.Folder, Icons.Folder) { DefaultFolder = "pictures" }),
                new SettingBlock(new SettingDef(R, "FileNameTemplate", SettingKind.Template, Icons.Template) { TemplateMode = TemplateMode.FileName }),
            }),
            // Pages whose content is provided by other modules (embedded at merge time, see Views/ExternalPages.cs).
            new PageDef("translate", "SettingsUI.Page.Translate", Icons.Translate, "Translate", new Block[]
            {
                new CustomBlock("translate", "Translate.Provider", "Translate.TargetLanguage", "Translate.FallbackLanguage", "Translate.Mode",
                    "Translate.Proxy", "Translate.ProxyAddress", "Translate.TimeoutSeconds", "Translate.OpenAi", "Translate.Baidu",
                    "Translate.Youdao", "Translate.DeepL"),
            }),
            new PageDef("recognition", "SettingsUI.Page.Recognition", Icons.Recognition, "Recognition", new Block[]
            {
                new HeaderBlock("SettingsUI.Header.Table"),
                new SettingBlock(new SettingDef("Recognition", "TableExportFormat", SettingKind.Choice, Icons.Format)
                {
                    Choices = new[] { new ChoiceOption("xlsx", "Excel (.xlsx)"), new ChoiceOption("csv", "CSV") },
                }),
                new SettingBlock(T("Recognition", "DetectTableLines", Icons.Grid)),
            }),
            new PageDef("about", "SettingsUI.Page.About", Icons.About, null, new Block[]
            {
                new CustomBlock("about"),
            }),
        };
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Finds a page by id (case-insensitive, a few aliases accepted). Null if unknown.</summary>
    public static PageDef? FindPage(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var key = id.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        key = key switch
        {
            "hotkey" or "shortcuts" or "shortcut" or "keys" => "hotkeys",
            "screenshot" => "capture",
            "pins" => "pin",
            "annotate" or "draw" => "annotation",
            "text" or "textrecognition" => "ocr",
            "long" or "scroll" or "scrolling" => "longcapture",
            "record" or "recorder" or "screenrecording" => "recording",
            "translation" or "translator" => "translate",
            "table" or "formula" or "tablerecognition" => "recognition",
            _ => key,
        };
        return Pages.FirstOrDefault(p => p.Id == key);
    }

    /// <summary>The section object type of <c>AppSettings.{section}</c>, or null.</summary>
    public static Type? SectionType(string section) =>
        typeof(AppSettings).GetProperty(section, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;

    /// <summary>All "Section.Property" paths edited by a page's explicit blocks.</summary>
    public static IEnumerable<string> CoveredPaths(PageDef page)
    {
        foreach (var b in page.Blocks)
        {
            switch (b)
            {
                case SettingBlock s: yield return s.Def.Path; break;
                case GroupBlock g:
                    if (g.HeaderToggle != null) yield return g.HeaderToggle.Path;
                    foreach (var d in g.Items) yield return d.Path;
                    break;
                case CustomBlock c:
                    foreach (var p in c.Covers) yield return p;
                    break;
            }
        }
    }

    /// <summary>All explicit setting definitions of a page.</summary>
    public static IEnumerable<SettingDef> Defs(PageDef page)
    {
        foreach (var b in page.Blocks)
        {
            if (b is SettingBlock s) yield return s.Def;
            else if (b is GroupBlock g)
            {
                if (g.HeaderToggle != null) yield return g.HeaderToggle;
                foreach (var d in g.Items) yield return d;
            }
        }
    }

    /// <summary>
    /// Properties of the page's section that no block covers and that are not skipped. Simple ones (bool, numbers,
    /// enums, strings) get an automatic card; complex ones are returned too so tests can flag them.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> UncoveredProperties(PageDef page)
    {
        if (page.Section is null || SectionType(page.Section) is not { } type) return Array.Empty<PropertyInfo>();
        var covered = CoveredPaths(page).ToHashSet(StringComparer.Ordinal);
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => !covered.Contains($"{page.Section}.{p.Name}") && !Skipped.Contains($"{page.Section}.{p.Name}"))
            .ToList();
    }

    public static bool IsSimple(Type t) =>
        t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(float) ||
        t == typeof(string) || t.IsEnum;

    /// <summary>An automatic card for a property without explicit definition (null for complex types).</summary>
    public static SettingDef? FallbackDef(string section, PropertyInfo p)
    {
        var t = p.PropertyType;
        if (!IsSimple(t)) return null;
        var kind = t == typeof(bool) ? SettingKind.Toggle
            : t.IsEnum ? SettingKind.Enum
            : t == typeof(string) ? SettingKind.Text
            : SettingKind.Number;
        return new SettingDef(section, p.Name, kind) { Min = 0, Max = kind == SettingKind.Number ? 1_000_000 : 100 };
    }

    /// <summary>"MaxLengthPx" → "Max length px" (title of automatic cards without a resource string).</summary>
    public static string Humanize(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    /// <summary>Resource key of an enum value's label.</summary>
    public static string EnumKey(Type enumType, string name) => $"SettingsUI.Enum.{enumType.Name}.{name}";

    public static string EnumKey(Enum value) => EnumKey(value.GetType(), value.ToString());

    /// <summary>Enum values with their label keys, in declaration order.</summary>
    public static IReadOnlyList<(object Value, string Key)> EnumOptions(Type enumType) =>
        Enum.GetValues(enumType).Cast<object>().Select(v => (v, EnumKey(enumType, v.ToString()!))).ToList();
}
