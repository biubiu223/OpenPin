using System.IO;
using OpenPin.Services;

namespace OpenPin.Tests.Services;

public class PathTemplateTests
{
    private static readonly SaveContext Ctx = new()
    {
        Timestamp = new DateTime(2024, 3, 7, 9, 5, 4, 321),
        WindowTitle = "Report: Q1/Q2 *draft*",
        ProcessName = "WINWORD.EXE",
    };

    private static string X(string t, int w = 800, int h = 600) => PathTemplate.Expand(t, Ctx, w, h);

    [Theory]
    [InlineData("OpenPin_$yyyy-MM-dd_HH-mm-ss$", "OpenPin_2024-03-07_09-05-04")]
    [InlineData("$yyyy-MM-dd_HH-mm-ss-fff$", "2024-03-07_09-05-04-321")]
    [InlineData("$yyyyMMdd$", "20240307")]
    [InlineData("a$d$b", "a7b")] // single char = custom format, not the "short date" standard format
    [InlineData("$yyyy$/$MM$/x", @"2024\03\x")]
    [InlineData("$yyyy/MM$", @"2024\03")]
    [InlineData("$HH:mm$", "09_05")]
    [InlineData("cost$$5", "cost$5")]
    [InlineData("unclosed$yyyy", "unclosed$yyyy")]
    public void DateFormats(string template, string expected) => Assert.Equal(expected, X(template));

    [Fact]
    public void Variables()
    {
        Assert.Equal("800x600", X("%width%x%height%"));
        Assert.Equal("Windows", X("%os%"));
        Assert.Equal(Environment.MachineName, X("%computername%"));
        Assert.Equal("WINWORD.EXE", X("%process%"));
        Assert.Equal("WINWORD", X("%process!%"));
        Assert.Equal("Report_ Q1_Q2 _draft_", X("%title%"));
        Assert.Equal("Report_ Q1", X("%title#10%"));
        Assert.Equal("WINW", X("%process!#4%"));
        Assert.Equal("WINW", X("%process#4!%"));
    }

    [Fact]
    public void VariablesAreCaseInsensitive() => Assert.Equal("800", X("%WIDTH%"));

    [Fact]
    public void Uuid_IsGuid() => Assert.True(Guid.TryParse(X("%uuid%"), out _));

    [Fact]
    public void Timestamps()
    {
        var s = new DateTimeOffset(Ctx.Timestamp).ToUnixTimeSeconds().ToString();
        var ms = new DateTimeOffset(Ctx.Timestamp).ToUnixTimeMilliseconds().ToString();
        Assert.Equal(s, X("%timestamp%"));
        Assert.Equal(s, X("%unix%"));
        Assert.Equal(ms, X("%timestampms%"));
        Assert.Equal(ms, X("%unixms%"));
    }

    [Fact]
    public void EnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("OPENPIN_TEST_VAR", @"a\b:c");
        Assert.Equal("a_b_c", X("%OPENPIN_TEST_VAR%"));       // separators sanitized
        Assert.Equal(@"a\b_c", X("%OPENPIN_TEST_VAR&%"));    // separators kept, non-drive colon sanitized
        Environment.SetEnvironmentVariable("OPENPIN_TEST_ROOT", @"D:\Data\OneDrive");
        Assert.Equal(@"D:\Data\OneDrive\Shots\x", X(@"%OPENPIN_TEST_ROOT&%\Shots\x"));
        Assert.Equal(@"D__Data_OneDrive", X("%OPENPIN_TEST_ROOT%"));
    }

    [Theory]
    [InlineData("100% done", "100% done")]
    [InlineData("%nosuchvariable_xyz%", "%nosuchvariable_xyz%")]
    [InlineData("50%%", "50%")]
    [InlineData("%", "%")]
    public void UnknownAndLiteralPercent(string t, string expected) => Assert.Equal(expected, X(t));

    [Theory]
    [InlineData("a<b>c|d?e*f\"g", "a_b_c_d_e_f_g")]
    [InlineData("name. ", "name")]
    [InlineData("dir. /file", @"dir\file")]
    [InlineData("a//b\\\\c", @"a\b\c")]
    [InlineData("CON", "CON_")]
    [InlineData("nul.png", "nul_.png")]
    [InlineData("C:/shots/$yyyy$", @"C:\shots\2024")]
    [InlineData("x:y", "x_y")]
    public void Sanitizing(string t, string expected) => Assert.Equal(expected, X(t));

    [Fact]
    public void ControlCharsSanitized()
    {
        var ctx = new SaveContext { WindowTitle = "a\tb\nc" };
        Assert.Equal("a_b_c", PathTemplate.Expand("%title%", ctx, 1, 1));
    }

    [Fact]
    public void EmptyTitle_ProducesNoText() => Assert.Equal("shot_", PathTemplate.Expand("shot_%title%", new SaveContext(), 1, 1));

    [Fact]
    public void TruncateDoesNotSplitSurrogatePairs()
    {
        var ctx = new SaveContext { WindowTitle = "a😀b" };
        Assert.Equal("a", PathTemplate.Expand("%title#2%", ctx, 1, 1));
    }

    [Fact]
    public void ExpandToFullPath_CombinesAndAddsExtension()
    {
        var p = PathTemplate.ExpandToFullPath(@"C:\base", "$yyyy$/shot", Ctx, 1, 1);
        Assert.Equal(@"C:\base\2024\shot.png", p);
        Assert.Equal(@"C:\base\shot.jpg", PathTemplate.ExpandToFullPath(@"C:\base", "shot.jpg", Ctx, 1, 1));
        Assert.Equal(@"C:\base\a.b.png", PathTemplate.ExpandToFullPath(@"C:\base", "a.b", Ctx, 1, 1));
        Assert.Equal(@"D:\abs\x.png", PathTemplate.ExpandToFullPath(@"C:\base", @"D:\abs\x", Ctx, 1, 1));
        Assert.Equal(@"C:\base\shot.jpg", PathTemplate.ExpandToFullPath(@"C:\base", "shot", Ctx, 1, 1, ".jpg"));
    }

    [Fact]
    public void ExpandToFullPath_EmptyTemplateUsesDefault()
    {
        var p = PathTemplate.ExpandToFullPath(@"C:\base", "", Ctx, 1, 1);
        Assert.Equal(@"C:\base\OpenPin_2024-03-07_09-05-04-321.png", p);
    }

    [Fact]
    public void MakeUnique_AppendsCounter()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\a\x.png", @"C:\a\x (2).png" };
        Assert.Equal(@"C:\a\x (3).png", PathTemplate.MakeUnique(@"C:\a\x.png", existing.Contains));
        Assert.Equal(@"C:\a\y.png", PathTemplate.MakeUnique(@"C:\a\y.png", existing.Contains));
    }

    [Fact]
    public void MakeUnique_OnDisk()
    {
        var dir = Directory.CreateTempSubdirectory("openpin-test-").FullName;
        try
        {
            var p = Path.Combine(dir, "f.png");
            File.WriteAllText(p, "x");
            Assert.Equal(Path.Combine(dir, "f (2).png"), PathTemplate.MakeUnique(p));
        }
        finally { Directory.Delete(dir, true); }
    }
}
