using System.Text;

namespace OpenPin.Recognition;

/// <summary>A merged cell: anchor (top-left) plus its span, all 0-based.</summary>
public sealed record CellMerge(int Row, int Column, int RowSpan, int ColumnSpan)
{
    public bool Contains(int r, int c) => r >= Row && r < Row + RowSpan && c >= Column && c < Column + ColumnSpan;
}

/// <summary>
/// A recognized table: a rows × columns text grid plus merged-cell ranges (the text of a merged cell lives in its
/// anchor; the covered cells are empty). Cell texts may be edited; the structure is fixed.
/// </summary>
public sealed class TableData
{
    private readonly string[][] _cells;

    public TableData(int rows, int columns, IEnumerable<CellMerge>? merges = null)
    {
        RowCount = Math.Max(0, rows);
        ColumnCount = Math.Max(0, columns);
        _cells = Enumerable.Range(0, RowCount).Select(_ => Enumerable.Repeat("", ColumnCount).ToArray()).ToArray();
        Merges = (merges ?? Array.Empty<CellMerge>()).Where(m => m.RowSpan * m.ColumnSpan > 1).ToList();
    }

    public int RowCount { get; }
    public int ColumnCount { get; }
    public IReadOnlyList<CellMerge> Merges { get; }

    public string this[int row, int column]
    {
        get => _cells[row][column];
        set => _cells[row][column] = value ?? "";
    }

    public bool IsEmpty => RowCount == 0 || ColumnCount == 0;

    /// <summary>The merge covering (r, c) other than at its anchor, or null.</summary>
    public CellMerge? CoveredBy(int r, int c) => Merges.FirstOrDefault(m => m.Contains(r, c) && (m.Row != r || m.Column != c));

    /// <summary>Tab-separated text for the clipboard (pastes into Excel / WPS cell by cell).</summary>
    public string ToTsv() => Join('\t');

    /// <summary>RFC 4180 CSV (comma, CRLF, quotes doubled).</summary>
    public string ToCsv() => Join(',');

    private string Join(char separator)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < RowCount; r++)
        {
            for (int c = 0; c < ColumnCount; c++)
            {
                if (c > 0) sb.Append(separator);
                sb.Append(Quote(_cells[r][c], separator));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    internal static string Quote(string value, char separator)
    {
        if (value.IndexOfAny(new[] { separator, '"', '\r', '\n' }) < 0 && (value.Length == 0 || (value[0] != ' ' && value[^1] != ' ')))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
