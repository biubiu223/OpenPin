using System.Text.RegularExpressions;

namespace OpenPin.Recognition;

/// <summary>
/// Cleans raw output from LLMs or users, stripping markdown fences, reasoning blocks,
/// and surrounding math delimiters to produce clean LaTeX formula strings.
/// </summary>
public static class LatexCleaner
{
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Trim();

        // Strip thinking/reasoning tags (DeepSeek-R1, Qwen etc.)
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();

        // Extract content from markdown code fences: ```latex ... ``` or ```tex ... ``` or ``` ... ```
        var fenceMatch = Regex.Match(text, @"```(?:latex|tex)?(?:\r?\n|\\n)?\s*(.*?)\s*(?:\\n|\r?\n)?```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (fenceMatch.Success)
        {
            text = fenceMatch.Groups[1].Value.Trim();
        }

        if (text.StartsWith("\\n", StringComparison.Ordinal)) text = text[2..].Trim();
        if (text.EndsWith("\\n", StringComparison.Ordinal)) text = text[..^2].Trim();

        // Strip common AI introductory prefixes like "Here is the LaTeX formula:" or "LaTeX:"
        text = Regex.Replace(text, @"^(?:Here is the (?:LaTeX )?formula:?|LaTeX formula:?|LaTeX:?|Formula:?|Result:?)\s*", "", RegexOptions.IgnoreCase).Trim();

        // Strip equation environments if wrapped around the whole thing
        var envMatch = Regex.Match(text, @"^\\begin\{(?:equation|align|gather|displaymath)\*?\}(.*?)\\end\{(?:equation|align|gather|displaymath)\*?\}$", RegexOptions.Singleline);
        if (envMatch.Success)
        {
            text = envMatch.Groups[1].Value.Trim();
        }

        // Strip $$...$$ or \[...\] or \(...\) or $...$
        if (text.StartsWith("$$", StringComparison.Ordinal) && text.EndsWith("$$", StringComparison.Ordinal) && text.Length >= 4)
            text = text[2..^2].Trim();
        else if (text.StartsWith("\\[", StringComparison.Ordinal) && text.EndsWith("\\]", StringComparison.Ordinal) && text.Length >= 4)
            text = text[2..^2].Trim();
        else if (text.StartsWith("\\(", StringComparison.Ordinal) && text.EndsWith("\\)", StringComparison.Ordinal) && text.Length >= 4)
            text = text[2..^2].Trim();
        else if (text.StartsWith('$') && text.EndsWith('$') && text.Length >= 2)
            text = text[1..^1].Trim();

        // Normalize line breaks
        text = text.Replace("\r\n", "\n").Trim();

        return text;
    }
}
