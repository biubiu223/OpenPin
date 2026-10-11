using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Xml.Linq;
using OpenPin.Recognition;
using OpenPin.Services;

namespace OpenPin.Tests.Recognition;

public class TableReconstructionTests
{
    private static WordBox W(string t, double x, double y, double w = 40, double h = 16) => new(t, new Rect(x, y, w, h));

    /// <summary>A 3×3 table laid out on a regular grid: columns at x = 10 / 150 / 290, rows at y = 10 / 40 / 70.</summary>
    private static List<WordBox> Grid3x3() => new()
    {
        W("Name", 10, 10), W("Qty", 150, 10, 30), W("Price", 290, 10),
        W("Apple", 10, 40), W("3", 150, 40, 10), W("1.50", 290, 40, 32),
        W("Pear", 10, 70), W("12", 150, 70, 18), W("0.80", 290, 70, 32),
    };

    [Fact]
    public void WordsOnly_RowsAndColumnsFromAlignment()
    {
        var t = TableReconstructor.Build(Grid3x3());
        Assert.Equal(3, t.RowCount);
        Assert.Equal(3, t.ColumnCount);
        Assert.Equal("Name", t[0, 0]);
        Assert.Equal("12", t[2, 1]);
        Assert.Equal("0.80", t[2, 2]);
        Assert.Empty(t.Merges);
    }

    [Fact]
    public void WordsOnly_MultiWordCellsAndCjkCharacters()
    {
        var words = new List<WordBox>
        {
            W("New", 10, 10, 30), W("York", 44, 10, 34), W("8.3M", 200, 10),
            // Chinese: one OCR word per character, tiny gaps
            W("北", 10, 40, 16), W("京", 27, 40, 16), W("21.5M", 200, 40),
        };
        var t = TableReconstructor.Build(words);
        Assert.Equal(2, t.RowCount);
        Assert.Equal(2, t.ColumnCount);
        Assert.Equal("New York", t[0, 0]);
        Assert.Equal("北京", t[1, 0]);
        Assert.Equal("21.5M", t[1, 1]);
    }

    [Fact]
    public void WordsOnly_TitleSpanningColumns_BecomesMergedCell()
    {
        var words = Grid3x3().Select(w => w with { Bounds = new Rect(w.Bounds.X, w.Bounds.Y + 40, w.Bounds.Width, w.Bounds.Height) }).ToList();
        words.Add(W("Quarterly", 100, 10, 70));
        words.Add(W("fruit", 174, 10, 40));
        words.Add(W("report", 218, 10, 50)); // one chunk from x=100 to 268: crosses the column separators
        var t = TableReconstructor.Build(words);
        Assert.Equal(4, t.RowCount);
        Assert.Equal(3, t.ColumnCount);
        var m = Assert.Single(t.Merges);
        Assert.Equal(0, m.Row);
        Assert.True(m.ColumnSpan >= 2);
        Assert.Equal("Quarterly fruit report", t[0, m.Column]);
        Assert.Equal("Apple", t[2, 0]);
    }

    [Fact]
    public void RulingGrid_DefinesCells_AndMissingSegmentsMergeCells()
    {
        // Grid: x lines at 0, 100, 200, 300; y lines at 0, 30, 60, 90.
        var lines = new List<RulingLine>();
        foreach (var y in new[] { 0, 30, 60, 90 }) lines.Add(new RulingLine(true, y, 0, 300, 1));
        // Vertical line x=100 is missing in the first row (header "Fruit stock" spans two columns).
        lines.Add(new RulingLine(false, 0, 0, 90, 1));
        lines.Add(new RulingLine(false, 100, 30, 90, 1));
        lines.Add(new RulingLine(false, 200, 0, 90, 1));
        lines.Add(new RulingLine(false, 300, 0, 90, 1));
        var words = new List<WordBox>
        {
            W("Fruit", 60, 7), W("stock", 104, 7), W("Total", 210, 7),
            W("Apple", 10, 37), W("3", 110, 37, 10), W("4.50", 210, 37),
            W("Pear", 10, 67), W("12", 110, 67, 18), W("9.60", 210, 67),
        };
        var t = TableReconstructor.Build(words, lines);
        Assert.Equal(3, t.RowCount);
        Assert.Equal(3, t.ColumnCount);
        var m = Assert.Single(t.Merges);
        Assert.Equal(new CellMerge(0, 0, 1, 2), m);
        Assert.Equal("Fruit stock", t[0, 0]);
        Assert.Equal("", t[0, 1]);
        Assert.Equal("Total", t[0, 2]);
        Assert.Equal("12", t[2, 1]);
    }

