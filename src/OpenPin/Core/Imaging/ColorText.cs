using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace OpenPin.Core.Imaging;

public enum ColorFormat { Rgb, Hex, Hsl, Hsv }

/// <summary>Color ⇄ text conversions used by the magnifier color picker, color pins and settings.</summary>
public static partial class ColorText
{
    public static string Format(Color c, ColorFormat format) => format switch
    {
        ColorFormat.Hex => $"#{c.R:X2}{c.G:X2}{c.B:X2}",
        ColorFormat.Rgb => $"{c.R}, {c.G}, {c.B}",
        ColorFormat.Hsl => FormatHsl(c),
        ColorFormat.Hsv => FormatHsv(c),
        _ => $"#{c.R:X2}{c.G:X2}{c.B:X2}",
    };

    /// <summary>CSS-style text: "rgb(41, 184, 219)", "hsl(…)", "hsv(…)" or "#29B8DB".</summary>
    public static string FormatCss(Color c, ColorFormat format) => format switch
    {
        ColorFormat.Rgb => $"rgb({c.R}, {c.G}, {c.B})",
        ColorFormat.Hsl => $"hsl({FormatHsl(c)})",
        ColorFormat.Hsv => $"hsv({FormatHsv(c)})",
        _ => Format(c, ColorFormat.Hex),
    };

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = Hue(r, g, b, max, min), l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (h, s, l);
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return (Hue(r, g, b, max, min), max == 0 ? 0 : (max - min) / max, max);
    }

    private static double Hue(double r, double g, double b, double max, double min)
    {
        double d = max - min;
        if (d == 0) return 0;
        double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static string FormatHsl(Color c)
    {
        var (h, s, l) = ToHsl(c);
        return $"{Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%";
    }

    private static string FormatHsv(Color c)
    {
        var (h, s, v) = ToHsv(c);
        return $"{Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(v * 100)}%";
    }

    [GeneratedRegex(@"^\s*#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\s*$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^\s*(?:rgba?\s*\(\s*)?(\d{1,3})\s*[,\s]\s*(\d{1,3})\s*[,\s]\s*(\d{1,3})(?:\s*[,/]\s*([\d.]+%?))?\s*\)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbRegex();

    /// <summary>
    /// Parses "#29B8DB", "29B8DB" (only with 6/8 digits and at least one letter or a leading #), "#FFF", "rgb(41, 184, 219)",
    /// "rgba(…)", "41, 184, 219" and CSS/WPF named colors ("orange"). Returns false for anything else.
    /// </summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;
        var t = text.Trim();

        var m = HexRegex().Match(t);
        if (m.Success && (t.StartsWith('#') || (m.Groups[1].Length >= 6 && t.Any(char.IsLetter))))
        {
            var hex = m.Groups[1].Value;
            if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
            uint v = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            color = hex.Length == 8
                ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        m = RgbRegex().Match(t);
        if (m.Success)
        {
            if (!byte.TryParse(m.Groups[1].Value, out var r) || !byte.TryParse(m.Groups[2].Value, out var g) ||
                !byte.TryParse(m.Groups[3].Value, out var b)) return false;
            byte a = 255;
            if (m.Groups[4].Success)
            {
                var av = m.Groups[4].Value;
                double alpha = av.EndsWith('%')
                    ? double.Parse(av.TrimEnd('%'), CultureInfo.InvariantCulture) / 100
                    : double.Parse(av, CultureInfo.InvariantCulture);
                if (alpha > 1) alpha /= 255;
                a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
            }
            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        if (t.All(char.IsLetter))
        {
            var prop = typeof(Colors).GetProperty(t, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
            if (prop?.GetValue(null) is Color named)
            {
                color = named;
                return true;
            }
        }
        return false;
    }
}
