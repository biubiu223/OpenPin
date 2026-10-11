using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using OpenPin.Core.Hotkeys;
using OpenPin.Core.Settings;
using OpenPin.SettingsUI.Logic;
using OpenPin.SettingsUI.Views;
using AppThemeMode = OpenPin.Core.Settings.ThemeMode;

namespace OpenPin.Tests.SettingsUI;

public class SettingsMergeTests
{
    [Fact]
    public void Merge_KeepsRuntimeStateWrittenByModules()
    {
        var baseline = new AppSettings();
        var baseNode = SettingsMerge.ToNode(baseline);

        var draft = new AppSettings();
        draft.Capture.MagnifierZoom = 12;                      // user edit
        draft.Hotkeys.Bindings["capture"] = "Ctrl+Shift+A";   // user edit (dictionary key)

        var current = new AppSettings();
        current.Save.LastFolder = @"D:\Shots";                 // written by SaveService meanwhile
        current.Annotation.ToolState["rect"] = "{}";           // written by the toolbar meanwhile

        var merged = SettingsMerge.Merge(baseNode, draft, current);
        Assert.Equal(12, merged.Capture.MagnifierZoom);
        Assert.Equal("Ctrl+Shift+A", merged.Hotkeys.Bindings["capture"]);
        Assert.Equal(@"D:\Shots", merged.Save.LastFolder);
        Assert.Equal("{}", merged.Annotation.ToolState["rect"]);
        Assert.Equal("Ctrl+2", merged.Hotkeys.Bindings["pin.clipboard"]);
    }

    [Fact]
    public void Merge_DraftWinsOnConflictsAndArraysAreReplaced()
    {
        var baseNode = SettingsMerge.ToNode(new AppSettings());
        var draft = new AppSettings();
        draft.Annotation.Palette = new List<string> { "#FF000000" };
        draft.Pin.DefaultOpacity = 50;
        var current = new AppSettings();
        current.Pin.DefaultOpacity = 80;
        current.Annotation.Palette.Add("#FF123456");

        var merged = SettingsMerge.Merge(baseNode, draft, current);
        Assert.Equal(50, merged.Pin.DefaultOpacity);
        Assert.Equal(new[] { "#FF000000" }, merged.Annotation.Palette);
    }

    [Fact]
    public void Merge_RemovedDictionaryKeyIsRemoved()
    {
        var baseNode = SettingsMerge.ToNode(new AppSettings());
        var draft = new AppSettings();
        draft.Hotkeys.Bindings.Remove("pin.nextgroup");
        var merged = SettingsMerge.Merge(baseNode, draft, new AppSettings());
        Assert.False(merged.Hotkeys.Bindings.ContainsKey("pin.nextgroup"));
    }

    [Fact]
    public void Diff_ListsChangedLeafPaths()
    {
        var a = new AppSettings();
        var b = new AppSettings();
        b.General.Theme = AppThemeMode.Dark;
        b.Hotkeys.Bindings["capture"] = "";
        var diff = SettingsMerge.Diff(SettingsMerge.ToNode(a), SettingsMerge.ToNode(b));
        Assert.Equal(new[] { "General.Theme", "Hotkeys.Bindings.capture" }, diff.OrderBy(x => x));
        Assert.Empty(SettingsMerge.Diff(SettingsMerge.ToNode(a), SettingsMerge.ToNode(new AppSettings())));
    }
}

