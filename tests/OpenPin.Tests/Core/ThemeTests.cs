using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using OpenPin.Core.Settings;
using OpenPin.Core.Theme;
using AppThemeMode = OpenPin.Core.Settings.ThemeMode;

namespace OpenPin.Tests.Core;

public class ThemeLogicTests
{
    [Theory]
    [InlineData(AppThemeMode.Dark, 1, true)]
    [InlineData(AppThemeMode.Dark, null, true)]
    [InlineData(AppThemeMode.Light, 0, false)]
    [InlineData(AppThemeMode.System, 0, true)]
    [InlineData(AppThemeMode.System, 1, false)]
    [InlineData(AppThemeMode.System, null, false)]
    public void ResolveIsDark(AppThemeMode mode, int? appsUseLightTheme, bool dark) =>
        Assert.Equal(dark, ThemeLogic.ResolveIsDark(mode, appsUseLightTheme));

    [Theory]
    [InlineData("#1677FF", 0x16, 0x77, 0xFF)]
    [InlineData("#80FF0000", 0xFF, 0x00, 0x00)] // alpha dropped: the accent is always opaque
    [InlineData("rgb(41, 184, 219)", 41, 184, 219)]
    [InlineData("orange", 0xFF, 0xA5, 0x00)]
    public void ParseAccent_Valid(string text, byte r, byte g, byte b) =>
        Assert.Equal(Color.FromRgb(r, g, b), ThemeLogic.ParseAccent(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not a color")]
    [InlineData("#00000000")] // fully transparent
    public void ParseAccent_FallsBack(string? text)
    {
        Assert.Equal(ThemeLogic.DefaultAccent, ThemeLogic.ParseAccent(text));
        Assert.Equal(Colors.Green, ThemeLogic.ParseAccent(text, Colors.Green));
    }

    [Fact]
    public void ResolveAccent_SystemAccentOnlyWhenEnabledAndKnown()
    {
        var sys = Color.FromRgb(1, 2, 3);
        Assert.Equal(sys, ThemeLogic.ResolveAccent("#FF0000", true, sys));
        Assert.Equal(Colors.Red, ThemeLogic.ResolveAccent("#FF0000", false, sys));
        Assert.Equal(Colors.Red, ThemeLogic.ResolveAccent("#FF0000", true, null));
        Assert.Equal(ThemeLogic.DefaultAccent, ThemeLogic.ResolveAccent("junk", true, null));
    }

    [Fact]
    public void FromAbgr_SwapsChannels() =>
        Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), ThemeLogic.FromAbgr(unchecked((int)0xFF332211)));

    [Theory]
    [InlineData("#1677FF", true)]  // default blue: white text
    [InlineData("#000000", true)]
    [InlineData("#E5484D", true)]
    [InlineData("#FFFFFF", false)] // white accent: black text
    [InlineData("#FADB14", false)] // yellow: black text
    [InlineData("#A0D911", false)]
    public void OnAccent_PicksReadableText(string accent, bool white) =>
        Assert.Equal(white ? Colors.White : Colors.Black, ThemeLogic.OnAccent(ThemeLogic.ParseAccent(accent)));
}

public class SettingsMigrationTests
{
    [Fact]
    public void NewSettings_AreCurrentAndFollowSystem()
    {
        var s = new AppSettings();
        Assert.Equal(SettingsMigrations.CurrentVersion, s.Version);
        Assert.Equal(AppThemeMode.System, s.General.Theme);
        Assert.False(SettingsMigrations.Migrate(s));
    }

    [Fact]
    public void Version1_LightBecomesSystem()
    {
        var s = Load("""{ "Version": 1, "General": { "Theme": "Light", "AccentColor": "#FF0000" } }""");
        Assert.Equal(AppThemeMode.System, s.General.Theme);
        Assert.Equal(2, s.Version);
        Assert.Equal("#FF0000", s.General.AccentColor);
    }

    [Fact]
    public void Version1_DarkIsKept()
    {
        var s = Load("""{ "Version": 1, "General": { "Theme": "Dark" } }""");
        Assert.Equal(AppThemeMode.Dark, s.General.Theme);
        Assert.Equal(2, s.Version);
    }

    [Fact]
    public void Version2_LightIsAnExplicitChoice()
    {
        var s = Load("""{ "Version": 2, "General": { "Theme": "Light" } }""");
        Assert.Equal(AppThemeMode.Light, s.General.Theme);
    }

