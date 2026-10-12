using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>
/// Image → text / text pin translation result (PRO-01): source on the left, translation on the right (both editable),
/// copy buttons, a target-language switcher that re-translates, and Ctrl+Enter to translate the edited source again.
/// Each source line is one segment, so the translation keeps the line structure. UI thread.
/// </summary>
public sealed class TranslateResultWindow : Window
{
    private readonly TextBox _source = TranslateUi.TextArea();
    private readonly TextBox _target = TranslateUi.TextArea();
    private readonly ComboBox _language = TranslateUi.Combo();
    private readonly TextBlock _detected = TranslateUi.Caption();
    private readonly TextBlock _status = TranslateUi.Caption();
    private readonly Button _translate;
    private CancellationTokenSource? _cts;
    private bool _suppressLanguageEvent;

    /// <summary>Open a window for <paramref name="segments"/> and start translating (non-modal, activated).</summary>
    public static TranslateResultWindow Show(IReadOnlyList<string> segments)
    {
        var w = new TranslateResultWindow(segments);
        w.Show();
        w.Activate();
        w.StartTranslation(explicitTarget: null);
        return w;
    }

    private TranslateResultWindow(IReadOnlyList<string> segments)
    {
        SetResourceReference(TitleProperty, "Translate.Window.Title");
        TranslateUi.SetupWindow(this, 920, 520);

        _source.Text = string.Join(Environment.NewLine, segments.Select(s => s.Replace("\r\n", " ").Replace('\n', ' ').Trim()));

        // ---- top bar: target language + translate
        foreach (var l in TranslateLanguages.All) _language.Items.Add(new ComboBoxItem { Content = l.DisplayName, Tag = l.Tag });
        _language.Width = 150;
        _language.Margin = new Thickness(8, 0, 8, 0);
        _language.SelectionChanged += (_, _) =>
        {
            if (_suppressLanguageEvent || _language.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            StartTranslation(tag);
        };
        _translate = TranslateUi.Button("Translate.Window.Retranslate", accent: true, () => StartTranslation(SelectedTarget), "");
        _translate.ToolTip = "Ctrl+Enter";

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        var provider = TranslateUi.Caption(text: Loc.Format("Translate.Window.Provider", TranslationService.ProviderName(TranslationService.Settings.Provider)));
        DockPanel.SetDock(provider, Dock.Right);
        top.Children.Add(provider);
        top.Children.Add(TranslateUi.Caption("Translate.Window.TargetLabel"));
        top.Children.Add(_language);
        top.Children.Add(_translate);

        // ---- two cards
        var left = BuildCard("Translate.Window.Source", _source, _detected, () => Copy(_source));
        var right = BuildCard("Translate.Window.Translation", _target, null, () => Copy(_target));
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        split.Children.Add(left);
        split.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        _status.Margin = new Thickness(2, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(split);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                StartTranslation(SelectedTarget);
            }
        };
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>The (possibly edited) translation.</summary>
    public string TranslatedText => _target.Text;

    private string? SelectedTarget => _language.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : null;

    private static Border BuildCard(string headerKey, TextBox text, TextBlock? extra, Action copy)
    {
        var header = new DockPanel { Margin = new Thickness(12, 6, 6, 0), LastChildFill = false };
        var copyButton = TranslateUi.IconButton("", "Translate.Window.Copy", copy);
        DockPanel.SetDock(copyButton, Dock.Right);
        header.Children.Add(copyButton);
        header.Children.Add(TranslateUi.Header(headerKey));
        if (extra != null)
        {
            extra.Margin = new Thickness(8, 0, 0, 0);
            header.Children.Add(extra);
        }
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(text);
        return TranslateUi.Card(panel);
    }

    private void Copy(TextBox box)
    {
        var text = box.SelectionLength > 0 ? box.SelectedText : box.Text;
        if (TranslateUi.TryCopyText(text)) TranslateUi.SetStatus(_status, Loc.Get("Translate.Window.Copied"));
    }

    /// <summary>Translate the current source text (one segment per line).</summary>
    public async void StartTranslation(string? explicitTarget)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var segments = TranslationHttp.Lines(_source.Text).Select(l => l.Trim()).ToList();
        while (segments.Count > 0 && segments[^1].Length == 0) segments.RemoveAt(segments.Count - 1);
        if (segments.All(string.IsNullOrWhiteSpace))
        {
            _target.Text = "";
            TranslateUi.SetStatus(_status, Loc.Get("Translate.NoText"));
            return;
        }
        if (!TranslationService.IsConfigured(TranslationService.Settings))
        {
            TranslateUi.SetStatus(_status, Loc.Get("Translate.NotConfigured"), error: true);
            TranslationService.EnsureConfigured();
            return;
        }

        _translate.IsEnabled = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        TranslateUi.SetStatus(_status, Loc.Get("Translate.Window.Working"));
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (cts.IsCancellationRequested) return;
            TranslateUi.SetStatus(_status, Loc.Format("Translate.Window.WorkingElapsed", stopwatch.Elapsed.TotalSeconds));
        };
        timer.Start();

        try
        {
            var outcome = await TranslationService.TranslateAsync(segments, explicitTarget, ct: cts.Token);
            timer.Stop();
            if (cts.IsCancellationRequested) return;
            _target.Text = string.Join(Environment.NewLine, outcome.Segments);
            _detected.Text = outcome.DetectedSource is { } d && TranslateLanguages.Find(d) is { } dl ? Loc.Format("Translate.Window.Detected", dl.DisplayName) : "";
            SelectLanguage(outcome.TargetLanguage);
            TranslateUi.SetStatus(_status, Loc.Format("Translate.Window.Done", outcome.Segments.Count(s => s.Length > 0), outcome.Elapsed.TotalSeconds));
        }
        catch (OperationCanceledException) { timer.Stop(); /* superseded or closed */ }
        catch (TranslationException ex)
        {
            timer.Stop();
            if (!cts.IsCancellationRequested) TranslateUi.SetStatus(_status, Loc.Format("Translate.Failed", ex.Message), error: true);
        }
        catch (Exception ex)
        {
            timer.Stop();
            Log.Error("Translation failed", ex);
            if (!cts.IsCancellationRequested) TranslateUi.SetStatus(_status, Loc.Format("Translate.Failed", ex.Message), error: true);
        }
        finally
        {
            timer.Stop();
            if (ReferenceEquals(_cts, cts)) _translate.IsEnabled = true;
        }
    }

    private void SelectLanguage(string tag)
    {
        _suppressLanguageEvent = true;
        try
        {
            foreach (ComboBoxItem item in _language.Items)
                if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) { _language.SelectedItem = item; break; }
        }
        finally { _suppressLanguageEvent = false; }
    }
}
