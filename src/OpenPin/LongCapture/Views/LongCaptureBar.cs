using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenPin.Core;
using OpenPin.Core.Native;

namespace OpenPin.LongCapture.Views;

/// <summary>
/// Win11-style floating control bar (LNG-03): 开始/停止, 方向, 自动裁剪, 自动滚动 | 裁剪 | 贴图, 保存, 快速保存, 复制 | 关闭,
/// plus size / status text. Never activates (the user keeps scrolling the app below) and is excluded from capture.
/// </summary>
internal sealed class LongCaptureBar : Window
{
    /// <summary>Transparent room around the toolbar for its shadow (DIPs).</summary>
    public const double Pad = 10;

    // Segoe Fluent Icons / MDL2 code points
    private const string GlyphPlay = "";
    private const string GlyphStop = "";
    private const string GlyphDown = "";
    private const string GlyphRight = "";
    private const string GlyphAutoScroll = "";
    private const string GlyphCrop = "";
    private const string GlyphPin = "";
    private const string GlyphSave = "";
    private const string GlyphQuickSave = "";
    private const string GlyphCopy = "";
    private const string GlyphClose = "";

    private readonly Run _sizeRun = new();
    private readonly Run _statusRun = new();

    public LongCaptureBar()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Title = "OpenPin long capture";

        StartStop = IconButton(GlyphPlay, "LongCapture.Start");
        Direction = IconButton(GlyphDown, "LongCapture.DirectionVertical");
        AutoCrop = new ToggleButton
        {
            Content = Loc.Get("LongCapture.AutoCrop"),
            ToolTip = Loc.Get("LongCapture.AutoCropTip"),
            Style = (Style)FindResource("Style.LongCapture.TextToggle"),
        };
        AutoScroll = IconToggle(GlyphAutoScroll, "LongCapture.AutoScroll");
        Crop = IconToggle(GlyphCrop, "LongCapture.Crop");
        PinButton = IconButton(GlyphPin, "LongCapture.Pin");
        SaveButton = IconButton(GlyphSave, "LongCapture.Save");
        QuickSaveButton = IconButton(GlyphQuickSave, "LongCapture.QuickSave");
        CopyButton = IconButton(GlyphCopy, "LongCapture.Copy");
        CloseButton = IconButton(GlyphClose, "LongCapture.Close");

        var info = new TextBlock { Style = (Style)FindResource("Style.LongCapture.Info"), Width = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        info.Inlines.Add(_sizeRun);
        info.Inlines.Add(new LineBreak());
        info.Inlines.Add(_statusRun);
        _sizeRun.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var e in new UIElement[]
                 {
                     StartStop, Direction, AutoCrop, AutoScroll, Separator(), Crop, Separator(),
                     PinButton, SaveButton, QuickSaveButton, CopyButton, Separator(), info, CloseButton,
                 })
            row.Children.Add(e);
        var border = new Border { Style = (Style)FindResource("Style.ToolbarBorder"), Child = row };
        Content = new Grid { Margin = new Thickness(Pad), Children = { border } };

        SourceInitialized += (_, _) =>
        {
            WindowHelper.SetToolWindow(this, true);
            WindowHelper.SetNoActivate(this, true);
            WindowHelper.SetExcludeFromCapture(this, true);
        };
    }

    public Button StartStop { get; }
    public Button Direction { get; }
    public ToggleButton AutoCrop { get; }
    public ToggleButton AutoScroll { get; }
    public ToggleButton Crop { get; }
    public Button PinButton { get; }
    public Button SaveButton { get; }
    public Button QuickSaveButton { get; }
    public Button CopyButton { get; }
    public Button CloseButton { get; }

    public void SetCapturing(bool capturing)
    {
        StartStop.Content = capturing ? GlyphStop : GlyphPlay;
        StartStop.ToolTip = Loc.Get(capturing ? "LongCapture.Stop" : "LongCapture.Start");
        if (capturing) StartStop.SetResourceReference(ForegroundProperty, "Brush.Danger");
        else StartStop.SetResourceReference(ForegroundProperty, "Brush.Accent");
    }

    public void SetDirection(LongCaptureDirection direction)
    {
        bool vertical = direction == LongCaptureDirection.Vertical;
        Direction.Content = vertical ? GlyphDown : GlyphRight;
        Direction.ToolTip = Loc.Get(vertical ? "LongCapture.DirectionVertical" : "LongCapture.DirectionHorizontal");
    }

    public void SetInfo(string size, string status, bool error)
    {
        _sizeRun.Text = size;
        _statusRun.Text = status;
        _statusRun.SetResourceReference(TextElement.ForegroundProperty, error ? "Brush.Danger" : "Brush.TextSecondary");
    }

    /// <summary>Output buttons (pin / save / copy …) are disabled while the result is being composed.</summary>
    public void SetBusy(bool busy)
    {
        foreach (var b in new ButtonBase[] { StartStop, PinButton, SaveButton, QuickSaveButton, CopyButton, Crop })
            b.IsEnabled = !busy;
    }

    private Button IconButton(string glyph, string tipKey) => new()
    {
        Content = glyph,
        ToolTip = Loc.Get(tipKey),
        Style = (Style)FindResource("Style.IconButton"),
    };

    private ToggleButton IconToggle(string glyph, string tipKey) => new()
    {
        Content = glyph,
        ToolTip = Loc.Get(tipKey),
        Style = (Style)FindResource("Style.IconButton"),
    };

    private Rectangle Separator() => new() { Style = (Style)FindResource("Style.ToolbarSeparator") };
}
