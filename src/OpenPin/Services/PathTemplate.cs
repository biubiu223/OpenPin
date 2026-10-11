using System.Globalization;
using System.IO;
using System.Text;

namespace OpenPin.Services;

/// <summary>
/// File-name / path template engine used by save, quick save and auto save.
/// <list type="bullet">
/// <item><c>$&lt;.NET date format&gt;$</c> — e.g. <c>$yyyy-MM-dd_HH-mm-ss$</c>. Path separators inside the format create
/// sub folders (<c>$yyyy$/$MM$/x</c> or <c>$yyyy/MM$</c>). <c>$$</c> is a literal <c>$</c>.</item>
/// <item><c>%title%</c>, <c>%process%</c>, <c>%uuid%</c>, <c>%width%</c>, <c>%height%</c>, <c>%os%</c>, <c>%computername%</c>,
/// <c>%username%</c>, <c>%timestamp%</c> / <c>%unix%</c> (unix seconds), <c>%timestampms%</c> / <c>%unixms%</c> (unix ms),
/// and any environment variable <c>%NAME%</c>. Unknown names are kept literally. <c>%%</c> is a literal <c>%</c>.</item>
/// <item>Modifiers (any order, before the closing <c>%</c>): <c>#N</c> truncate to N characters, <c>!</c> strip a trailing
/// <c>.exe</c>, <c>&amp;</c> keep path separators (and a drive colon) in the value — e.g. <c>%OneDrive&amp;%\Shots</c>.
/// Without <c>&amp;</c>, separators inside values are replaced by <c>_</c>.</item>
/// </list>
/// Invalid file-name characters are replaced by <c>_</c>, trailing dots/spaces of path segments are removed and reserved
/// device names (CON, NUL, COM1…) are suffixed with <c>_</c>.
/// </summary>
public static class PathTemplate
{
    private const char Sep = '\\';

