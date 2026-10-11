using System.Windows;
using System.Windows.Controls;
using OpenPin.Core;
using OpenPin.Core.Hotkeys;
using OpenPin.Core.Settings;
using OpenPin.SettingsUI.Controls;
using OpenPin.SettingsUI.Logic;

namespace OpenPin.SettingsUI.Views;

/// <summary>Hotkeys page body (SET-02): every registered action with a recorder, conflict hints, reset to defaults.</summary>
internal sealed class HotkeysView : StackPanel
{
    private readonly SettingsSession _session;
    private readonly SettingsWindow _window;
    private readonly Dictionary<string, (HotkeyBox Box, TextBlock Issue)> _rows = new();
    private readonly TextBlock _summary;

    public HotkeysView(SettingsSession session, SettingsWindow window)
    {
        _session = session;
        _window = window;

        _summary = Kit.Hint();
        var reset = Kit.Button("SettingsUI.Hotkeys.ResetDefaults", async (_, _) =>
        {
            var r = await _window.ShowDialogAsync("SettingsUI.Hotkeys.ResetDefaults", Loc.Get("SettingsUI.Hotkeys.ResetConfirm"),
                "SettingsUI.Hotkeys.ResetButton", null, "Common.Cancel");
            if (r != DialogChoice.Primary) return;
            _session.Draft.Hotkeys.Bindings = HotkeySettings.DefaultBindings();
            _session.NotifyEdited();
            Build();
        }, icon: "");
        Children.Add(Kit.Card(SettingsCatalog.Icons.Reset, Kit.TitleBlock("SettingsUI.Hotkeys.ResetDefaults", "SettingsUI.Hotkeys.ResetDefaults.Desc"), reset, _summary));

        EventHandler onReplaced = (_, _) => Build();
        Loaded += (_, _) => { _session.DraftReplaced -= onReplaced; _session.DraftReplaced += onReplaced; Build(); };
        Unloaded += (_, _) => _session.DraftReplaced -= onReplaced;
        Build();
    }

    private static string GroupKey(string actionId) =>
        actionId.StartsWith("capture", StringComparison.Ordinal) ? "SettingsUI.Hotkeys.GroupCapture"
        : actionId.StartsWith("pin", StringComparison.Ordinal) ? "SettingsUI.Hotkeys.GroupPin"
        : "SettingsUI.Hotkeys.GroupApp";

    private void Build()
    {
        while (Children.Count > 1) Children.RemoveAt(1);
        _rows.Clear();
        var actions = AppServices.Actions?.All.ToList() ?? new List<Core.Actions.AppAction>();
        foreach (var group in actions.GroupBy(a => GroupKey(a.Id)))
        {
            Children.Add(Kit.Header(group.Key));
            var container = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, Kit.CardSpacing) };
            container.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            container.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            var rows = new StackPanel();
            container.Child = rows;
            foreach (var action in group)
            {
                var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                title.Children.Add(Kit.Text(action.NameKey));
                var issue = Kit.Hint("", "SystemFillColorCriticalBrush");
                issue.Visibility = Visibility.Collapsed;
                title.Children.Add(issue);

                var box = new HotkeyBox(_session.SuspendHotkeys)
                {
                    HotkeyText = _session.Draft.Hotkeys.Bindings.GetValueOrDefault(action.Id, ""),
                };
                string id = action.Id;
                box.HotkeyChanged += text =>
                {
                    _session.Draft.Hotkeys.Bindings[id] = text;
                    _session.NotifyEdited();
                    UpdateIssues();
                };
                var clear = Kit.IconButton("", "SettingsUI.Hotkeys.Clear", (_, _) =>
                {
                    box.HotkeyText = "";
                    _session.Draft.Hotkeys.Bindings[id] = "";
                    _session.NotifyEdited();
                    UpdateIssues();
                });
                clear.Margin = new Thickness(6, 0, 0, 0);
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                right.Children.Add(box);
                right.Children.Add(clear);

                var row = new Border
                {
                    Padding = new Thickness(16, 8, 12, 8),
                    MinHeight = 52,
                    BorderThickness = new Thickness(0, rows.Children.Count > 0 ? 1 : 0, 0, 0),
                    Child = Kit.Row(null, title, right, null, 0),
                };
                row.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
                rows.Children.Add(row);
                _rows[id] = (box, issue);
            }
            Children.Add(container);
        }
        UpdateIssues();
    }

    private void UpdateIssues()
    {
        var bindings = _rows.Keys.ToDictionary(id => id, id => _session.Draft.Hotkeys.Bindings.GetValueOrDefault(id, ""));
        Func<Hotkey, bool>? probe = AppServices.Hotkeys is { } hs ? hs.IsAvailable : null;
        // Failures from the last registration (also those at app startup) + failures of this window's last apply.
        var failed = _session.FailedHotkeys.Concat(AppServices.Hotkeys?.LastFailed ?? Array.Empty<string>()).ToHashSet();
        var issues = HotkeyConflicts.Analyze(bindings, probe, failed);
        foreach (var (id, (_, text)) in _rows)
        {
            var issue = issues.GetValueOrDefault(id, HotkeyIssue.None);
            text.Text = issue.Kind switch
            {
                HotkeyIssueKind.Duplicate => Loc.Format("SettingsUI.Hotkeys.Duplicate",
                    string.Join("、", issue.OtherActions.Select(o => AppServices.Actions?.Get(o)?.DisplayName ?? o))),
                HotkeyIssueKind.InUse => Loc.Get("SettingsUI.Hotkeys.InUse"),
                HotkeyIssueKind.Invalid => Loc.Get("SettingsUI.Hotkeys.Invalid"),
                HotkeyIssueKind.RegisterFailed => Loc.Get("SettingsUI.Hotkeys.RegisterFailed"),
                _ => "",
            };
            text.Visibility = text.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        int problems = HotkeyConflicts.CountProblems(issues);
        _summary.Text = problems == 0 ? Loc.Get("SettingsUI.Hotkeys.NoConflicts") : Loc.Format("SettingsUI.Hotkeys.ConflictCount", problems);
        _summary.SetResourceReference(TextBlock.ForegroundProperty, problems == 0 ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");
    }
}
