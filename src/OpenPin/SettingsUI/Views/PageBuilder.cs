using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OpenPin.Core;
using OpenPin.SettingsUI.Controls;
using OpenPin.SettingsUI.Logic;

namespace OpenPin.SettingsUI.Views;

/// <summary>Renders a <see cref="PageDef"/> from <see cref="SettingsCatalog"/> into cards bound to the session draft.</summary>
internal sealed class PageBuilder
{
    private readonly SettingsSession _session;
    private readonly SettingsWindow _window;
    private static List<string>? _fontNames;

    public PageBuilder(SettingsSession session, SettingsWindow window)
    {
        _session = session;
        _window = window;
    }

    public FrameworkElement Build(PageDef page)
    {
        var panel = new StackPanel();
        foreach (var block in page.Blocks)
        {
            switch (block)
            {
                case HeaderBlock h: panel.Children.Add(Kit.Header(h.TitleKey)); break;
                case SettingBlock s: panel.Children.Add(BuildCard(s.Def)); break;
                case GroupBlock g: panel.Children.Add(BuildGroup(g)); break;
                case CustomBlock c: panel.Children.Add(CustomBlocks.Build(c.Id, _session, _window)); break;
            }
        }

        // Properties of the section without explicit cards (e.g. options added later by other modules).
        if (page.Section != null)
        {
            var extra = SettingsCatalog.UncoveredProperties(page)
                .Select(p => SettingsCatalog.FallbackDef(page.Section, p))
                .OfType<SettingDef>()
                .ToList();
            if (extra.Count > 0)
            {
                panel.Children.Add(Kit.Header("SettingsUI.Header.Other"));
                foreach (var def in extra) panel.Children.Add(BuildCard(def));
            }
        }
        return panel;
    }

    // ------------------------------------------------------------------ cards

    private string Title(SettingDef def) =>
        def.TitleOverride ?? (Kit.HasString(def.TitleKey) ? def.TitleKey : SettingsCatalog.Humanize(def.Property));

    private FrameworkElement TitleBlock(SettingDef def) => Kit.TitleBlock(Title(def), def.DescKey);

    private FrameworkElement BuildCard(SettingDef def)
    {
        var (control, below) = BuildControl(def);
        if (control != null) Kit.SetAutomationName(control, Title(def));
        var card = Kit.Card(def.Icon.Length > 0 ? def.Icon : "", TitleBlock(def), control, below);
        BindEnabled(card, def);
        return card;
    }

    private FrameworkElement BuildGroup(GroupBlock g)
    {
        UIElement? headerControl = null;
        if (g.HeaderToggle != null && BuildControl(g.HeaderToggle).Control is { } hc)
        {
            Kit.SetAutomationName(hc, g.TitleKey);
            headerControl = hc;
        }
        var expander = new ExpanderCard(g.Icon, g.TitleKey, g.DescKey, headerControl, _window.ExpandGroups);
        foreach (var def in g.Items)
        {
            var (control, below) = BuildControl(def);
            if (control != null) Kit.SetAutomationName(control, Title(def));
            var row = expander.AddRow(TitleBlock(def), control, below);
            BindEnabled(row, def);
        }
        return expander;
    }

    private void BindEnabled(FrameworkElement element, SettingDef def)
    {
        if (def.EnabledBy == null) return;
        Kit.Bind(element, UIElement.IsEnabledProperty, _session, $"{def.Section}.{def.EnabledBy}", mode: BindingMode.OneWay);
        // Fluent's disabled look only greys the controls; dim the whole row so titles read as disabled too.
        element.IsEnabledChanged += (_, _) => element.Opacity = element.IsEnabled ? 1 : 0.5;
        element.Opacity = element.IsEnabled ? 1 : 0.5;
    }

