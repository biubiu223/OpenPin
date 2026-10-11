using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.SettingsUI.Logic;

namespace OpenPin.SettingsUI.Views;

/// <summary>About page (SET-04).</summary>
internal static class AboutView
{
    public const string GitHubUrl = "https://github.com/biubiu223/OpenPin";

    /// <summary>Third-party components shipped with OpenPin: (name, license, url).</summary>
    public static readonly IReadOnlyList<(string Name, string License, string Url)> ThirdParty = new[]
    {
        ("Hardcodet.NotifyIcon.Wpf", "MIT", "https://github.com/hardcodet/wpf-notifyicon"),
        ("ZXing.Net", "Apache-2.0", "https://github.com/micjahn/ZXing.Net"),
        (".NET / WPF (Fluent theme)", "MIT", "https://github.com/dotnet/wpf"),
    };

    public const string MitLicense =
        "MIT License\n\nCopyright (c) 2026 OpenPin contributors\n\n" +
        "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation " +
        "files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, " +
        "modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software " +
        "is furnished to do so, subject to the following conditions:\n\n" +
        "The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.\n\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE " +
        "WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR " +
        "COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, " +
        "ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.";

    /// <summary>Informational version without the "+commit" suffix's full hash (kept to 7 chars).</summary>
    public static string Version
    {
        get
        {
            var asm = typeof(AboutView).Assembly;
            var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? asm.GetName().Version?.ToString() ?? "0.0.0";
            return ShortenVersion(v);
        }
    }

    /// <summary>"0.1.0+abcdef1234567" → "0.1.0+abcdef1".</summary>
    public static string ShortenVersion(string v)
    {
        int plus = v.IndexOf('+');
        return plus >= 0 && v.Length - plus - 1 > 7 ? v[..(plus + 8)] : v;
    }

    public static FrameworkElement Build(SettingsWindow window)
    {
        var sp = new StackPanel();

        // Hero
        var hero = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Image { Width = 64, Height = 64, Margin = new Thickness(4, 4, 20, 4) };
        try { icon.Source = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/AppIcon.ico")); }
        catch (Exception ex) { Log.Warn($"About icon: {ex.Message}"); }
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        hero.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Kit.Text("OpenPin", 24, "TextFillColorPrimaryBrush", FontWeights.SemiBold));
        text.Children.Add(Kit.Text(Loc.Format("SettingsUI.About.Version", Version), 13, "TextFillColorSecondaryBrush"));
        var free = Kit.Text("SettingsUI.About.Free", 13, "AccentTextFillColorPrimaryBrush");
        free.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(free);
        Grid.SetColumn(text, 1);
        hero.Children.Add(text);
        sp.Children.Add(hero);

        sp.Children.Add(Kit.Card(SettingsCatalog.Icons.Code, Kit.TitleBlock("SettingsUI.About.Source", GitHubUrl),
            Kit.Button("SettingsUI.About.OpenGitHub", (_, _) => Kit.OpenShell(GitHubUrl), icon: "")));

        var license = new ExpanderCard(SettingsCatalog.Icons.License, "SettingsUI.About.License", "SettingsUI.About.License.Desc", null, window.ExpandGroups);
        var licenseText = new TextBox
        {
            Text = MitLicense,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        license.AddContent(licenseText);
        sp.Children.Add(license);

        var notices = new ExpanderCard(SettingsCatalog.Icons.Info, "SettingsUI.About.ThirdParty", "SettingsUI.About.ThirdParty.Desc", null, window.ExpandGroups);
        foreach (var (name, lic, url) in ThirdParty)
        {
            var link = Kit.Button("SettingsUI.About.Homepage", (_, _) => Kit.OpenShell(url));
            notices.AddRow(Kit.TitleBlock(name, Loc.Format("SettingsUI.About.LicenseName", lic)), link);
        }
        sp.Children.Add(notices);

        sp.Children.Add(Kit.Header("SettingsUI.About.Folders"));
        sp.Children.Add(Kit.Card(SettingsCatalog.Icons.Data, Kit.TitleBlock("SettingsUI.About.DataDir", AppPaths.DataDir),
            Kit.Button("SettingsUI.About.Open", (_, _) => Kit.OpenShell(AppPaths.Ensure(AppPaths.DataDir)), icon: "")));
        sp.Children.Add(Kit.Card(SettingsCatalog.Icons.Log, Kit.TitleBlock("SettingsUI.About.LogDir", System.IO.Path.Combine(AppPaths.DataDir, "logs")),
            Kit.Button("SettingsUI.About.Open", (_, _) => Kit.OpenShell(AppPaths.LogsDir), icon: "")));
        return sp;
    }
}
