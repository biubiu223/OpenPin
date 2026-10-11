namespace OpenPin.Translate;

/// <summary>
/// For line-based APIs (Baidu, Youdao): flattens segments into their non-blank lines, and rebuilds the segments from
/// the translated lines (blank lines are kept as they were). Pure, unit tested.
/// </summary>
public sealed class LineFlattener
{
    private readonly List<string[]> _segmentLines = new();

    /// <summary>Non-blank lines of all segments, in order — what is sent to the service.</summary>
    public IReadOnlyList<string> Lines { get; }

    public LineFlattener(IReadOnlyList<string> segments)
    {
        var lines = new List<string>();
        foreach (var s in segments)
        {
            var parts = TranslationHttp.Lines(s ?? "");
            _segmentLines.Add(parts);
            foreach (var p in parts)
                if (!string.IsNullOrWhiteSpace(p)) lines.Add(p.Trim());
        }
        Lines = lines;
    }

    /// <summary>Segments rebuilt from one translated line per entry of <see cref="Lines"/>.</summary>
    public IReadOnlyList<string> Rebuild(IReadOnlyList<string> translatedLines)
    {
        if (translatedLines.Count != Lines.Count)
            throw new TranslationException(Core.Loc.Format("Translate.Error.Count", Lines.Count, translatedLines.Count));
        var result = new List<string>(_segmentLines.Count);
        int k = 0;
        foreach (var parts in _segmentLines)
        {
            var outLines = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                outLines[i] = string.IsNullOrWhiteSpace(parts[i]) ? "" : translatedLines[k++];
            result.Add(string.Join("\n", outLines));
        }
        return result;
    }
}
