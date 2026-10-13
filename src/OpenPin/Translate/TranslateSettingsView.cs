using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>
/// Settings page content for translation (PRO-01), to be embedded by the settings window:
/// <code>var view = new TranslateSettingsView(draft.Translate);   // or DataContext = draft.Translate
/// view.Changed += (_, _) => MarkDirty();</code>
/// Every edit is written straight into the bound <see cref="TranslateSettings"/> (keys DPAPI-encrypted); the host
/// persists it (Apply / SettingsService.Replace). "Test connection" uses the bound (unsaved) values.
/// </summary>
public sealed class TranslateSettingsView : UserControl
{
    private static readonly (string Id, string Name, string BaseUrl, string Model)[] Presets =
    {
        ("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
        ("siliconflow", "硅基流动 (SiliconFlow)", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3"),
        ("qwen", "通义千问 (DashScope)", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ("kimi", "Kimi (Moonshot)", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        ("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ("doubao", "豆包 (火山方舟)", "https://ark.cn-beijing.volces.com/api/v3", "doubao-pro-32k"),
        ("stepfun", "阶跃星辰 (StepFun)", "https://api.stepfun.com/v1", "step-1-8k"),
        ("yi", "零一万物 (01.AI)", "https://api.lingyiwanwu.com/v1", "yi-lightning"),
        ("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
        ("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "deepseek/deepseek-chat"),
        ("groq", "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
        ("ollama", "Ollama", "http://localhost:11434/v1", "qwen2.5:7b"),
        ("lmstudio", "LM Studio", "http://localhost:1234/v1", "local-model"),
        ("custom", "", "", ""),
    };

    private TranslateSettings _s = new();
    private bool _loading;

    private readonly ComboBox _provider = TranslateUi.Combo();
    private readonly StackPanel _openAiPanel = new();
    private readonly StackPanel _baiduPanel = new();
    private readonly StackPanel _youdaoPanel = new();
    private readonly StackPanel _deeplPanel = new();
    private readonly ComboBox _preset = TranslateUi.Combo();
    private readonly TextBox _baseUrl = Input();
    private readonly ComboBox _model = ModelBox();
    private readonly Button _fetchModels;
    private readonly TextBlock _modelStatus = TranslateUi.Caption();
    private CancellationTokenSource? _modelsCts;
    private readonly SecretBox _openAiKey = new();
    private readonly TextBox _baiduAppId = Input();
    private readonly SecretBox _baiduSecret = new();
    private readonly TextBox _youdaoAppKey = Input();
    private readonly SecretBox _youdaoSecret = new();
    private readonly SecretBox _deeplKey = new();
    private readonly ComboBox _target = TranslateUi.Combo();
    private readonly ComboBox _fallback = TranslateUi.Combo();
    private readonly RadioButton _modeText = Radio("Translate.Settings.ModeText");
    private readonly RadioButton _modeImage = Radio("Translate.Settings.ModeImage");
    private readonly ComboBox _proxy = TranslateUi.Combo();
    private readonly TextBox _proxyAddress = Input();
    private readonly TextBox _timeout = Input();
    private readonly Button _test;
    private readonly TextBlock _testStatus = TranslateUi.Caption();
    private CancellationTokenSource? _testCts;

    /// <summary>Raised after any value was written into the bound settings.</summary>
    public event EventHandler? Changed;

    public TranslateSettingsView() : this(null) { }

    public TranslateSettingsView(TranslateSettings? settings)
    {
        SetResourceReference(FontFamilyProperty, "Font.UI");
        SetResourceReference(ForegroundProperty, "Brush.Text");

        // ---- provider
        AddItem(_provider, "", Loc.Get("Translate.Provider.none"));
        foreach (var id in TranslationService.ProviderIds) AddItem(_provider, id, Loc.Get("Translate.Settings.ProviderLong." + id));
        _provider.SelectionChanged += (_, _) => Edit(() => { _s.Provider = TagOf(_provider); UpdateVisibility(); });

        foreach (var p in Presets) AddItem(_preset, p.Id, p.Id == "custom" ? Loc.Get("Translate.Settings.PresetCustom") : p.Name);
        _preset.SelectionChanged += (_, _) => Edit(() =>
        {
            var id = TagOf(_preset);
            _s.OpenAi.Preset = id;
            var p = Presets.FirstOrDefault(x => x.Id == id);
            if (p.Id != null && p.Id != "custom")
            {
                _s.OpenAi.BaseUrl = p.BaseUrl;
                _s.OpenAi.Model = p.Model;
                _loading = true;
                try { _baseUrl.Text = p.BaseUrl; _model.Items.Clear(); _model.Text = p.Model; UpdateModelReasoningHint(p.Model); }
                finally { _loading = false; }
            }
        });
        _baseUrl.TextChanged += (_, _) => Edit(() => { _s.OpenAi.BaseUrl = _baseUrl.Text.Trim(); _model.Items.Clear(); });
        _model.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Edit(() =>
        {
            var text = (_model.Text ?? "").Trim();
            _s.OpenAi.Model = text;
            UpdateModelReasoningHint(text);
        })));
        _model.DropDownOpened += (_, _) => { if (_model.Items.Count == 0 && !_loading) FetchModels(); };
        _fetchModels = TranslateUi.Button("Translate.Settings.FetchModels", accent: false, FetchModels, "\uE72C");
        _fetchModels.Margin = new Thickness(8, 0, 0, 0);
        _openAiKey.Changed += v => Edit(() => { _s.OpenAi.ApiKeyProtected = SecretProtector.Protect(v.Trim()); _model.Items.Clear(); });
        _baiduAppId.TextChanged += (_, _) => Edit(() => _s.Baidu.AppId = _baiduAppId.Text.Trim());
        _baiduSecret.Changed += v => Edit(() => _s.Baidu.SecretProtected = SecretProtector.Protect(v.Trim()));
        _youdaoAppKey.TextChanged += (_, _) => Edit(() => _s.Youdao.AppKey = _youdaoAppKey.Text.Trim());
        _youdaoSecret.Changed += v => Edit(() => _s.Youdao.SecretProtected = SecretProtector.Protect(v.Trim()));
        _deeplKey.Changed += v => Edit(() => _s.DeepL.ApiKeyProtected = SecretProtector.Protect(v.Trim()));

        _openAiPanel.Children.Add(Row("Translate.Settings.Preset", _preset));
        _openAiPanel.Children.Add(Row("Translate.Settings.BaseUrl", _baseUrl));
        var modelRow = new DockPanel();
        DockPanel.SetDock(_fetchModels, Dock.Right);
        modelRow.Children.Add(_fetchModels);
        modelRow.Children.Add(_model);
        _openAiPanel.Children.Add(Row("Translate.Settings.Model", modelRow));
        _modelStatus.Margin = new Thickness(110, -2, 0, 8);
        _modelStatus.Visibility = Visibility.Collapsed;
        _openAiPanel.Children.Add(_modelStatus);
        _openAiPanel.Children.Add(Row("Translate.Settings.ApiKey", _openAiKey));
        _openAiPanel.Children.Add(Hint("Translate.Settings.OpenAiHint"));
        _baiduPanel.Children.Add(Row("Translate.Settings.BaiduAppId", _baiduAppId));
        _baiduPanel.Children.Add(Row("Translate.Settings.BaiduSecret", _baiduSecret));
        _baiduPanel.Children.Add(Hint("Translate.Settings.BaiduHint"));
        _youdaoPanel.Children.Add(Row("Translate.Settings.YoudaoAppKey", _youdaoAppKey));
        _youdaoPanel.Children.Add(Row("Translate.Settings.YoudaoSecret", _youdaoSecret));
        _youdaoPanel.Children.Add(Hint("Translate.Settings.YoudaoHint"));
        _deeplPanel.Children.Add(Row("Translate.Settings.DeepLKey", _deeplKey));
        _deeplPanel.Children.Add(Hint("Translate.Settings.DeepLHint"));

        // ---- languages
        AddItem(_target, "", Loc.Get("Translate.Lang.FollowApp"));
        AddItem(_fallback, "none", Loc.Get("Translate.Lang.None"));
        foreach (var l in TranslateLanguages.All)
        {
            AddItem(_target, l.Tag, l.DisplayName);
            AddItem(_fallback, l.Tag, l.DisplayName);
        }
        _target.SelectionChanged += (_, _) => Edit(() => _s.TargetLanguage = TagOf(_target));
        _fallback.SelectionChanged += (_, _) => Edit(() => _s.FallbackLanguage = TagOf(_fallback));

        // ---- mode
        _modeText.GroupName = _modeImage.GroupName = "OpenPin.Translate.Mode." + Guid.NewGuid().ToString("N");
        _modeText.Checked += (_, _) => Edit(() => _s.Mode = TranslateMode.ImageToText);
        _modeImage.Checked += (_, _) => Edit(() => _s.Mode = TranslateMode.ImageToImage);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        modes.Children.Add(_modeText);
        modes.Children.Add(_modeImage);

        // ---- network
        foreach (var m in Enum.GetValues<TranslateProxyMode>()) AddItem(_proxy, m.ToString(), Loc.Get("Translate.Settings.Proxy." + m));
        _proxy.SelectionChanged += (_, _) => Edit(() =>
        {
            if (Enum.TryParse<TranslateProxyMode>(TagOf(_proxy), out var m)) _s.Proxy = m;
            UpdateVisibility();
        });
        _proxyAddress.TextChanged += (_, _) => Edit(() => _s.ProxyAddress = _proxyAddress.Text.Trim());
        _timeout.Width = 80;
        _timeout.HorizontalAlignment = HorizontalAlignment.Left;
        _timeout.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        _timeout.TextChanged += (_, _) => Edit(() =>
        {
            if (int.TryParse(_timeout.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) _s.TimeoutSeconds = Math.Clamp(t, 5, 300);
        });
        var proxyRow = new DockPanel();
        _proxy.Width = 150;
        DockPanel.SetDock(_proxy, Dock.Left);
        proxyRow.Children.Add(_proxy);
        _proxyAddress.Margin = new Thickness(8, 0, 0, 0);
        _proxyAddress.ToolTip = Loc.Get("Translate.Settings.ProxyAddressTip");
        proxyRow.Children.Add(_proxyAddress);

        // ---- test
        _test = TranslateUi.Button("Translate.Settings.Test", accent: false, TestConnection, "");
        _testStatus.Margin = new Thickness(10, 0, 0, 0);
        var testRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_test, Dock.Left);
        testRow.Children.Add(_test);
        testRow.Children.Add(_testStatus);

        var root = new StackPanel { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 420 };
        root.Children.Add(Section("Translate.Settings.SectionService"));
        root.Children.Add(Row("Translate.Settings.Provider", _provider));
        root.Children.Add(Hint("Translate.Settings.ProviderHint"));
        root.Children.Add(_openAiPanel);
        root.Children.Add(_baiduPanel);
        root.Children.Add(_youdaoPanel);
        root.Children.Add(_deeplPanel);
        root.Children.Add(Section("Translate.Settings.SectionLanguage"));
        root.Children.Add(Row("Translate.Settings.Target", _target));
        root.Children.Add(Row("Translate.Settings.Fallback", _fallback));
        root.Children.Add(Hint("Translate.Settings.FallbackHint"));
        root.Children.Add(Section("Translate.Settings.SectionImage"));
        root.Children.Add(Row("Translate.Settings.Mode", modes));
        root.Children.Add(Section("Translate.Settings.SectionNetwork"));
        root.Children.Add(Row("Translate.Settings.Proxy", proxyRow));
        root.Children.Add(Row("Translate.Settings.Timeout", _timeout));
        root.Children.Add(testRow);
        Content = root;

        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is TranslateSettings ts && !ReferenceEquals(ts, _s)) Settings = ts;
        };
        Unloaded += (_, _) => { _testCts?.Cancel(); _modelsCts?.Cancel(); };
        Settings = settings ?? (DataContext as TranslateSettings) ?? new TranslateSettings();
    }

    /// <summary>The settings instance being edited.</summary>
    public TranslateSettings Settings
    {
        get => _s;
        set
        {
            _s = value ?? new TranslateSettings();
            Load();
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            Select(_provider, _s.Provider);
            Select(_preset, _s.OpenAi.Preset);
            _baseUrl.Text = _s.OpenAi.BaseUrl;
            _model.Items.Clear();
            _model.Text = _s.OpenAi.Model;
            UpdateModelReasoningHint(_s.OpenAi.Model);
            _openAiKey.Value = SecretProtector.Unprotect(_s.OpenAi.ApiKeyProtected);
            _baiduAppId.Text = _s.Baidu.AppId;
            _baiduSecret.Value = SecretProtector.Unprotect(_s.Baidu.SecretProtected);
            _youdaoAppKey.Text = _s.Youdao.AppKey;
            _youdaoSecret.Value = SecretProtector.Unprotect(_s.Youdao.SecretProtected);
            _deeplKey.Value = SecretProtector.Unprotect(_s.DeepL.ApiKeyProtected);
            Select(_target, TranslateLanguages.Find(_s.TargetLanguage)?.Tag ?? "");
            Select(_fallback, string.Equals(_s.FallbackLanguage, "none", StringComparison.OrdinalIgnoreCase) ? "none" : (TranslateLanguages.Find(_s.FallbackLanguage)?.Tag ?? "none"));
            _modeText.IsChecked = _s.Mode == TranslateMode.ImageToText;
            _modeImage.IsChecked = _s.Mode == TranslateMode.ImageToImage;
            Select(_proxy, _s.Proxy.ToString());
            _proxyAddress.Text = _s.ProxyAddress;
            _timeout.Text = _s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            TranslateUi.SetStatus(_testStatus, "");
        }
        finally { _loading = false; }
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var p = _s.Provider;
        _openAiPanel.Visibility = p == "openai" ? Visibility.Visible : Visibility.Collapsed;
        _baiduPanel.Visibility = p == "baidu" ? Visibility.Visible : Visibility.Collapsed;
        _youdaoPanel.Visibility = p == "youdao" ? Visibility.Visible : Visibility.Collapsed;
        _deeplPanel.Visibility = p == "deepl" ? Visibility.Visible : Visibility.Collapsed;
        _proxyAddress.IsEnabled = _s.Proxy is TranslateProxyMode.Http or TranslateProxyMode.Socks5;
        _test.IsEnabled = TranslationService.IsConfigured(_s);
    }

    private void Edit(Action apply)
    {
        if (_loading) return;
        try { apply(); }
        catch (Exception ex) { Log.Error("Translate settings edit failed", ex); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async void TestConnection()
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        if (!TranslationService.IsConfigured(_s))
        {
            TranslateUi.SetStatus(_testStatus, Loc.Get("Translate.Settings.TestChooseProvider"), error: true);
            return;
        }
        var target = TranslateLanguages.ResolveTarget(_s.TargetLanguage);
        var sample = target == "en" ? "你好，世界！" : "Hello, world!";
        _test.IsEnabled = false;
        TranslateUi.SetStatus(_testStatus, Loc.Get("Translate.Settings.Testing"));
        try
        {
            var outcome = await TranslationService.TranslateAsync(new[] { sample }, target, _s, cts.Token);
            if (cts.IsCancellationRequested) return;
            TranslateUi.SetStatus(_testStatus, Loc.Format("Translate.Settings.TestOk", sample, outcome.Segments[0], outcome.Elapsed.TotalSeconds));
        }
        catch (OperationCanceledException) { /* closed */ }
        catch (TranslationException ex)
        {
            if (!cts.IsCancellationRequested) TranslateUi.SetStatus(_testStatus, Loc.Format("Translate.Settings.TestFailed", ex.Message), error: true);
        }
        catch (Exception ex)
        {
            Log.Error("Translation connection test failed", ex);
            if (!cts.IsCancellationRequested) TranslateUi.SetStatus(_testStatus, Loc.Format("Translate.Settings.TestFailed", ex.Message), error: true);
        }
        finally { _test.IsEnabled = TranslationService.IsConfigured(_s); }
    }

    /// <summary>Fill the model dropdown from GET {base}/models (only on explicit user action).</summary>
    private async void FetchModels()
    {
        _modelsCts?.Cancel();
        var cts = _modelsCts = new CancellationTokenSource();
        _fetchModels.IsEnabled = false;
        SetModelStatus(Loc.Get("Translate.Settings.FetchingModels"));
        try
        {
            using var http = TranslationHttp.CreateClient(_s);
            var models = await OpenAiModelList.FetchAsync(http, _s.OpenAi.BaseUrl, SecretProtector.Unprotect(_s.OpenAi.ApiKeyProtected), cts.Token);
            if (cts.IsCancellationRequested) return;
            var current = _model.Text;
            _loading = true;
            try
            {
                _model.Items.Clear();
                foreach (var m in models) _model.Items.Add(m);
                _model.Text = current;
            }
            finally { _loading = false; }
            SetModelStatus(models.Count == 0 ? Loc.Get("Translate.Settings.ModelsEmpty") : Loc.Format("Translate.Settings.ModelsFetched", models.Count), error: models.Count == 0);
            if (models.Count > 0 && _model.IsKeyboardFocusWithin) _model.IsDropDownOpen = true;
        }
        catch (OperationCanceledException) { if (!cts.IsCancellationRequested) SetModelStatus(Loc.Get("Translate.Settings.ModelsTimeout"), error: true); }
        catch (Exception ex)
        {
            if (ex is not TranslationException) Log.Warn($"Fetching model list failed: {ex.Message}");
            if (!cts.IsCancellationRequested) SetModelStatus(Loc.Format("Translate.Settings.ModelsFailed", ex.Message), error: true);
        }
        finally { _fetchModels.IsEnabled = true; }
    }

    private void SetModelStatus(string text, bool error = false)
    {
        TranslateUi.SetStatus(_modelStatus, text, error);
        _modelStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateModelReasoningHint(string model)
    {
        if (OpenAiModelList.IsReasoningModel(model))
        {
            SetModelStatus(Loc.Get("Translate.Settings.ReasoningModelHint"), error: false);
        }
        else if (_modelStatus.Text == Loc.Get("Translate.Settings.ReasoningModelHint"))
        {
            SetModelStatus("");
        }
    }

    // ------------------------------------------------------------------ building blocks

    private static ComboBox ModelBox()
    {
        var c = TranslateUi.Combo();
        c.IsEditable = true;
        c.IsTextSearchEnabled = false; // typing must not jump to another model
        c.ToolTip = Loc.Get("Translate.Settings.ModelTip");
        return c;
    }

    private static TextBox Input()
    {
        var t = new TextBox();
        t.SetResourceReference(StyleProperty, "Style.Translate.TextBox");
        return t;
    }

    private static RadioButton Radio(string key)
    {
        var r = new RadioButton();
        r.SetResourceReference(ContentProperty, key);
        r.SetResourceReference(StyleProperty, "Style.Translate.RadioButton");
        return r;
    }

    private static FrameworkElement Section(string key)
    {
        var t = TranslateUi.Header(key);
        t.FontSize = 14;
        t.Margin = new Thickness(0, 16, 0, 8);
        return t;
    }

    private static FrameworkElement Row(string labelKey, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        label.SetResourceReference(TextBlock.TextProperty, labelKey);
        Grid.SetColumn(control, 1);
        grid.Children.Add(label);
        grid.Children.Add(control);
        return grid;
    }

    private static FrameworkElement Hint(string key)
    {
        var t = TranslateUi.Caption(key);
        t.Margin = new Thickness(110, -2, 0, 8);
        return t;
    }

    private static void AddItem(ComboBox box, string tag, string text) => box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

    private static string TagOf(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: string t } ? t : "";

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        box.SelectedIndex = box.Items.Count > 0 ? 0 : -1;
    }

    /// <summary>Password box with a show/hide toggle.</summary>
    private sealed class SecretBox : Grid
    {
        private readonly PasswordBox _hidden = new();
        private readonly TextBox _shown = Input();
        private readonly ToggleButton _eye;
        private bool _sync;

        public event Action<string>? Changed;

        public SecretBox()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _hidden.SetResourceReference(StyleProperty, "Style.Translate.PasswordBox");
            _shown.Visibility = Visibility.Collapsed;
            _eye = (ToggleButton)TranslateUi.IconButton("", "Translate.Settings.ShowKey", () => Toggle(), toggle: true);
            _eye.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(_eye, 1);
            Children.Add(_hidden);
            Children.Add(_shown);
            Children.Add(_eye);
            _hidden.PasswordChanged += (_, _) => { if (!_sync) { Mirror(_hidden.Password, toShown: true); Changed?.Invoke(_hidden.Password); } };
            _shown.TextChanged += (_, _) => { if (!_sync) { Mirror(_shown.Text, toShown: false); Changed?.Invoke(_shown.Text); } };
        }

        public string Value
        {
            get => _hidden.Password;
            set
            {
                _sync = true;
                try { _hidden.Password = value ?? ""; _shown.Text = value ?? ""; }
                finally { _sync = false; }
            }
        }

        private void Mirror(string v, bool toShown)
        {
            _sync = true;
            try { if (toShown) _shown.Text = v; else _hidden.Password = v; }
            finally { _sync = false; }
        }

        private void Toggle()
        {
            bool show = _eye.IsChecked == true;
            _shown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _hidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            _eye.Content = show ? "" : "";
        }
    }
}
