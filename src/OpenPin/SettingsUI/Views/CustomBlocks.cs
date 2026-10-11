using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using OpenPin.Capture;
using OpenPin.Core;
using OpenPin.Core.Settings;
using OpenPin.Services;
using OpenPin.SettingsUI.Controls;
using OpenPin.SettingsUI.Logic;

namespace OpenPin.SettingsUI.Views;

/// <summary>Hand-written blocks referenced by <see cref="CustomBlock"/> ids in the catalog.</summary>
internal static class CustomBlocks
{
    public static FrameworkElement Build(string id, SettingsSession session, SettingsWindow window) => id switch
    {
        "general.backup" => Backup(session, window),
        "hotkeys.list" => new HotkeysView(session, window),
        "capture.presets" => CapturePresets(session),
        "annotation.palette" => Palette(session),
        "annotation.toolstate" => ToolState(session),
        "save.variables" => TemplateVariables(window),
        "ocr.language" => OcrLanguage(session),
        "about" => AboutView.Build(window),
        "translate" => ExternalPages.CreateTranslateContent(session, window),
        "recognition" => ExternalPages.CreateRecognitionContent(session, window),
        _ => Kit.Hint(id),
    };

    /// <summary>Re-runs <paramref name="build"/> whenever the draft object is replaced.</summary>
    private static ContentControl Rebuilding(SettingsSession session, Func<FrameworkElement> build)
    {
        var host = new ContentControl { Focusable = false, Content = build() };
        EventHandler handler = (_, _) => host.Content = build();
        host.Loaded += (_, _) => { session.DraftReplaced -= handler; session.DraftReplaced += handler; host.Content = build(); };
        host.Unloaded += (_, _) => session.DraftReplaced -= handler;
        return host;
    }

    // ------------------------------------------------------------------ general: import / export / reset (SET-05)

    private static FrameworkElement Backup(SettingsSession session, SettingsWindow window)
    {
        var sp = new StackPanel();
        sp.Children.Add(Kit.Card(SettingsCatalog.Icons.Import, "SettingsUI.Backup.Import", "SettingsUI.Backup.Import.Desc",
            Kit.Button("SettingsUI.Backup.ImportButton", async (_, _) =>
            {
                var dlg = new OpenFileDialog { Filter = Loc.Get("SettingsUI.Backup.Filter"), Title = Loc.Get("SettingsUI.Backup.Import") };
                if (dlg.ShowDialog(window) != true) return;
                string json;
                try { json = await File.ReadAllTextAsync(dlg.FileName); }
                catch (Exception ex)
                {
                    Log.Error("Reading settings file failed", ex);
                    await window.ShowMessageAsync("SettingsUI.Backup.ImportFailed", ex.Message);
                    return;
                }
                if (!SettingsImport.TryRead(json, out var imported, out var error) || imported is null)
                {
                    Log.Warn($"Import of '{dlg.FileName}' rejected: {error}");
                    await window.ShowMessageAsync("SettingsUI.Backup.ImportFailed", Loc.Format("SettingsUI.Backup.Invalid", error));
                    return;
                }
                session.ReplaceDraft(imported);
                window.SetStatus("SettingsUI.Backup.Imported", StatusKind.Info);
            }, icon: SettingsCatalog.Icons.Import)));

        sp.Children.Add(Kit.Card(SettingsCatalog.Icons.Export, "SettingsUI.Backup.Export", "SettingsUI.Backup.Export.Desc",
            Kit.Button("SettingsUI.Backup.ExportButton", async (_, _) =>
            {
                var dlg = new SaveFileDialog
                {
                    Filter = Loc.Get("SettingsUI.Backup.Filter"),
                    FileName = $"OpenPin-settings-{DateTime.Now:yyyyMMdd}.json",
                    Title = Loc.Get("SettingsUI.Backup.Export"),
                };
                if (dlg.ShowDialog(window) != true) return;
                try
                {
                    await File.WriteAllTextAsync(dlg.FileName, SettingsImport.Serialize(session.BuildMerged()));
                    window.SetStatus("SettingsUI.Backup.Exported", StatusKind.Success);
                }
                catch (Exception ex)
                {
                    Log.Error("Exporting settings failed", ex);
                    await window.ShowMessageAsync("SettingsUI.Backup.ExportFailed", ex.Message);
                }
            }, icon: SettingsCatalog.Icons.Export)));

        sp.Children.Add(Kit.Card(SettingsCatalog.Icons.Reset, "SettingsUI.Backup.Reset", "SettingsUI.Backup.Reset.Desc",
            Kit.Button("SettingsUI.Backup.ResetButton", async (_, _) =>
            {
                var r = await window.ShowDialogAsync("SettingsUI.Backup.ResetConfirmTitle", Loc.Get("SettingsUI.Backup.ResetConfirm"),
                    "SettingsUI.Backup.ResetButton", null, "Common.Cancel");
                if (r != DialogChoice.Primary) return;
                session.ReplaceDraft(new AppSettings());
                window.SetStatus("SettingsUI.Backup.ResetDone", StatusKind.Info);
            })));
        return sp;
    }