public class SettingsImportTests
{
    [Fact]
    public void ValidFile_IsNormalized()
    {
        const string json = "{ // comment\n \"General\": { \"Theme\": \"Dark\" }, \"Capture\": null, \"Hotkeys\": { \"Bindings\": { \"capture\": \"F1\" } }, }";
        Assert.True(SettingsImport.TryRead(json, out var s, out var error), error);
        Assert.Equal(AppThemeMode.Dark, s!.General.Theme);
        Assert.NotNull(s.Capture);
        Assert.Equal("F1", s.Hotkeys.Bindings["capture"]);
        Assert.Equal("Ctrl+2", s.Hotkeys.Bindings["pin.clipboard"]); // missing defaults added
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{ \"General\": { \"Theme\": \"Purple\" } }")]
    [InlineData("{ \"Capture\": { \"MagnifierZoom\": \"big\" } }")]
    public void InvalidFile_IsRejected(string json)
    {
        Assert.False(SettingsImport.TryRead(json, out var s, out var error));
        Assert.Null(s);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ExportRoundTrips()
    {
        var s = new AppSettings();
        s.Pin.TextFontSize = 17.5;
        Assert.True(SettingsImport.TryRead(SettingsImport.Serialize(s), out var back, out _));
        Assert.Equal(17.5, back!.Pin.TextFontSize);
    }
}

public class HotkeyLogicTests
{
    [Fact]
    public void Analyze_FindsDuplicatesInvalidAndInUse()
    {
        var bindings = new Dictionary<string, string>
        {
            ["a"] = "Ctrl+1",
            ["b"] = "ctrl+1",       // same hotkey, different spelling
            ["c"] = "Ctrl+Alt+Z",   // taken by another program
            ["d"] = "Ctrl+Nope",
            ["e"] = "",
            ["f"] = "F2",
        };
        Hotkey.TryParse("Ctrl+Alt+Z", out var taken);
        var issues = HotkeyConflicts.Analyze(bindings, hk => hk != taken, new[] { "f" });

        Assert.Equal(HotkeyIssueKind.Duplicate, issues["a"].Kind);
        Assert.Equal(new[] { "b" }, issues["a"].OtherActions);
        Assert.Equal(HotkeyIssueKind.Duplicate, issues["b"].Kind);
        Assert.Equal(HotkeyIssueKind.InUse, issues["c"].Kind);
        Assert.Equal(HotkeyIssueKind.Invalid, issues["d"].Kind);
        Assert.Equal(HotkeyIssueKind.None, issues["e"].Kind);
        Assert.Equal(HotkeyIssueKind.RegisterFailed, issues["f"].Kind);
        Assert.Equal(5, HotkeyConflicts.CountProblems(issues));
    }

    [Fact]
    public void Analyze_ProbeExceptionsAreIgnored()
    {
        var issues = HotkeyConflicts.Analyze(new Dictionary<string, string> { ["a"] = "Ctrl+1" }, _ => throw new InvalidOperationException());
        Assert.Equal(HotkeyIssueKind.None, issues["a"].Kind);
    }

    [Theory]
    [InlineData(Key.A, ModifierKeys.Control | ModifierKeys.Shift, "Set", "Ctrl+Shift+A")]
    [InlineData(Key.D1, ModifierKeys.Alt, "Set", "Alt+1")]
    [InlineData(Key.F1, ModifierKeys.None, "Set", "F1")]
    [InlineData(Key.Snapshot, ModifierKeys.None, "Set", "PrintScreen")]
    [InlineData(Key.Escape, ModifierKeys.None, "Cancel", "")]
    [InlineData(Key.Back, ModifierKeys.None, "Clear", "")]
    [InlineData(Key.Delete, ModifierKeys.None, "Clear", "")]
    [InlineData(Key.LeftCtrl, ModifierKeys.Control, "Pending", "Ctrl+")]
    [InlineData(Key.A, ModifierKeys.None, "Rejected", "A")]
    [InlineData(Key.A, ModifierKeys.Shift, "Rejected", "Shift+A")]
    [InlineData(Key.Escape, ModifierKeys.Control, "Set", "Ctrl+Esc")]
    public void Recorder_InterpretsKeys(Key key, ModifierKeys mods, string outcome, string preview)
    {
        var r = HotkeyRecorderLogic.Interpret(key, mods);
        Assert.Equal(outcome, r.Outcome.ToString());
        Assert.Equal(preview, r.Preview);
    }
}

public class TemplatePreviewTests
{
    private static readonly DateTime Ts = new(2026, 3, 4, 5, 6, 7, 89);

    [Fact]
    public void FileName_ExpandsAndAppendsExtension()
    {
        Assert.Equal("OpenPin_2026-03-04_05-06-07.jpg", TemplatePreview.FileName("OpenPin_$yyyy-MM-dd_HH-mm-ss$", ".JPG", Ts));
        Assert.Equal("notepad_1920x1080.png", TemplatePreview.FileName("%process!%_%width%x%height%", null, Ts));
        Assert.Equal("OpenPin_2026-03-04_05-06-07.png", TemplatePreview.FileName("", "png", Ts));
    }

