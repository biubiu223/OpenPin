using System.Windows;
using System.Windows.Controls;
using OpenPin.Core;

namespace OpenPin.Services;

/// <summary>QR / barcode result window (SRV-07): list of decoded contents with copy and open-link (http/https only). UI thread.</summary>
public sealed class QrResultWindow : Window
{
    private readonly TextBlock _status;

    /// <summary>Open a result window (non-modal, activated).</summary>
    public static QrResultWindow Show(IReadOnlyList<QrCodeResult> results)
    {
        var w = new QrResultWindow(results);
        w.Show();
        w.Activate();
        return w;
    }

    private QrResultWindow(IReadOnlyList<QrCodeResult> results)
    {
        SetResourceReference(TitleProperty, "Services.QrTitle");
        ServicesUi.SetupDialog(this, 520, results.Count > 1 ? 460 : 320);

        _status = ServicesUi.Label(results.Count == 0 ? Loc.Get("Services.QrNone") : Loc.Format("Services.QrCount", results.Count));

        var list = new StackPanel();
        foreach (var r in results) list.Children.Add(BuildItem(r));

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        if (results.Count > 1)
        {
            var copyAll = ServicesUi.Button("Services.QrCopyAll", accent: false,
                (_, _) => Copied(ServicesUi.TryCopyText(string.Join(Environment.NewLine, results.Select(r => r.Text)))), "");
            DockPanel.SetDock(copyAll, Dock.Right);
            bottom.Children.Add(copyAll);
        }
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private UIElement BuildItem(QrCodeResult r)
    {
        var format = ServicesUi.Label(r.Format.Replace('_', ' '), secondary: true, size: 11);
        var text = new TextBox
        {
            Text = r.Text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 8),
        };
        text.SetResourceReference(StyleProperty, "Style.Services.TextBox");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (r.IsUrl)
        {
            var open = ServicesUi.Button("Services.QrOpenLink", accent: true, (_, _) => ServicesUi.OpenUrl(r.Text), "");
            open.Margin = new Thickness(0, 0, 8, 0);
            open.ToolTip = r.Text;
            buttons.Children.Add(open);
        }
        buttons.Children.Add(ServicesUi.Button("Services.QrCopy", accent: !r.IsUrl, (_, _) => Copied(ServicesUi.TryCopyText(r.Text)), ""));

        var stack = new StackPanel();
        stack.Children.Add(format);
        stack.Children.Add(text);
        stack.Children.Add(buttons);
        var card = ServicesUi.Card(stack, new Thickness(12));
        card.Margin = new Thickness(0, 0, 0, 10);
        return card;
    }

    private void Copied(bool ok)
    {
        if (ok) _status.Text = Loc.Get("Services.Copied");
    }
}
