using System.ComponentModel;
using System.Text.Json.Nodes;
using OpenPin.Core;
using OpenPin.Core.Settings;
using OpenPin.SettingsUI.Logic;

namespace OpenPin.SettingsUI;

/// <summary>
/// The settings window's edit model. All controls bind to <c>Draft.…</c> on this object (Source = session), so
/// replacing the draft (reload / import / reset) refreshes every binding at once.
/// <para>Apply model: the user edits a draft; 确定 / 应用 write only the changed values onto the current settings
/// (<see cref="SettingsMerge"/>), then re-register hotkeys, update auto-start and the hotkey suspension.</para>
/// </summary>
internal sealed class SettingsSession : INotifyPropertyChanged, IDisposable
{
    private JsonNode _baseline = new JsonObject();
    private bool _applying;
    private int _hotkeySuspensions;
    private bool _suspendedBefore;

    public SettingsSession()
    {
        Reload();
        AppServices.Settings.Changed += OnSettingsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after <see cref="Draft"/> was replaced (custom blocks rebuild their rows).</summary>
    public event EventHandler? DraftReplaced;

    /// <summary>Raised after any edit (dirty state recomputed).</summary>
    public event EventHandler? Edited;

    public AppSettings Draft { get; private set; } = new();

    public bool IsDirty { get; private set; }

    /// <summary>Action ids whose hotkey failed to register at the last apply.</summary>
    public IReadOnlyList<string> FailedHotkeys { get; private set; } = Array.Empty<string>();

    /// <summary>"Section.Property" paths changed in the draft (for diagnostics / status).</summary>
    public IReadOnlyList<string> ChangedPaths => SettingsMerge.Diff(_baseline, SettingsMerge.ToNode(Draft));

    /// <summary>Discards the draft and starts over from the current settings.</summary>
    public void Reload()
    {
        var draft = AppServices.Settings.Clone();
        SettingsImport.Normalize(draft);
        _baseline = SettingsMerge.ToNode(draft);
        SetDraft(draft);
        UpdateDirty();
    }

    /// <summary>Replaces the draft (import / reset to defaults). Takes effect on apply.</summary>
    public void ReplaceDraft(AppSettings settings)
    {
        SettingsImport.Normalize(settings);
        SetDraft(settings);
        NotifyEdited();
    }

    /// <summary>Call after any change to the draft that did not go through a binding.</summary>
    public void NotifyEdited()
    {
        UpdateDirty();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The settings that apply would write (current settings + the user's changes).</summary>
    public AppSettings BuildMerged() => SettingsMerge.Merge(_baseline, Draft, AppServices.Settings.Current);

    /// <summary>Applies the draft. Returns the action ids whose hotkeys could not be registered.</summary>
    public IReadOnlyList<string> Apply()
    {
        IReadOnlyList<string> failed;
        _applying = true;
        try
        {
            var merged = BuildMerged();
            bool autoStartChanged = merged.General.AutoStart != AppServices.Config.General.AutoStart;
            AppServices.Settings.Replace(merged);

            // Dev/test runs (OPENPIN_DATA_DIR) must never touch the real Run key — same rule as App.OnStartup.
            if (Environment.GetEnvironmentVariable("OPENPIN_DATA_DIR") is not { Length: > 0 } && (autoStartChanged || AutoStart.IsEnabled != merged.General.AutoStart))
                AutoStart.Set(merged.General.AutoStart);

            failed = ApplyHotkeys(merged.Hotkeys);
            FailedHotkeys = failed;
        }
        finally
        {
            _applying = false;
        }
        Reload();
        Log.Info($"Settings applied{(failed.Count > 0 ? $"; hotkeys failed: {string.Join(", ", failed)}" : "")}");
        return failed;
    }

    private IReadOnlyList<string> ApplyHotkeys(HotkeySettings hotkeys)
    {
        var service = AppServices.Hotkeys;
        if (service is null) return Array.Empty<string>();
        bool suspended = hotkeys.Disabled || Environment.GetEnvironmentVariable("OPENPIN_NO_HOTKEYS") == "1";
        if (_hotkeySuspensions > 0)
        {
            // A recorder is active: remember the state to restore when it ends.
            _suspendedBefore = suspended;
            service.ApplyBindings(hotkeys.Bindings);
            return Array.Empty<string>();
        }
        service.Suspended = suspended;
        return service.ApplyBindings(hotkeys.Bindings);
    }

    /// <summary>Temporarily unregisters global hotkeys (while the hotkey recorder listens). Dispose to restore.</summary>
    public IDisposable SuspendHotkeys()
    {
        var service = AppServices.Hotkeys;
        if (service is null) return new Releaser(() => { });
        if (_hotkeySuspensions++ == 0)
        {
            _suspendedBefore = service.Suspended;
            service.Suspended = true;
        }
        return new Releaser(() =>
        {
            if (--_hotkeySuspensions == 0) service.Suspended = _suspendedBefore;
        });
    }

    private void SetDraft(AppSettings draft)
    {
        Draft = draft;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Draft)));
        DraftReplaced?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirty()
    {
        bool dirty = !JsonNode.DeepEquals(_baseline, SettingsMerge.ToNode(Draft));
        if (dirty == IsDirty) return;
        IsDirty = dirty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_applying) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnSettingsChanged(sender, e));
            return;
        }
        // Another part of the app saved settings (tray "disable hotkeys", remembered folders…). Without pending edits
        // simply follow; with pending edits keep them — apply merges onto the then-current settings anyway.
        if (!IsDirty) Reload();
    }

    public void Dispose()
    {
        AppServices.Settings.Changed -= OnSettingsChanged;
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() { _release?.Invoke(); _release = null; }
    }
}