    private static AppSettings Load(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), $"openpin-migration-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, json);
            var svc = new SettingsService(file);
            svc.Load();
            return svc.Current;
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class ThemeDictionaryTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException("STA action failed: " + error);
    }

    private static string ResourcesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OpenPin.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "OpenPin", "Resources");
    }

    private static ResourceDictionary LoadDict(string name) =>
        (ResourceDictionary)XamlReader.Parse(File.ReadAllText(Path.Combine(ResourcesDir(), name)));

    [Fact]
    public void LightAndDarkDefineTheSameKeysWithTheSameTypes()
    {
        RunSta(() =>
        {
            var light = LoadDict("Theme.Light.xaml");
            var dark = LoadDict("Theme.Dark.xaml");
            var lk = light.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToList();
            var dk = dark.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToList();
            Assert.Equal(lk, dk);
            Assert.True(lk.Count > 30);
            foreach (var k in lk) Assert.Equal(light[k].GetType(), dark[k].GetType());

            // The tokens modules and ThemeManager rely on.
            foreach (var k in new[] { "Color.Accent", "Color.Surface", "Color.Text", "Color.PinActive", "Brush.Accent", "Brush.OnAccent",
                                      "Brush.Surface", "Brush.SurfaceAlt", "Brush.Text", "Brush.TextSecondary", "Brush.Border",
                                      "Brush.Hover", "Brush.Pressed", "Effect.Shadow", "Brush.Pin.Badge", "Brush.Recording.Ready" })
                Assert.Contains(k, lk);

            // Dark really is dark, light really is light.
            Assert.True(ThemeLogic.Luminance((Color)dark["Color.Surface"]) < 0.05);
            Assert.True(ThemeLogic.Luminance((Color)light["Color.Surface"]) > 0.9);
            Assert.True(ThemeLogic.ContrastRatio((Color)dark["Color.Text"], (Color)dark["Color.Surface"]) > 7);
            Assert.True(ThemeLogic.ContrastRatio((Color)dark["Color.TextSecondary"], (Color)dark["Color.Surface"]) > 4.5);
        });
    }

    [Fact]
    public void ColorTokensMatchTheirBrushes()
    {
        RunSta(() =>
        {
            foreach (var name in new[] { "Theme.Light.xaml", "Theme.Dark.xaml" })
            {
                var d = LoadDict(name);
                foreach (var token in new[] { "Accent", "Surface", "SurfaceAlt", "Border", "Text", "TextSecondary", "Hover", "Pressed", "Danger", "Warning", "Success" })
                    Assert.True(((SolidColorBrush)d["Brush." + token]).Color == (Color)d["Color." + token], $"{name}: {token}");
            }
        });
    }

    [Fact]
    public void ModuleStylesDoNotRedefineThemeTokens()
    {
        RunSta(() =>
        {
            var themeKeys = LoadDict("Theme.Light.xaml").Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();
            foreach (var file in Directory.GetFiles(Path.Combine(ResourcesDir(), "Styles"), "*.xaml").Append(Path.Combine(ResourcesDir(), "Theme.xaml")))
            {
                var text = File.ReadAllText(file);
                foreach (var k in themeKeys)
                    Assert.False(text.Contains($"x:Key=\"{k}\""), $"{Path.GetFileName(file)} redefines {k} (later dictionaries would win over the theme)");
            }
        });
    }

    [Fact]
    public void SetAccent_OverridesAccentDerivedTokens()
    {
        RunSta(() =>
        {
            var d = LoadDict("Theme.Dark.xaml");
            var accent = Color.FromRgb(0xFA, 0xDB, 0x14);
            ThemeManager.SetAccent(d, accent, dark: true);
            Assert.Equal(accent, (Color)d["Color.Accent"]);
            Assert.Equal(accent, (Color)d["Color.PinActive"]);
            Assert.Equal(accent, ((SolidColorBrush)d["Brush.Accent"]).Color);
            Assert.Equal(Colors.Black, ((SolidColorBrush)d["Brush.OnAccent"]).Color);
            Assert.Equal(accent, ((SolidColorBrush)d["Brush.Recording.Ready"]).Color);
            Assert.True(((SolidColorBrush)d["Brush.Accent"]).IsFrozen);
        });
    }
}
