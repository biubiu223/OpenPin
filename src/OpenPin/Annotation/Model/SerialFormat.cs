using System.Text;

namespace OpenPin.Annotation.Model;

/// <summary>Formats serial marker values: 1,2,3… / A…Z, AA… / I, II, III…</summary>
internal static class SerialFormat
{
    public static string Format(int value, SerialType type) => type switch
    {
        SerialType.Letter => ToLetters(value),
        SerialType.Roman => ToRoman(value),
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>1 → A, 26 → Z, 27 → AA (spreadsheet style). Values below 1 fall back to digits.</summary>
    public static string ToLetters(int value)
    {
        if (value < 1) return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        while (value > 0)
        {
            value--;
            sb.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }
        return sb.ToString();
    }

    private static readonly (int Value, string Text)[] RomanTable =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
        (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    /// <summary>1..3999 → Roman numerals; anything else falls back to digits.</summary>
    public static string ToRoman(int value)
    {
        if (value < 1 || value > 3999) return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var (v, t) in RomanTable)
            while (value >= v) { sb.Append(t); value -= v; }
        return sb.ToString();
    }
}
