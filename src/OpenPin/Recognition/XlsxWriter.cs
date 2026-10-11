using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace OpenPin.Recognition;

/// <summary>
/// Minimal Office Open XML spreadsheet writer (no dependencies): one sheet, shared strings, numbers stored as numbers,
/// thin borders + wrapped text, merged cells and column widths. Opens in Excel, WPS and LibreOffice.
/// </summary>
public static class XlsxWriter
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly Regex NumberPattern = new(@"^[-+]?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$", RegexOptions.Compiled);

    public static void Save(string path, TableData table, string sheetName = "Sheet1")
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) Write(fs, table, sheetName);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Write(Stream output, TableData table, string sheetName = "Sheet1")
    {
        // Shared strings + cell values.
        var strings = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int stringRefs = 0;

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Entry(zip, "[Content_Types].xml", w =>
        {
            w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(w, "xml", "application/xml");
            Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(w, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            Override(w, "/xl/sharedStrings.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml");
            w.WriteEndElement();
        });
        Entry(zip, "_rels/.rels", w =>
        {
            w.WriteStartElement("Relationships", PkgRelNs);
            Rel(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            w.WriteEndElement();
        });
        Entry(zip, "xl/workbook.xml", w =>
        {
            w.WriteStartElement("workbook", MainNs);
            w.WriteAttributeString("xmlns", "r", null, RelNs);
            w.WriteStartElement("sheets", MainNs);
            w.WriteStartElement("sheet", MainNs);
            w.WriteAttributeString("name", SafeSheetName(sheetName));
            w.WriteAttributeString("sheetId", "1");
            w.WriteAttributeString("id", RelNs, "rId1");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        });
        Entry(zip, "xl/_rels/workbook.xml.rels", w =>
        {
            w.WriteStartElement("Relationships", PkgRelNs);
            Rel(w, "rId1", RelNs + "/worksheet", "worksheets/sheet1.xml");
            Rel(w, "rId2", RelNs + "/styles", "styles.xml");
            Rel(w, "rId3", RelNs + "/sharedStrings", "sharedStrings.xml");
            w.WriteEndElement();
        });
        RawEntry(zip, "xl/styles.xml", StylesXml);
        Entry(zip, "xl/worksheets/sheet1.xml", w =>
        {
            w.WriteStartElement("worksheet", MainNs);
            w.WriteAttributeString("xmlns", "r", null, RelNs);
            if (!table.IsEmpty)
            {
                w.WriteStartElement("dimension", MainNs);
                w.WriteAttributeString("ref", $"A1:{CellRef(table.RowCount - 1, table.ColumnCount - 1)}");
                w.WriteEndElement();
            }
            w.WriteStartElement("sheetViews", MainNs);
            w.WriteStartElement("sheetView", MainNs);
            w.WriteAttributeString("workbookViewId", "0");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("sheetFormatPr", MainNs);
            w.WriteAttributeString("defaultRowHeight", "15");
            w.WriteEndElement();

            if (table.ColumnCount > 0)
            {
                w.WriteStartElement("cols", MainNs);
                for (int c = 0; c < table.ColumnCount; c++)
                {
                    w.WriteStartElement("col", MainNs);
                    w.WriteAttributeString("min", (c + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("max", (c + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("width", ColumnWidth(table, c).ToString("0.##", CultureInfo.InvariantCulture));
                    w.WriteAttributeString("customWidth", "1");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }

            w.WriteStartElement("sheetData", MainNs);
            for (int r = 0; r < table.RowCount; r++)
            {
                w.WriteStartElement("row", MainNs);
                w.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
                for (int c = 0; c < table.ColumnCount; c++)
                {
                    var text = Sanitize(table[r, c]);
                    w.WriteStartElement("c", MainNs);
                    w.WriteAttributeString("r", CellRef(r, c));
                    w.WriteAttributeString("s", "1");
                    if (table.CoveredBy(r, c) == null && text.Length > 0)
                    {
                        if (TryParseNumber(text, out var number))
                        {
                            w.WriteElementString("v", MainNs, number.ToString("R", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            if (!index.TryGetValue(text, out var si))
                            {
                                si = strings.Count;
                                strings.Add(text);
                                index[text] = si;
                            }
                            stringRefs++;
                            w.WriteAttributeString("t", "s");
                            w.WriteElementString("v", MainNs, si.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement(); // sheetData

            var merges = table.Merges.Where(m => m.Row + m.RowSpan <= table.RowCount && m.Column + m.ColumnSpan <= table.ColumnCount).ToList();
            if (merges.Count > 0)
            {
                w.WriteStartElement("mergeCells", MainNs);
                w.WriteAttributeString("count", merges.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var m in merges)
                {
                    w.WriteStartElement("mergeCell", MainNs);
                    w.WriteAttributeString("ref", $"{CellRef(m.Row, m.Column)}:{CellRef(m.Row + m.RowSpan - 1, m.Column + m.ColumnSpan - 1)}");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }

            w.WriteStartElement("pageMargins", MainNs);
            foreach (var (k, v) in new[] { ("left", "0.7"), ("right", "0.7"), ("top", "0.75"), ("bottom", "0.75"), ("header", "0.3"), ("footer", "0.3") })
                w.WriteAttributeString(k, v);
            w.WriteEndElement();
            w.WriteEndElement(); // worksheet
        });
        Entry(zip, "xl/sharedStrings.xml", w =>
        {
            w.WriteStartElement("sst", MainNs);
            w.WriteAttributeString("count", stringRefs.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("uniqueCount", strings.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var s in strings)
            {
                w.WriteStartElement("si", MainNs);
                w.WriteStartElement("t", MainNs);
                if (s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]) || s.Contains('\n')))
                    w.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                w.WriteString(s);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        });
    }

    // Constant style sheet: xf 0 = default, xf 1 = thin border + vertically centred + wrapped (all table cells).
    private const string StylesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"" + MainNs + "\">" +
            "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left style=\"thin\"><color auto=\"1\"/></left><right style=\"thin\"><color auto=\"1\"/></right>" +
            "<top style=\"thin\"><color auto=\"1\"/></top><bottom style=\"thin\"><color auto=\"1\"/></bottom><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

    // ------------------------------------------------------------------ helpers

    private static void Entry(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        w.WriteStartDocument(standalone: true);
        write(w);
        w.WriteEndDocument();
    }

    private static void RawEntry(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void Default(XmlWriter w, string ext, string type)
    {
        w.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types");
        w.WriteAttributeString("Extension", ext);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Override(XmlWriter w, string part, string type)
    {
        w.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types");
        w.WriteAttributeString("PartName", part);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Rel(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", PkgRelNs);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    public static string ColumnName(int index)
    {
        var sb = new StringBuilder();
        for (int n = index + 1; n > 0; n = (n - 1) / 26) sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    public static string CellRef(int row, int column) => ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Plain decimal numbers ("12", "-3.5", "1,234.50") become numeric cells. Kept as text: leading zeros ("007"),
    /// more than 15 significant digits (IDs, card numbers), currency / percent / units.
    /// </summary>
    public static bool TryParseNumber(string text, out double value)
    {
        value = 0;
        var s = text.Trim();
        if (s.Length == 0 || s.Length > 24 || !NumberPattern.IsMatch(s)) return false;
        var plain = s.Replace(",", "");
        var digits = plain.TrimStart('-', '+');
        var intPart = digits.Split('.')[0];
        if (intPart.Length > 1 && intPart[0] == '0') return false;
        if (digits.Count(char.IsDigit) > 15) return false;
        return double.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Drop characters XML 1.0 cannot carry; normalise line breaks.</summary>
    internal static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
            if (c == '\r') { if (i + 1 < s.Length && s[i + 1] == '\n') continue; sb.Append('\n'); continue; }
            if (c == '\t' || c == '\n' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string SafeSheetName(string name)
    {
        var n = new string((name ?? "").Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
        if (n.Length == 0) n = "Sheet1";
        return n.Length > 31 ? n[..31] : n;
    }

    /// <summary>Approximate column width in characters (CJK counts double), 6–60.</summary>
    private static double ColumnWidth(TableData table, int c)
    {
        double max = 0;
        for (int r = 0; r < table.RowCount; r++)
        {
            if (table.Merges.Any(m => m.Contains(r, c) && m.ColumnSpan > 1)) continue;
            foreach (var line in table[r, c].Split('\n'))
                max = Math.Max(max, line.Sum(ch => ch > 0x2E80 ? 2.0 : 1.0));
        }
        return Math.Clamp(max + 2, 6, 60);
    }
}