    // ------------------------------------------------------------------ capture: custom capture presets (CAP-14)

    private static FrameworkElement CapturePresets(SettingsSession session) => Rebuilding(session, () =>
    {
        var list = session.Draft.Capture.CustomPresets;
        var rows = new StackPanel();
        var card = Kit.Card(SettingsCatalog.Icons.Presets, "SettingsUI.Presets", "SettingsUI.Presets.Desc", null, rows);

        void Rebuild()
        {
            rows.Children.Clear();
            if (list.Count > 0)
            {
                var header = PresetGrid();
                string[] keys = { "SettingsUI.Presets.Name", "X", "Y", "SettingsUI.Presets.Width", "SettingsUI.Presets.Height", "SettingsUI.Presets.Delay" };
                for (int i = 0; i < keys.Length; i++)
                {
                    var t = Kit.Text(keys[i], 12, "TextFillColorSecondaryBrush");
                    t.Margin = new Thickness(2, 0, 0, 4);
                    Grid.SetColumn(t, i);
                    header.Children.Add(t);
                }
                rows.Children.Add(header);
            }
            foreach (var preset in list.ToList())
            {
                var g = PresetGrid();
                g.Margin = new Thickness(0, 0, 0, 6);
                var name = new TextBox { Margin = new Thickness(0, 0, 6, 0) };
                Kit.BindTo(name, TextBox.TextProperty, preset, nameof(CustomCapturePreset.Name));
                g.Children.Add(name);
                (string Prop, double Min, double Max)[] nums =
                {
                    (nameof(CustomCapturePreset.X), -100000, 100000), (nameof(CustomCapturePreset.Y), -100000, 100000),
                    (nameof(CustomCapturePreset.Width), 1, 100000), (nameof(CustomCapturePreset.Height), 1, 100000),
                    (nameof(CustomCapturePreset.DelaySeconds), 0, 60),
                };
                for (int i = 0; i < nums.Length; i++)
                {
                    var nb = new NumberBox { Minimum = nums[i].Min, Maximum = nums[i].Max, Width = double.NaN, Margin = new Thickness(0, 0, 6, 0) };
                    Kit.BindTo(nb, NumberBox.ValueProperty, preset, nums[i].Prop, NumberConverter.Instance);
                    Grid.SetColumn(nb, i + 1);
                    g.Children.Add(nb);
                }
                var del = Kit.IconButton("", "SettingsUI.Presets.Remove", (_, _) =>
                {
                    list.Remove(preset);
                    session.NotifyEdited();
                    Rebuild();
                });
                Grid.SetColumn(del, 6);
                g.Children.Add(del);
                rows.Children.Add(g);
            }
            var add = Kit.Button("SettingsUI.Presets.Add", (_, _) =>
            {
                list.Add(new CustomCapturePreset { Name = Loc.Format("SettingsUI.Presets.DefaultName", list.Count + 1) });
                session.NotifyEdited();
                Rebuild();
            }, icon: "");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 4, 0, 0);
            rows.Children.Add(add);
        }
        Rebuild();
        return card;
    });

    private static Grid PresetGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 100 });
        for (int i = 0; i < 5; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 86 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        return g;
    }

    // ------------------------------------------------------------------ annotation palette

    private static FrameworkElement Palette(SettingsSession session) => Rebuilding(session, () =>
    {
        var palette = session.Draft.Annotation.Palette;
        var wrap = new WrapPanel();
        var card = Kit.Card(SettingsCatalog.Icons.Palette, "SettingsUI.Palette", "SettingsUI.Palette.Desc", null, wrap);

        void Rebuild()
        {
            wrap.Children.Clear();
            for (int i = 0; i < palette.Count; i++)
            {
                int index = i;
                var box = new ColorBox(compact: true) { AllowAlpha = true, ShowRemove = true, Value = palette[i], Margin = new Thickness(0, 0, 16, 8) };
                box.SetBinding(ColorBox.ValueProperty, new System.Windows.Data.Binding($"[{index}]")
                {
                    Source = palette,
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    NotifyOnSourceUpdated = true,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                });
                box.RemoveRequested += _ =>
                {
                    palette.RemoveAt(index);
                    session.NotifyEdited();
                    Rebuild();
                };
                wrap.Children.Add(box);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var add = Kit.Button("SettingsUI.Palette.Add", (_, _) =>
            {
                palette.Add("#FF1677FF");
                session.NotifyEdited();
                Rebuild();
            }, icon: "");
            add.IsEnabled = palette.Count < 24;
            buttons.Children.Add(add);
            var reset = Kit.Button("SettingsUI.Palette.Reset", (_, _) =>
            {
                palette.Clear();
                palette.AddRange(new Annotation.AnnotationSettings().Palette);
                session.NotifyEdited();
                Rebuild();
            }, icon: "");
            reset.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(reset);
            wrap.Children.Add(buttons);
        }
        Rebuild();
        return card;
    });

    private static FrameworkElement ToolState(SettingsSession session) => Rebuilding(session, () =>
    {
        var state = session.Draft.Annotation.ToolState;
        var button = Kit.Button("SettingsUI.ToolState.Reset", (_, _) => { }, icon: "");
        var card = Kit.Card(SettingsCatalog.Icons.Undo, Kit.TitleBlock("SettingsUI.ToolState",
            Loc.Format("SettingsUI.ToolState.Desc", state.Count)), button);
        button.IsEnabled = state.Count > 0;
        button.Click += (_, _) =>
        {
            state.Clear();
            session.NotifyEdited();
            button.IsEnabled = false;
        };
        return card;
    });

    // ------------------------------------------------------------------ save: template variable help

    private static FrameworkElement TemplateVariables(SettingsWindow window)
    {
        var exp = new ExpanderCard(SettingsCatalog.Icons.Info, "SettingsUI.Var", "SettingsUI.Var.Desc", null, window.ExpandGroups);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int r = 0;
        foreach (var (token, descKey) in TemplatePreview.Variables)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tok = new TextBox
            {
                Text = token,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 3, 24, 3),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                MinWidth = 0,
                MinHeight = 0,
            };
            tok.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            Grid.SetRow(tok, r);
            grid.Children.Add(tok);
            var d = Kit.Text(descKey, 13, "TextFillColorSecondaryBrush");
            d.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(d, r);
            Grid.SetColumn(d, 1);
            grid.Children.Add(d);
            r++;
        }
        exp.AddContent(grid);
        return exp;
    }

    // ------------------------------------------------------------------ OCR language

    private static FrameworkElement OcrLanguage(SettingsSession session)
    {
        var sp = new StackPanel();
        var languages = OcrService.GetAvailableLanguages();
        var items = new List<(object, string)> { ("", "SettingsUI.Ocr.FollowSystem") };
        items.AddRange(languages.Select(l => ((object)l.Tag, $"{l.DisplayName} ({l.Tag})")));
        var current = session.Draft.Ocr.Language ?? "";
        if (current.Length > 0 && !languages.Any(l => string.Equals(l.Tag, current, StringComparison.OrdinalIgnoreCase)))
            items.Add((current, Loc.Format("SettingsUI.Ocr.NotInstalled", current)));
        var combo = Kit.Combo(items, 240);
        Kit.Bind(combo, System.Windows.Controls.Primitives.Selector.SelectedValueProperty, session, "Ocr.Language");
        sp.Children.Add(Kit.Card(SettingsCatalog.Icons.Language, "SettingsUI.Ocr.Language", "SettingsUI.Ocr.Language.Desc", combo));

        var desc = languages.Count == 0
            ? Loc.Get("SettingsUI.Ocr.NoLanguages")
            : Loc.Format("SettingsUI.Ocr.Installed", string.Join("、", languages.Select(l => l.DisplayName)));
        var hint = Kit.Card(languages.Count == 0 ? SettingsCatalog.Icons.Warning : SettingsCatalog.Icons.Info,
            Kit.TitleBlock("SettingsUI.Ocr.AddLanguage", desc + "\n" + Loc.Get("SettingsUI.Ocr.AddLanguage.Desc")),
            Kit.Button("SettingsUI.Ocr.OpenLanguageSettings", (_, _) => Kit.OpenShell("ms-settings:regionlanguage"), icon: ""));
        sp.Children.Add(hint);
        return sp;
    }
}