    [Fact]
    public void FullPath_UsesFolderOrDefault()
    {
        Assert.Equal(@"C:\Shots\2026\03\a.png", TemplatePreview.FullPath(@"C:\Shots", @"C:\Default", "$yyyy$/$MM$/a", Ts));
        Assert.Equal(@"C:\Default\b.jpg", TemplatePreview.FullPath("  ", @"C:\Default", "b.jpg", Ts));
    }

    [Fact]
    public void ResolveFolder_ExpandsEnvironmentVariables()
    {
        var expected = Environment.GetEnvironmentVariable("WINDIR") + @"\x";
        Assert.Equal(expected, TemplatePreview.ResolveFolder(@"%WINDIR%\x", "fallback"));
        Assert.Equal("fallback", TemplatePreview.ResolveFolder(null, "fallback"));
    }
}

public class WindowLogicTests
{
    [Theory]
    [InlineData(AppThemeMode.Dark, 1, true)]
    [InlineData(AppThemeMode.Light, 0, false)]
    [InlineData(AppThemeMode.System, 0, true)]
    [InlineData(AppThemeMode.System, 1, false)]
    public void Theme(AppThemeMode mode, int appsLight, bool dark) => Assert.Equal(dark, ThemeResolver.IsDark(mode, appsLight));

    [Fact]
    public void Theme_UnknownSystemValueIsLight() => Assert.False(ThemeResolver.IsDark(AppThemeMode.System, null));

    private static readonly Int32Rect[] TwoMonitors = { new(0, 0, 1920, 1040), new(1920, 0, 2560, 1400) };

    [Fact]
    public void Bounds_VisibleAreKept() =>
        Assert.Equal(new Int32Rect(2000, 100, 1200, 900), WindowBoundsLogic.Validate(new Int32Rect(2000, 100, 1200, 900), TwoMonitors));

    [Fact]
    public void Bounds_OffscreenAreDropped() =>
        Assert.Null(WindowBoundsLogic.Validate(new Int32Rect(6000, 100, 1200, 900), TwoMonitors));

    [Fact]
    public void Bounds_TooLargeAreCapped()
    {
        var r = WindowBoundsLogic.Validate(new Int32Rect(10, 10, 4000, 3000), TwoMonitors)!.Value;
        Assert.Equal(new Int32Rect(1920, 0, 2560, 1400), r); // capped to and moved onto the monitor it mostly covers
    }

    [Fact]
    public void Bounds_TitleBarAboveScreenIsDropped() =>
        Assert.Null(WindowBoundsLogic.Validate(new Int32Rect(100, -500, 1000, 520), TwoMonitors));

