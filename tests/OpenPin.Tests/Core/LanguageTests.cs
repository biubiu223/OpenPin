using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using OpenPin.Core;
using OpenPin.Core.Settings;
using OpenPin.SettingsUI.Logic;

namespace OpenPin.Tests.Core;

public class LanguageManagerLogicTests
{
    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-TW", "zh-CN")]
    [InlineData("en-US", "en-US")]
    [InlineData("en", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData(null, "zh-CN")]
    [InlineData("", "zh-CN")]
    [InlineData("   ", "zh-CN")]
    [InlineData("fr-FR", "zh-CN")]
    [InlineData("ja-JP", "zh-CN")]
    public void NormalizeLanguage_MapsCorrectly(string? input, string expected)
    {
        Assert.Equal(expected, LanguageManager.NormalizeLanguage(input));
    }

    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("zh", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("en-US", true)]
    [InlineData("en", true)]
    [InlineData("en-GB", true)]
    [InlineData("fr-FR", false)]
    [InlineData("ja", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsSupported_IdentifiesLanguages(string? input, bool expected)
    {
        Assert.Equal(expected, LanguageManager.IsSupported(input));
    }

    [Fact]
    public void SettingsCatalog_ContainsEnglishOption()
    {
        var generalPage = SettingsCatalog.Pages.FirstOrDefault(p => p.Id == "general");
        Assert.NotNull(generalPage);

        var langDef = SettingsCatalog.Defs(generalPage!).FirstOrDefault(d => d.Property == "Language");
        Assert.NotNull(langDef);
        Assert.NotNull(langDef!.Choices);

        var choices = langDef.Choices!;
        Assert.Contains(choices, c => c.Value.Equals("zh-CN") && c.Label == "简体中文");
        Assert.Contains(choices, c => c.Value.Equals("en-US") && c.Label == "English");
    }

    [Theory]
    [InlineData("zh-CN", "Common", "/OpenPin;component/Resources/Strings/zh-CN/Common.xaml")]
    [InlineData("en-US", "Common", "/OpenPin;component/Resources/Strings/en-US/Common.xaml")]
    [InlineData("en", "Capture", "/OpenPin;component/Resources/Strings/en-US/Capture.xaml")]
    [InlineData("unknown", "Pin", "/OpenPin;component/Resources/Strings/zh-CN/Pin.xaml")]
    public void GetDictionarySource_ReturnsExpectedPackUri(string lang, string module, string expectedUri)
    {
        Assert.Equal(expectedUri, LanguageManager.GetDictionarySource(lang, module));
    }
}

public class LanguageDictionaryIntegrityTests
{
    private static readonly XNamespace XNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string GetStringsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OpenPin.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "OpenPin", "Resources", "Strings");
    }

    [Fact]
    public void ModulesCountMatchesRequirement()
    {
        Assert.Equal(10, LanguageManager.Modules.Count);
        var expected = new[]
        {
            "Common", "Annotation", "Capture", "Pin", "Services",
            "SettingsUI", "LongCapture", "Recording", "Translate", "Recognition"
        };
        Assert.Equal(expected, LanguageManager.Modules);
    }

    [Theory]
    [InlineData("Common")]
    [InlineData("Annotation")]
    [InlineData("Capture")]
    [InlineData("Pin")]
    [InlineData("Services")]
    [InlineData("SettingsUI")]
    [InlineData("LongCapture")]
    [InlineData("Recording")]
    [InlineData("Translate")]
    [InlineData("Recognition")]
    public void EnUsDictionary_MatchesZhCnKeysExactly(string module)
    {
        var baseDir = GetStringsDirectory();
        var zhPath = Path.Combine(baseDir, "zh-CN", $"{module}.xaml");
        var enPath = Path.Combine(baseDir, "en-US", $"{module}.xaml");

        Assert.True(File.Exists(zhPath), $"zh-CN/{module}.xaml must exist");
        Assert.True(File.Exists(enPath), $"en-US/{module}.xaml must exist");

        var zhDoc = XDocument.Load(zhPath);
        var enDoc = XDocument.Load(enPath);

        var zhDict = zhDoc.Root!.Elements()
            .Select(e => (Key: e.Attribute(XNamespace + "Key")?.Value, Value: e.Value))
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .ToDictionary(x => x.Key!, x => x.Value);

        var enDict = enDoc.Root!.Elements()
            .Select(e => (Key: e.Attribute(XNamespace + "Key")?.Value, Value: e.Value))
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .ToDictionary(x => x.Key!, x => x.Value);

        Assert.True(zhDict.Count > 0, $"{module} zh-CN dictionary should not be empty");
        Assert.True(enDict.Count > 0, $"{module} en-US dictionary should not be empty");

        var missingInEn = zhDict.Keys.Except(enDict.Keys).OrderBy(k => k).ToList();
        var extraInEn = enDict.Keys.Except(zhDict.Keys).OrderBy(k => k).ToList();

        Assert.True(missingInEn.Count == 0, $"en-US/{module}.xaml is missing keys: {string.Join(", ", missingInEn)}");
        Assert.True(extraInEn.Count == 0, $"en-US/{module}.xaml has extra keys: {string.Join(", ", extraInEn)}");

        // Validate non-empty content
        foreach (var (k, v) in enDict)
        {
            Assert.False(string.IsNullOrWhiteSpace(v), $"en-US/{module}.xaml key '{k}' has empty or whitespace value");
        }

        // Validate string.Format placeholder parity ({0}, {1}, ...)
        var placeholderRegex = new Regex(@"\{(\d+)\}");
        foreach (var (k, zhVal) in zhDict)
        {
            var enVal = enDict[k];
            var zhPlaceholders = placeholderRegex.Matches(zhVal).Select(m => m.Value).Distinct().OrderBy(p => p).ToList();
            var enPlaceholders = placeholderRegex.Matches(enVal).Select(m => m.Value).Distinct().OrderBy(p => p).ToList();
            Assert.Equal(zhPlaceholders, enPlaceholders);
        }
    }
}

public class LanguageManagerSwitchingTests
{
    private static readonly object SyncLock = new();
    private static bool _appInitialized;