    /// <summary>The control for a setting: (right-side control, optional full-width content below the text).</summary>
    public (FrameworkElement? Control, FrameworkElement? Below) BuildControl(SettingDef def)
    {
        var propType = SettingsCatalog.SectionType(def.Section)?.GetProperty(def.Property)?.PropertyType;
        switch (def.Kind)
        {
            case SettingKind.Toggle:
            {
                var t = Kit.Toggle();
                Kit.Bind(t, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, _session, def.Path);
                return (t, null);
            }
            case SettingKind.Number:
            {
                var nb = new NumberBox
                {
                    Minimum = def.Min,
                    Maximum = def.Max,
                    Step = def.Step,
                    IsInteger = propType != typeof(double) && propType != typeof(float),
                };
                Kit.Bind(nb, NumberBox.ValueProperty, _session, def.Path, NumberConverter.Instance);
                return (WithUnit(nb, def.UnitKey), null);
            }
            case SettingKind.Slider:
            {
                var slider = new Slider
                {
                    Minimum = def.Min,
                    Maximum = def.Max,
                    Width = 200,
                    SmallChange = def.Step,
                    LargeChange = Math.Max(def.Step, (def.Max - def.Min) / 10),
                    IsSnapToTickEnabled = true,
                    TickFrequency = def.Step,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Kit.Bind(slider, System.Windows.Controls.Primitives.RangeBase.ValueProperty, _session, def.Path, NumberConverter.Instance);
                var value = new TextBlock { MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                value.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value))
                {
                    Source = slider,
                    StringFormat = def.UnitKey == "SettingsUI.Unit.Percent" ? "{0:0}%" : "{0:0}",
                });
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(value);
                sp.Children.Add(slider);
                return (sp, null);
            }
            case SettingKind.Enum when propType is { IsEnum: true }:
            {
                var cb = Kit.Combo(SettingsCatalog.EnumOptions(propType).Select(o => (o.Value, o.Key)));
                Kit.Bind(cb, System.Windows.Controls.Primitives.Selector.SelectedValueProperty, _session, def.Path);
                return (cb, null);
            }
            case SettingKind.Choice:
            {
                var options = (def.Choices ?? Array.Empty<ChoiceOption>()).Select(o => (o.Value, o.Label)).ToList();
                // Keep unknown values (hand-edited settings.json) selectable.
                var current = GetValue(def);
                if (current != null && !options.Any(o => Equals(o.Value, current)))
                    options.Add((current, current.ToString() ?? ""));
                var cb = Kit.Combo(options);
                Kit.Bind(cb, System.Windows.Controls.Primitives.Selector.SelectedValueProperty, _session, def.Path);
                return (cb, null);
            }
            case SettingKind.Color:
            {
                var box = new ColorBox { AllowEmpty = def.AllowEmpty, AllowAlpha = def.AllowAlpha };
                Kit.Bind(box, ColorBox.ValueProperty, _session, def.Path);
                return (box, null);
            }
            case SettingKind.Font:
            {
                var cb = new ComboBox { IsEditable = true, Width = 240, ItemsSource = FontNames(), IsTextSearchEnabled = true };
                Kit.Bind(cb, ComboBox.TextProperty, _session, def.Path);
                return (cb, null);
            }
            case SettingKind.Folder:
                return (null, FolderBox(def));
            case SettingKind.Template:
                return (null, TemplateBox(def));
            default:
            {
                var tb = new TextBox { Width = 260 };
                Kit.Bind(tb, TextBox.TextProperty, _session, def.Path);
                return (tb, null);
            }
        }
    }

    private object? GetValue(SettingDef def)
    {
        var section = typeof(Core.Settings.AppSettings).GetProperty(def.Section)?.GetValue(_session.Draft);
        return section?.GetType().GetProperty(def.Property)?.GetValue(section);
    }

    private static FrameworkElement WithUnit(FrameworkElement control, string? unitKey)
    {
        if (unitKey is null) return control;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(control);
        var unit = Kit.Text(unitKey, 13, "TextFillColorSecondaryBrush");
        unit.Margin = new Thickness(8, 0, 0, 0);
        unit.MinWidth = 24;
        sp.Children.Add(unit);
        return sp;
    }

    private static List<string> FontNames()
    {
        if (_fontNames != null) return _fontNames;
        var lang = System.Windows.Markup.XmlLanguage.GetLanguage("zh-cn");
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var f in Fonts.SystemFontFamilies)
        {
            try
            {
                names.Add(f.FamilyNames.TryGetValue(lang, out var local) ? local : f.Source);
            }
            catch (Exception ex) { Log.Warn($"Font enumeration: {ex.Message}"); }
        }
        return _fontNames = names.ToList();
    }

    private static string DefaultFolderPath(string? id) =>
        id == "autosave" ? TemplatePreview.DefaultAutoSaveFolder : TemplatePreview.DefaultPicturesFolder;

    private FrameworkElement FolderBox(SettingDef def)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        Kit.Bind(box, TextBox.TextProperty, _session, def.Path);
        var withHint = Kit.WithPlaceholder(box, Loc.Format("SettingsUI.Folder.Default", DefaultFolderPath(def.DefaultFolder)));
        grid.Children.Add(withHint);
        var browse = Kit.Button("SettingsUI.Browse", (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = Loc.Get("SettingsUI.Folder.Pick") };
            var initial = TemplatePreview.ResolveFolder(box.Text, DefaultFolderPath(def.DefaultFolder));
            if (Directory.Exists(initial)) dlg.InitialDirectory = initial;
            if (dlg.ShowDialog(_window) == true) box.Text = dlg.FolderName;
        }, icon: "");
        browse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        return grid;
    }

    private FrameworkElement TemplateBox(SettingDef def)
    {
        var sp = new StackPanel();
        var box = new TextBox { FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI") };
        Kit.Bind(box, TextBox.TextProperty, _session, def.Path);
        sp.Children.Add(box);

        var preview = Kit.Hint();
        preview.Margin = new Thickness(2, 6, 0, 0);
        sp.Children.Add(preview);

        void Update()
        {
            TemplatePreview.SampleTitle = Loc.Get("SettingsUI.SampleWindowTitle");
            var now = DateTime.Now;
            string result;
            if (def.TemplateMode == TemplateMode.FileName)
            {
                var ext = def.ExtensionProperty != null ? GetValue(def with { Property = def.ExtensionProperty }) as string : ".png";
                result = TemplatePreview.FileName(box.Text, ext, now);
            }
            else
            {
                var folder = def.FolderProperty != null ? GetValue(def with { Property = def.FolderProperty }) as string : "";
                result = TemplatePreview.FullPath(folder, DefaultFolderPath(def.DefaultFolder), box.Text, now);
            }
            preview.Text = Loc.Format("SettingsUI.Template.Preview", result);
        }
        box.TextChanged += (_, _) => Update();
        EventHandler onEdited = (_, _) => Update();
        sp.Loaded += (_, _) => { _session.Edited += onEdited; Update(); };
        sp.Unloaded += (_, _) => _session.Edited -= onEdited;
        return sp;
    }
}