    [Theory]
    [InlineData("0.1.0+4ab0b0c1234567890", "0.1.0+4ab0b0c")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("1.2.3+abc", "1.2.3+abc")]
    public void VersionIsShortened(string v, string expected) => Assert.Equal(expected, AboutView.ShortenVersion(v));
}

public class SettingsCatalogTests
{
    private static readonly Lazy<HashSet<string>> StringKeys = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OpenPin.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = new HashSet<string>();
        foreach (var file in new[] { "SettingsUI.xaml", "Common.xaml" })
        {
            var doc = XDocument.Load(Path.Combine(dir!.FullName, "src", "OpenPin", "Resources", "Strings", "zh-CN", file));
            foreach (var e in doc.Root!.Elements()) if (e.Attribute(x + "Key") is { } k) keys.Add(k.Value);
        }
        return keys;
    });

    [Fact]
    public void PageIdsAreUniqueAndFindable()
    {
        var ids = SettingsCatalog.Pages.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var id in new[] { "general", "hotkeys", "capture", "pin", "annotation", "save", "ocr", "longcapture", "recording", "translate", "recognition", "about" })
            Assert.Equal(id, SettingsCatalog.FindPage(id)?.Id);
        Assert.Equal("hotkeys", SettingsCatalog.FindPage("Hotkey")?.Id);
        Assert.Equal("longcapture", SettingsCatalog.FindPage("long-capture")?.Id);
        Assert.Null(SettingsCatalog.FindPage("nope"));
        Assert.Null(SettingsCatalog.FindPage(null));
    }

    [Fact]
    public void DefinitionsReferenceExistingPropertiesOfMatchingKind()
    {
        foreach (var page in SettingsCatalog.Pages)
        foreach (var def in SettingsCatalog.Defs(page))
        {
            var type = SettingsCatalog.SectionType(def.Section);
            Assert.True(type != null, $"section {def.Section}");
            var prop = type!.GetProperty(def.Property);
            Assert.True(prop != null, $"property {def.Path}");
            var t = prop!.PropertyType;
            bool ok = def.Kind switch
            {
                SettingKind.Toggle => t == typeof(bool),
                SettingKind.Enum => t.IsEnum,
                SettingKind.Number or SettingKind.Slider => t == typeof(int) || t == typeof(double) || t == typeof(long),
                SettingKind.Choice => def.Choices != null && def.Choices.All(c => c.Value.GetType() == t),
                _ => t == typeof(string),
            };
            Assert.True(ok, $"{def.Path} is {t.Name}, not suitable for {def.Kind}");
            if (def.EnabledBy != null) Assert.Equal(typeof(bool), type.GetProperty(def.EnabledBy)?.PropertyType);
        }
    }

    [Fact]
    public void EveryPropertyIsCoveredOrRenderedAutomatically()
    {
        // Sections owned by Core / finished modules must be covered explicitly; complex properties never fall back.
        foreach (var page in SettingsCatalog.Pages.Where(p => p.Section != null))
        {
            var uncovered = SettingsCatalog.UncoveredProperties(page);
            Assert.All(uncovered, p => Assert.True(SettingsCatalog.IsSimple(p.PropertyType), $"{page.Section}.{p.Name} needs a custom block"));
            if (page.Section is "General" or "Hotkeys" or "Capture" or "Pin" or "Annotation" or "Save" or "Ocr")
                Assert.True(uncovered.Count == 0, $"{page.Section}: {string.Join(", ", uncovered.Select(p => p.Name))}");
        }
    }

    [Fact]
    public void AllSectionsOfAppSettingsHaveAPage()
    {
        var sections = typeof(AppSettings).GetProperties().Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string)).Select(p => p.Name);
        var withPage = SettingsCatalog.Pages.Select(p => p.Section).ToHashSet();
        Assert.All(sections, s => Assert.Contains(s, withPage));
    }

    [Fact]
    public void ExplicitTitlesAndEnumLabelsExist()
    {
        var keys = StringKeys.Value;
        foreach (var page in SettingsCatalog.Pages)
        {
            Assert.Contains(page.TitleKey, keys);
            foreach (var b in page.Blocks)
            {
                if (b is HeaderBlock h) Assert.Contains(h.TitleKey, keys);
                if (b is GroupBlock g) Assert.Contains(g.TitleKey, keys);
            }
            foreach (var def in SettingsCatalog.Defs(page))
            {
                Assert.True(keys.Contains(def.TitleKey), def.TitleKey);
                if (def.UnitKey != null) Assert.Contains(def.UnitKey, keys);
                foreach (var c in def.Choices ?? Array.Empty<ChoiceOption>())
                    if (c.Label.StartsWith("SettingsUI.")) Assert.True(keys.Contains(c.Label), c.Label);
                var t = SettingsCatalog.SectionType(def.Section)!.GetProperty(def.Property)!.PropertyType;
                if (def.Kind == SettingKind.Enum)
                    foreach (var (_, key) in SettingsCatalog.EnumOptions(t)) Assert.True(keys.Contains(key), key);
            }
        }
    }

    [Fact]
    public void TemplateVariableKeysExist()
    {
        foreach (var (_, key) in TemplatePreview.Variables) Assert.Contains(key, StringKeys.Value);
    }

    [Theory]
    [InlineData("MaxLength", "Max length")]
    [InlineData("ShowCursor", "Show cursor")]
    [InlineData("Fps", "Fps")]
    public void Humanize(string name, string expected) => Assert.Equal(expected, SettingsCatalog.Humanize(name));
}