    private static void EnsureApplication()
    {
        lock (SyncLock)
        {
            if (!_appInitialized && Application.Current == null)
            {
                try
                {
                    _ = new Application();
                    _appInitialized = true;
                }
                catch { }
            }
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                EnsureApplication();
                action();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException("STA action failed: " + error);
    }

    [Fact]
    public void LanguageManager_SwapsDictionariesDynamically()
    {
        RunSta(() =>
        {
            var rootDict = new ResourceDictionary();

            // Initial apply: zh-CN
            LanguageManager.Apply(rootDict.MergedDictionaries, "zh-CN");
            Assert.Equal("zh-CN", LanguageManager.CurrentLanguage);
            Assert.Equal(10, rootDict.MergedDictionaries.Count);

            // Verify zh-CN strings
            Assert.Equal("确定", rootDict["Common.OK"]);
            Assert.Equal("截图", rootDict["Action.Capture"]);
            Assert.Equal("设置", rootDict["Action.OpenSettings"]);
            Assert.Equal("OpenPin 设置", rootDict["SettingsUI.WindowTitle"]);

            // Switch to English
            bool eventFired = false;
            EventHandler handler = (_, _) => eventFired = true;
            LanguageManager.LanguageChanged += handler;

            try
            {
                LanguageManager.Apply(rootDict.MergedDictionaries, "en-US");

                Assert.True(eventFired, "LanguageChanged event should fire on language swap");
                Assert.Equal("en-US", LanguageManager.CurrentLanguage);
                Assert.Equal(10, rootDict.MergedDictionaries.Count);

                // Dictionaries point to en-US
                foreach (var module in LanguageManager.Modules)
                {
                    Assert.Contains(rootDict.MergedDictionaries, d =>
                        d.Source != null && d.Source.OriginalString.Contains($"en-US/{module}.xaml", StringComparison.OrdinalIgnoreCase));
                }

                // Verify en-US strings
                Assert.Equal("OK", rootDict["Common.OK"]);
                Assert.Equal("Capture", rootDict["Action.Capture"]);
                Assert.Equal("Settings", rootDict["Action.OpenSettings"]);
                Assert.Equal("OpenPin Settings", rootDict["SettingsUI.WindowTitle"]);
                Assert.Equal("Scrolling Screenshot", rootDict["Action.CaptureLong"]);
                Assert.Equal("Screen Recording", rootDict["Action.CaptureRecord"]);
                Assert.Equal("Saved", rootDict["Services.Saved"]);

                // Switch back to zh-CN
                eventFired = false;
                LanguageManager.Apply(rootDict.MergedDictionaries, "zh-CN");

                Assert.True(eventFired);
                Assert.Equal("zh-CN", LanguageManager.CurrentLanguage);
                Assert.Equal(10, rootDict.MergedDictionaries.Count);
                Assert.Equal("确定", rootDict["Common.OK"]);
                Assert.Equal("截图", rootDict["Action.Capture"]);
            }
            finally
            {
                LanguageManager.LanguageChanged -= handler;
            }
        });
    }

    [Fact]
    public void LanguageManager_ReplacesExistingDictionariesAtSameIndices()
    {
        RunSta(() =>
        {
            var rootDict = new ResourceDictionary();

            // Simulate pre-existing dictionaries in App.xaml
            foreach (var m in LanguageManager.Modules)
            {
                rootDict.MergedDictionaries.Add(LanguageManager.BuildDictionary("zh-CN", m));
            }

            Assert.Equal(10, rootDict.MergedDictionaries.Count);

            // Apply en-US
            LanguageManager.Apply(rootDict.MergedDictionaries, "en-US");

            // Total count should still be 10 (replaced in-place, not appended)
            Assert.Equal(10, rootDict.MergedDictionaries.Count);
            foreach (var d in rootDict.MergedDictionaries)
            {
                Assert.Contains("en-US", d.Source?.OriginalString ?? "");
            }
        });
    }

    [Fact]
    public void LanguageManager_SettingsChanged_UpdatesMergedDictionaries()
    {
        RunSta(() =>
        {
            var rootDict = new ResourceDictionary();
            var tempSettingsFile = Path.Combine(Path.GetTempPath(), $"openpin-lang-settings-{Guid.NewGuid():N}.json");

            try
            {
                var settings = new SettingsService(tempSettingsFile);
                settings.Current.General.Language = "en-US";

                // Apply target language
                LanguageManager.Apply(rootDict.MergedDictionaries, settings.Current.General.Language);
                Assert.Equal("en-US", LanguageManager.CurrentLanguage);
                Assert.Equal("OK", rootDict["Common.OK"]);

                // When settings change to zh-CN
                settings.Current.General.Language = "zh-CN";
                LanguageManager.Apply(rootDict.MergedDictionaries, settings.Current.General.Language);
                Assert.Equal("zh-CN", LanguageManager.CurrentLanguage);
                Assert.Equal("确定", rootDict["Common.OK"]);
            }
            finally
            {
                LanguageManager.Reset();
                if (File.Exists(tempSettingsFile))
                {
                    File.Delete(tempSettingsFile);
                }
            }
        });
    }
}