    [Fact]
    public void RulingGrid_VerticalMerge()
    {
        var lines = new List<RulingLine>
        {
            new(true, 0, 0, 200, 1), new(true, 30, 100, 200, 1), new(true, 60, 0, 200, 1), // y=30 missing in column 0
            new(false, 0, 0, 60, 1), new(false, 100, 0, 60, 1), new(false, 200, 0, 60, 1),
        };
        var words = new List<WordBox> { W("Group", 10, 22), W("a", 110, 7, 10), W("b", 110, 37, 10) };
        var t = TableReconstructor.Build(words, lines);
        Assert.Equal(2, t.RowCount);
        Assert.Equal(new CellMerge(0, 0, 2, 1), Assert.Single(t.Merges));
        Assert.Equal("Group", t[0, 0]);
        Assert.Equal("b", t[1, 1]);
    }

    [Fact]
    public void HorizontalRulesOnly_RowsFromRules_WrappedCellTextStaysInOneRow()
    {
        var lines = new List<RulingLine> { new(true, 0, 0, 400, 1), new(true, 50, 0, 400, 1), new(true, 80, 0, 400, 1) };
        var words = new List<WordBox>
        {
            W("Long", 10, 5), W("description", 54, 5, 80), W("10", 300, 15, 18),
            W("wrapped", 10, 26, 60),                         // second line of the same cell
            W("Short", 10, 57), W("20", 300, 57, 18),
        };
        var t = TableReconstructor.Build(words, lines);
        Assert.Equal(2, t.RowCount);
        Assert.Equal(2, t.ColumnCount);
        Assert.Equal("Long description wrapped", t[0, 0]);
        Assert.Equal("20", t[1, 1]);
    }

    [Fact]
    public void Empty() => Assert.True(TableReconstructor.Build(new List<WordBox>()).IsEmpty);

    [Fact]
    public void WordsOf_FlattensOcrResult()
    {
        var ocr = new OcrResult(new[]
        {
            new OcrLine("a b", new[] { new OcrWord("a", new Rect(0, 0, 5, 5)), new OcrWord("b", new Rect(10, 0, 5, 5)) }, new Rect(0, 0, 15, 5)),
        }, "en");
        Assert.Equal(2, TableReconstructor.WordsOf(ocr).Count);
    }
}

