using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Pin.Model;

namespace OpenPin.Pin.Views;

/// <summary>Operations the group dialogs / overlay need from the pin manager.</summary>
internal interface IPinGroupHost
{
    PinGroupBook Groups { get; }
    (int OnScreen, int Total) CountGroup(string groupId);
    void CreateGroupInteractive();
    bool RenameGroup(string id, string name);
    void SetGroupColor(string id, string? color);
    void DeleteGroup(string id, string? moveToId);
    void ClearGroup(string id);
    void MoveGroup(string id, int delta);
    void SwitchGroup(string id, bool showOverlay);
}

/// <summary>Pin group management: rename, color, delete (optionally moving pins), clear, reorder, switch (PIN-11).</summary>
internal sealed class ManageGroupsDialog : PinDialog
{
    private readonly IPinGroupHost _host;
    private readonly ListBox _list = new() { Width = 300, Height = 280 };
    private readonly List<Button> _needsSelection = new();
    private Button? _delete, _up, _down;

    public ManageGroupsDialog(IPinGroupHost host) : base(Loc.Get("Pin.Group.ManageTitle"))
    {
        _host = host;
        _list.SetResourceReference(StyleProperty, "Style.Pin.ListBox");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => { if (SelectedId is { } id) { _host.SwitchGroup(id, false); Refresh(); } };

        var buttons = new StackPanel { Margin = new Thickness(12, 0, 0, 0), MinWidth = 120 };
        Add(buttons, "Pin.Group.New", () => { _host.CreateGroupInteractive(); Refresh(); }, needsSelection: false);
        Add(buttons, "Pin.Group.SwitchTo", () => { _host.SwitchGroup(SelectedId!, false); Refresh(); });
        Add(buttons, "Pin.Group.Rename", Rename);
        Add(buttons, "Pin.Group.ChangeColor", ChangeColor);
        _up = Add(buttons, "Pin.Group.MoveUp", () => { _host.MoveGroup(SelectedId!, -1); Refresh(); });
        _down = Add(buttons, "Pin.Group.MoveDown", () => { _host.MoveGroup(SelectedId!, 1); Refresh(); });
        Add(buttons, "Pin.Group.Clear", Clear);
        _delete = Add(buttons, "Pin.Group.Delete", Delete);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_list);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        Body = grid;
        AddButton(Loc.Get("Common.Close"), true, () => DialogResult = true, isDefault: true);
        Refresh();
    }

    private string? SelectedId => (_list.SelectedItem as ListBoxItem)?.Tag as string;

    private Button Add(Panel panel, string key, Action onClick, bool needsSelection = true)
    {
        var b = new Button { Content = Loc.Get(key), Height = 32, Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Center };
        b.SetResourceReference(StyleProperty, "Style.Pin.Button");
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Log.Error($"Group action {key} failed", ex); }
        };
        panel.Children.Add(b);
        if (needsSelection) _needsSelection.Add(b);
        return b;
    }

    private void Refresh()
    {
        var keep = SelectedId ?? _host.Groups.CurrentId;
        _list.Items.Clear();
        foreach (var g in _host.Groups.Groups)
        {
            var (on, total) = _host.CountGroup(g.Id);
            var row = new DockPanel { LastChildFill = true };
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            if (g.Color != null && ColorText.TryParse(g.Color, out var c)) dot.Background = BitmapUtil.Frozen(new SolidColorBrush(c));
            else { dot.SetResourceReference(Border.BorderBrushProperty, "Brush.TextSecondary"); dot.BorderThickness = new Thickness(1); }
            row.Children.Add(dot);
            var count = new TextBlock { Text = $"{on}/{total}", Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            var name = new TextBlock
            {
                Text = g.Id == _host.Groups.CurrentId ? $"{g.Name}  ·  {Loc.Get("Pin.Group.CurrentMark")}" : g.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = g.Id == _host.Groups.CurrentId ? FontWeights.SemiBold : FontWeights.Normal,
            };
            row.Children.Add(name);
            var item = new ListBoxItem { Content = row, Tag = g.Id, ToolTip = Loc.Format("Pin.Group.CountTip", on, total) };
            item.SetResourceReference(StyleProperty, "Style.Pin.ListBoxItem");
            _list.Items.Add(item);
            if (g.Id == keep) _list.SelectedItem = item;
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var id = SelectedId;
        foreach (var b in _needsSelection) b.IsEnabled = id != null;
        int idx = _list.SelectedIndex;
        if (_delete != null) _delete.IsEnabled = id != null && id != PinGroupBook.DefaultGroupId;
        if (_up != null) _up.IsEnabled = idx > 1;
        if (_down != null) _down.IsEnabled = idx > 0 && idx < _list.Items.Count - 1;
    }

    private void Rename()
    {
        var id = SelectedId!;
        var g = _host.Groups.Find(id);
        if (g == null) return;
        var d = new InputDialog(Loc.Get("Pin.Group.Rename"), Loc.Get("Pin.Group.Name"), g.Name,
            text => !_host.Groups.IsNameAvailable(text, id)
                ? Loc.Get(PinGroupBook.NormalizeName(text).Length == 0 ? "Pin.Group.NameEmpty" : "Pin.Group.NameTaken")
                : null) { Owner = this };
        if (d.ShowDialog() == true) _host.RenameGroup(id, d.Value);
        Refresh();
    }

    private void ChangeColor()
    {
        var id = SelectedId!;
        var g = _host.Groups.Find(id);
        if (g == null) return;
        var d = new ColorDialog(g.Color) { Owner = this };
        if (d.ShowDialog() == true) _host.SetGroupColor(id, d.Color);
        Refresh();
    }

    private void Clear()
    {
        var id = SelectedId!;
        var g = _host.Groups.Find(id);
        if (g == null) return;
        if (!Confirm(Loc.Get("Pin.Group.Clear"), Loc.Format("Pin.Group.ClearConfirm", g.Name))) return;
        _host.ClearGroup(id);
        Refresh();
    }

    private void Delete()
    {
        var id = SelectedId!;
        var g = _host.Groups.Find(id);
        if (g == null || id == PinGroupBook.DefaultGroupId) return;
        var (_, total) = _host.CountGroup(id);
        var d = new DeleteGroupDialog(_host.Groups, g, total) { Owner = this };
        if (d.ShowDialog() != true) return;
        _host.DeleteGroup(id, d.MoveToId);
        Refresh();
    }

    private sealed class ColorDialog : PinDialog
    {
        private readonly Func<string?> _get;

        public ColorDialog(string? initial) : base(Loc.Get("Pin.Group.ChangeColor"))
        {
            var (panel, get) = ColorPicker(initial);
            _get = get;
            Body = panel;
            AddButton(Loc.Get("Common.OK"), true, () => DialogResult = true, isDefault: true);
            AddButton(Loc.Get("Common.Cancel"), false, () => DialogResult = false);
        }

        public string? Color => _get();
    }
}
