using System.Windows;
using System.Windows.Threading;
using OpenPin.Capture;
using OpenPin.Core;
using OpenPin.Core.Actions;
using OpenPin.Core.Hotkeys;
using OpenPin.Core.Settings;
using OpenPin.Core.Theme;
using OpenPin.Core.Tray;
using OpenPin.LongCapture;
using OpenPin.Pin;
using OpenPin.Recording;
using OpenPin.Services;
using OpenPin.SettingsUI;

namespace OpenPin;

/// <summary>
/// Composition root. Startup order: single instance → logging → settings → modules → actions → hotkeys → tray → pins.
/// Command line: <c>OpenPin.exe --action &lt;id&gt;</c> (ids in <see cref="ActionIds"/>), forwarded to the running instance.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _single;
    private TrayIconHost? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new SingleInstance();
        if (!_single.IsFirstInstance)
        {
            SingleInstance.SendToFirstInstance(e.Args.Length > 0 ? e.Args : new[] { "--already-running" });
            Shutdown();
            return;
        }

        Log.Init();
        Log.Info($"OpenPin {typeof(App).Assembly.GetName().Version} starting. Data: {AppPaths.DataDir}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Unhandled exception", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Unobserved task exception", ev.Exception); ev.SetObserved(); };

        // Settings
        AppServices.Settings = new SettingsService(AppPaths.SettingsFile);
        AppServices.Settings.Load();
        if (Environment.GetEnvironmentVariable("OPENPIN_DATA_DIR") is not { Length: > 0 }) // don't touch the registry from dev/test runs
            AutoStart.Sync(AppServices.Config.General.AutoStart);

        // Language (i18n) before theme and modules
        LanguageManager.Initialize(this, AppServices.Settings);

        // Theme (light / dark + accent) before any window opens
        ThemeManager.Initialize(this, AppServices.Settings);

        // Modules
        AppServices.Actions = new ActionRegistry();
        AppServices.Capture = new CaptureController();
        AppServices.Pins = new PinManager();
        AppServices.LongCapture = new LongCaptureController();
        AppServices.Recorder = new ScreenRecorder();
        AppServices.SettingsUi = new SettingsUi();
        RegisterActions(AppServices.Actions);

        // Hotkeys
        AppServices.Hotkeys = new HotkeyService();
        AppServices.Hotkeys.Triggered += id => AppServices.Actions.Execute(id);
        // OPENPIN_NO_HOTKEYS=1: dev/test runs that must not grab global hotkeys.
        AppServices.Hotkeys.Suspended = AppServices.Config.Hotkeys.Disabled ||
                                        Environment.GetEnvironmentVariable("OPENPIN_NO_HOTKEYS") == "1";
        var failed = AppServices.Hotkeys.ApplyBindings(AppServices.Config.Hotkeys.Bindings);

        // Tray + notifications
        _tray = new TrayIconHost();
        Notify.Initialize(o => _tray.ShowBalloon(o.Title, o.Message, o.IsError));

        if (failed.Count > 0)
            Notify.Error(Loc.Format("Common.HotkeyConflict",
                string.Join(", ", failed.Select(id => $"{AppServices.Actions.Get(id)?.DisplayName ?? id} ({AppServices.Config.Hotkeys.Bindings[id]})"))));
        else if (AppServices.Config.General.ShowStartupNotification)
            Notify.Info(Loc.Format("Common.StartedHint",
                AppServices.Config.Hotkeys.Bindings.GetValueOrDefault(ActionIds.Capture, ""),
                AppServices.Config.Hotkeys.Bindings.GetValueOrDefault(ActionIds.PinClipboard, "")));

        AppServices.Pins.Initialize();

        _single.StartListening(args => Dispatcher.BeginInvoke(() => HandleCommandLine(args)));
        HandleCommandLine(e.Args);
    }

    private static void RegisterActions(ActionRegistry r)
    {
        r.Register(ActionIds.Capture, "Action.Capture", () => AppServices.Capture.Start(CaptureMode.Edit));
        r.Register(ActionIds.CaptureCustom, "Action.CaptureCustom", () => AppServices.Capture.StartCustom());
        r.Register(ActionIds.CaptureCopy, "Action.CaptureCopy", () => AppServices.Capture.Start(CaptureMode.Copy));
        r.Register(ActionIds.CapturePin, "Action.CapturePin", () => AppServices.Capture.Start(CaptureMode.Pin));
        r.Register(ActionIds.CaptureOcr, "Action.CaptureOcr", () => AppServices.Capture.Start(CaptureMode.Ocr));
        r.Register(ActionIds.CaptureSave, "Action.CaptureSave", () => AppServices.Capture.Start(CaptureMode.Save));
        r.Register(ActionIds.CaptureQuickSave, "Action.CaptureQuickSave", () => AppServices.Capture.Start(CaptureMode.QuickSave));
        r.Register(ActionIds.CaptureFullScreenCopy, "Action.CaptureFullScreenCopy", () => AppServices.Capture.CaptureFullScreenToClipboard());
        r.Register(ActionIds.CaptureLastRegionCopy, "Action.CaptureLastRegionCopy", () => AppServices.Capture.CaptureLastRegionToClipboard());
        r.Register(ActionIds.CaptureLong, "Action.CaptureLong", () => AppServices.Capture.Start(CaptureMode.LongCapture));
        r.Register(ActionIds.CaptureRecord, "Action.CaptureRecord", () => AppServices.Capture.Start(CaptureMode.Record));
        r.Register(ActionIds.TranslateMirror, "Action.TranslateMirror", () => Translate.TranslateWorkflow.OpenMirror());

        r.Register(ActionIds.PinClipboard, "Action.PinClipboard", () => AppServices.Pins.PinFromClipboard());
        r.Register(ActionIds.PinFromFile, "Action.PinFromFile", () => AppServices.Pins.PinFromFile());
        r.Register(ActionIds.PinRestore, "Action.PinRestore", () => AppServices.Pins.RestoreLastClosed());
        r.Register(ActionIds.PinCloseAll, "Action.PinCloseAll", () => AppServices.Pins.CloseAll());
        r.Register(ActionIds.PinDestroyAll, "Action.PinDestroyAll", () => AppServices.Pins.DestroyAll());
        r.Register(ActionIds.PinToggleHideAll, "Action.PinToggleHideAll", () => AppServices.Pins.ToggleHideAll());
        r.Register(ActionIds.PinToggleClickThrough, "Action.PinToggleClickThrough", () => AppServices.Pins.ToggleClickThroughUnderCursor());
        r.Register(ActionIds.PinNextGroup, "Action.PinNextGroup", () => AppServices.Pins.SwitchToNextGroup());

        r.Register(ActionIds.ToggleHotkeys, "Action.ToggleHotkeys", () =>
        {
            AppServices.Hotkeys.Suspended = !AppServices.Hotkeys.Suspended;
            AppServices.Config.Hotkeys.Disabled = AppServices.Hotkeys.Suspended;
            AppServices.Settings.Save();
        });
        r.Register(ActionIds.OpenSettings, "Action.OpenSettings", () => AppServices.SettingsUi.Show());
        r.Register(ActionIds.Exit, "Action.Exit", () => Current.Shutdown());
    }

    private static void HandleCommandLine(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--action" or "-a" && i + 1 < args.Length)
                AppServices.Actions.Execute(args[++i]);
            else if (args[i] == "--already-running")
                Notify.Info(Loc.Get("Common.AlreadyRunning"));
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        Notify.Error(Loc.Format("Common.ActionFailed", e.Exception.Message));
        e.Handled = true; // a screenshot tool must not die because one feature failed
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppServices.Pins?.Shutdown(); } catch (Exception ex) { Log.Error("Pins shutdown failed", ex); }
        try { AppServices.Settings?.Save(); } catch (Exception ex) { Log.Error("Settings save failed", ex); }
        try { Notify.DismissAll(); } catch { /* ignore */ }
        AppServices.Hotkeys?.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
        Log.Info("OpenPin exited");
        base.OnExit(e);
    }
}