public class RulingLineTests
{
    private static byte[] White(int w, int h)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        return px;
    }

    private static void Dark(byte[] px, int w, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 40;
        }
    }

    [Fact]
    public void DetectsGridLines_IgnoresTextBlobs()
    {
        int w = 300, h = 120;
        var px = White(w, h);
        foreach (var y in new[] { 0, 40, 80, 119 }) Dark(px, w, 0, y, w, y + 1);    // 4 horizontal rules
        foreach (var x in new[] { 0, 150, 299 }) Dark(px, w, x, 0, x + 1, h);       // 3 vertical rules
        Dark(px, w, 160, 10, 175, 30);                                              // a "glyph" (15×20 filled)
        Dark(px, w, 20, 55, 60, 57);                                                // a short underline (40 px)

        var lines = RulingLineDetector.Detect(px, w, h);
        var hs = lines.Where(l => l.Horizontal).Select(l => (int)l.Position).OrderBy(p => p).ToList();
        var vs = lines.Where(l => !l.Horizontal).Select(l => (int)l.Position).OrderBy(p => p).ToList();
        Assert.Equal(new[] { 0, 40, 80, 119 }, hs);
        Assert.Equal(new[] { 0, 150, 299 }, vs);
        Assert.All(lines.Where(l => l.Horizontal), l => Assert.True(l.Length >= w - 2));
    }

    [Fact]
    public void BrokenVerticalLine_GivesSegments()
    {
        int w = 200, h = 100;
        var px = White(w, h);
        Dark(px, w, 100, 0, 101, 30);
        Dark(px, w, 100, 50, 101, 100);
        var v = RulingLineDetector.Detect(px, w, h).Where(l => !l.Horizontal).OrderBy(l => l.Start).ToList();
        Assert.Equal(2, v.Count);
        Assert.Equal(0, v[0].Start);
        Assert.Equal(30, v[0].End);
        Assert.Equal(50, v[1].Start);
    }

    [Fact]
    public void ThickBars_AreNotLines()
    {
        int w = 200, h = 100;
        var px = White(w, h);
        Dark(px, w, 0, 20, w, 40); // 20 px tall filled bar
        Assert.Empty(RulingLineDetector.Detect(px, w, h).Where(l => l.Horizontal));
    }
}