    /// <summary>
    /// Expands <paramref name="template"/>. The result uses <c>\</c> as separator and may be relative (sub folders) or
    /// absolute (only when the template starts with a drive / UNC path or a <c>&amp;</c>-variable that expands to one).
    /// </summary>
    public static string Expand(string template, SaveContext? context, int imageWidth, int imageHeight)
    {
        context ??= new SaveContext();
        var sb = new StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '$')
            {
                int end = template.IndexOf('$', i + 1);
                if (end < 0) { sb.Append(SanitizeLiteral("$")); i++; continue; }
                var fmt = template.Substring(i + 1, end - i - 1);
                if (fmt.Length == 0) sb.Append('$');
                else sb.Append(SanitizeLiteral(FormatDate(context.Timestamp, fmt) ?? "$" + fmt + "$"));
                i = end + 1;
                continue;
            }
            if (c == '%')
            {
                if (i + 1 < template.Length && template[i + 1] == '%') { sb.Append('%'); i += 2; continue; }
                if (TryParseVariable(template, i, out var name, out var mods, out int next) &&
                    ResolveVariable(name, context, imageWidth, imageHeight) is { } value)
                {
                    sb.Append(ApplyModifiers(value, mods));
                    i = next;
                    continue;
                }
                sb.Append('%');
                i++;
                continue;
            }
            sb.Append(SanitizeLiteral(c.ToString()));
            i++;
        }
        return NormalizePath(sb.ToString());
    }

    /// <summary>
    /// Expands <paramref name="template"/> and combines it with <paramref name="baseFolder"/> (unless the expansion is already
    /// absolute). Appends <paramref name="defaultExtension"/> (".png") when the result has no known image extension.
    /// </summary>
    public static string ExpandToFullPath(string baseFolder, string template, SaveContext? context, int imageWidth, int imageHeight,
        string defaultExtension = ".png")
    {
        var rel = Expand(string.IsNullOrWhiteSpace(template) ? "OpenPin_$yyyy-MM-dd_HH-mm-ss-fff$" : template, context, imageWidth, imageHeight);
        if (rel.Length == 0 || rel.EndsWith(Sep)) rel += "OpenPin";
        if (!SaveService.IsSupportedExtension(Path.GetExtension(rel)))
            rel += defaultExtension.StartsWith('.') ? defaultExtension : "." + defaultExtension;
        return Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(baseFolder, rel));
    }

    /// <summary>Returns <paramref name="path"/> or, if a file/folder with that name exists, "name (2).ext", "name (3).ext"…</summary>
    public static string MakeUnique(string path) => MakeUnique(path, p => File.Exists(p) || Directory.Exists(p));

    /// <summary>Testable variant of <see cref="MakeUnique(string)"/>.</summary>
    public static string MakeUnique(string path, Func<string, bool> exists)
    {
        if (!exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 2; n < 100000; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>Replaces characters that are invalid in a file name (including path separators) by '_'.</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(IsInvalidFileNameChar(ch) ? '_' : ch);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ internals

    private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));

    private static bool IsInvalidFileNameChar(char ch) => ch < 32 || InvalidFileNameChars.Contains(ch);

    /// <summary>Literal template text: separators are kept (normalized to '\'), other invalid chars → '_'.
    /// A drive colon ("C:") is kept by <see cref="NormalizePath"/> via a placeholder.</summary>
    private static string SanitizeLiteral(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '/' or '\\') sb.Append(Sep);
            else if (ch == ':') sb.Append(DriveColon);
            else sb.Append(IsInvalidFileNameChar(ch) ? '_' : ch);
        }
        return sb.ToString();
    }

    // A private-use char marks a ':' that may survive as a drive colon (only valid at index 1 of the final path).
    private const char DriveColon = '\uE000';

    private static string? FormatDate(DateTime ts, string fmt)
    {
        try
        {
            // A single-character format would be a *standard* format ("d" = short date); '%' forces a custom one.
            var f = fmt.Length == 1 ? "%" + fmt : fmt;
            return ts.ToString(f, CultureInfo.InvariantCulture);
        }
        catch (FormatException) { return null; }
    }

    private static bool TryParseVariable(string t, int start, out string name, out string mods, out int next)
    {
        name = mods = "";
        next = start;
        int i = start + 1;
        int nameStart = i;
        while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] is '_' or '(' or ')' or '.' or '-')) i++;
        if (i == nameStart) return false;
        name = t.Substring(nameStart, i - nameStart);
        int modStart = i;
        while (i < t.Length && (t[i] is '!' or '&' || t[i] == '#' || (char.IsDigit(t[i]) && i > modStart))) i++;
        if (i >= t.Length || t[i] != '%') return false;
        mods = t.Substring(modStart, i - modStart);
        next = i + 1;
        return true;
    }

    private static string? ResolveVariable(string name, SaveContext ctx, int w, int h)
    {
        switch (name.ToLowerInvariant())
        {
            case "title": return ctx.WindowTitle ?? "";
            case "process": return ctx.ProcessName ?? "";
            case "uuid": case "guid": return Guid.NewGuid().ToString("D");
            case "width": return w.ToString(CultureInfo.InvariantCulture);
            case "height": return h.ToString(CultureInfo.InvariantCulture);
            case "os": return "Windows";
            case "computername": return Environment.MachineName;
            case "username": return Environment.UserName;
            case "timestamp": case "unix":
                return new DateTimeOffset(ctx.Timestamp).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "timestampms": case "timestamp_ms": case "unixms":
                return new DateTimeOffset(ctx.Timestamp).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }
        try { return Environment.GetEnvironmentVariable(name); }
        catch { return null; }
    }

    private static string ApplyModifiers(string value, string mods)
    {
        bool keepSeparators = mods.Contains('&');
        if (mods.Contains('!') && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];

        int hash = mods.IndexOf('#');
        if (hash >= 0)
        {
            int j = hash + 1;
            while (j < mods.Length && char.IsDigit(mods[j])) j++;
            if (int.TryParse(mods.AsSpan(hash + 1, j - hash - 1), out var max) && max >= 0 && value.Length > max)
            {
                // Do not cut a surrogate pair in half.
                if (max > 0 && char.IsHighSurrogate(value[max - 1])) max--;
                value = value[..max];
            }
        }

        var sb = new StringBuilder(value.Length);
        for (int k = 0; k < value.Length; k++)
        {
            char ch = value[k];
            if (ch is '/' or '\\') sb.Append(keepSeparators ? Sep : '_');
            else if (ch == ':' && keepSeparators) sb.Append(DriveColon);
            else sb.Append(IsInvalidFileNameChar(ch) ? '_' : ch);
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Resolve drive colons, collapse separators, trim segments, protect reserved names.</summary>
    private static string NormalizePath(string raw)
    {
        // Root detection: "X:\..." (drive) or "\\server\share" (UNC).
        string root = "";
        string rest = raw;
        if (raw.Length >= 2 && char.IsAsciiLetter(raw[0]) && raw[1] == DriveColon && (raw.Length == 2 || raw[2] == Sep))
        {
            root = raw[0] + ":";
            rest = raw[2..];
            if (rest.Length > 0 && rest[0] == Sep) root += Sep;
        }
        else if (raw.StartsWith(@"\\", StringComparison.Ordinal))
        {
            root = @"\\";
            rest = raw[2..];
        }
        rest = rest.Replace(DriveColon, '_');

        bool trailingSep = rest.EndsWith(Sep);
        var segments = rest.Split(Sep, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.TrimEnd(' ', '.').TrimStart(' '))
            .Where(s => s.Length > 0)
            .Select(s => ReservedNames.Contains(Path.GetFileNameWithoutExtension(s)) ? s.Insert(Path.GetFileNameWithoutExtension(s).Length, "_") : s)
            .ToList();
        var result = root + string.Join(Sep, segments);
        if (trailingSep && segments.Count > 0) result += Sep;
        return result;
    }
}