public class XlsxTests
{
    private static XDocument Part(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        Assert.NotNull(e);
        using var s = e!.Open();
        return XDocument.Load(s);
    }

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void WritesValidMinimalWorkbook_WithNumbersStringsAndMerges()
    {
        var t = new TableData(3, 3, new[] { new CellMerge(0, 0, 1, 2) });
        t[0, 0] = "Fruit & <stock>"; t[0, 2] = "Total";
        t[1, 0] = "Apple"; t[1, 1] = "3"; t[1, 2] = "1,234.50";
        t[2, 0] = "007"; t[2, 1] = "-0.5"; t[2, 2] = "Apple";
        using var ms = new MemoryStream();
        XlsxWriter.Write(ms, t);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        foreach (var name in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/sharedStrings.xml", "xl/worksheets/sheet1.xml" })
            Assert.NotNull(zip.GetEntry(name));

        var types = Part(zip, "[Content_Types].xml").ToString();
        Assert.Contains("/xl/worksheets/sheet1.xml", types);
        Assert.Contains("spreadsheetml.sheet.main+xml", types);

        var sst = Part(zip, "xl/sharedStrings.xml");
        var strings = sst.Root!.Elements(Main + "si").Select(si => si.Element(Main + "t")!.Value).ToList();
        Assert.Equal(new[] { "Fruit & <stock>", "Total", "Apple", "007" }, strings); // "Apple" stored once
        Assert.Equal("5", sst.Root.Attribute("count")!.Value);
        Assert.Equal("4", sst.Root.Attribute("uniqueCount")!.Value);

        var sheet = Part(zip, "xl/worksheets/sheet1.xml");
        var cells = sheet.Descendants(Main + "c").ToDictionary(c => c.Attribute("r")!.Value);
        Assert.Equal(9, cells.Count);
        Assert.Equal("s", cells["A1"].Attribute("t")!.Value);
        Assert.Null(cells["B2"].Attribute("t"));                  // number
        Assert.Equal("3", cells["B2"].Element(Main + "v")!.Value);
        Assert.Equal("1234.5", cells["C2"].Element(Main + "v")!.Value);
        Assert.Equal("-0.5", cells["B3"].Element(Main + "v")!.Value);
        Assert.Equal("s", cells["A3"].Attribute("t")!.Value);    // leading zero kept as text
        Assert.Null(cells["B1"].Element(Main + "v"));             // covered by the merge
        Assert.Equal("A1:C3", sheet.Descendants(Main + "dimension").Single().Attribute("ref")!.Value);
        Assert.Equal("A1:B1", sheet.Descendants(Main + "mergeCell").Single().Attribute("ref")!.Value);
        Assert.Equal(3, sheet.Descendants(Main + "col").Count());

        // element order required by the schema
        var order = sheet.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(new[] { "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "mergeCells", "pageMargins" }, order);

        var wb = Part(zip, "xl/workbook.xml");
        Assert.Equal("rId1", wb.Descendants(Main + "sheet").Single().Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"))!.Value);
        var styles = Part(zip, "xl/styles.xml");
        Assert.Equal(2, styles.Descendants(Main + "fill").Count());
    }

    [Fact]
    public void InvalidXmlCharacters_AreDropped()
    {
        var t = new TableData(1, 1);
        t[0, 0] = "a\u0001b\r\nc";
        using var ms = new MemoryStream();
        XlsxWriter.Write(ms, t);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Equal("ab\nc", Part(zip, "xl/sharedStrings.xml").Descendants(Main + "t").Single().Value);
    }

    [Theory]
    [InlineData("12", true, 12)]
    [InlineData("-3.25", true, -3.25)]
    [InlineData("1,234,567.8", true, 1234567.8)]
    [InlineData("0.5", true, 0.5)]
    [InlineData("0", true, 0)]
    [InlineData("007", false, 0)]
    [InlineData("12%", false, 0)]
    [InlineData("¥12", false, 0)]
    [InlineData("1,23", false, 0)]
    [InlineData("1234567890123456", false, 0)]
    [InlineData("", false, 0)]
    public void Numbers(string text, bool ok, double value)
    {
        Assert.Equal(ok, XlsxWriter.TryParseNumber(text, out var v));
        if (ok) Assert.Equal(value, v, 9);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ColumnNames(int index, string name) => Assert.Equal(name, XlsxWriter.ColumnName(index));

    [Fact]
    public void TsvAndCsv_Quoting()
    {
        var t = new TableData(2, 2);
        t[0, 0] = "a"; t[0, 1] = "b,c";
        t[1, 0] = "line1\nline2"; t[1, 1] = "say \"hi\"";
        Assert.Equal("a\tb,c\r\n\"line1\nline2\"\t\"say \"\"hi\"\"\"\r\n", t.ToTsv());
        Assert.Equal("a,\"b,c\"\r\n\"line1\nline2\",\"say \"\"hi\"\"\"\r\n", t.ToCsv());
    }
}

public class TextSnapTests
{
    private static OcrResult Ocr() => new(new[]
    {
        new OcrLine("Hello world", new[] { new OcrWord("Hello", new Rect(10, 10, 50, 20)), new OcrWord("world", new Rect(70, 10, 50, 20)) }, new Rect(10, 10, 110, 20)),
        new OcrLine("Second", new[] { new OcrWord("Second", new Rect(10, 50, 60, 20)) }, new Rect(10, 50, 60, 20)),
    }, "en");

    [Fact]
    public void WordOrLineUnderPoint()
    {
        Assert.Equal(new Rect(8, 8, 114, 24), TextSnap.Find(Ocr(), new Point(20, 20), wholeLine: true));
        Assert.Equal(new Rect(68, 8, 54, 24), TextSnap.Find(Ocr(), new Point(80, 20), wholeLine: false));
        // in the gap between two words: the line, or the nearest word
        Assert.Equal(new Rect(8, 8, 114, 24), TextSnap.Find(Ocr(), new Point(65, 20), wholeLine: true));
        Assert.NotNull(TextSnap.Find(Ocr(), new Point(65, 20), wholeLine: false));
        Assert.Equal(new Rect(8, 48, 64, 24), TextSnap.Find(Ocr(), new Point(30, 60), wholeLine: true));
    }

    [Fact]
    public void NothingUnderPoint() => Assert.Null(TextSnap.Find(Ocr(), new Point(300, 300), wholeLine: true));

    [Fact]
    public void NoOcr() => Assert.Null(TextSnap.Find(null, new Point(1, 1), wholeLine: true));
}
