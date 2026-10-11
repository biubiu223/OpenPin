using System.Windows.Input;

namespace OpenPin.Core.Hotkeys;

/// <summary>
/// A key + modifiers combination with a stable text form, e.g. "Ctrl+Shift+A", "Alt+1", "Ctrl+`", "F1".
/// Used for global hotkeys (settings.json) and can be reused for in-app shortcuts.
/// </summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static readonly Hotkey None = new(ModifierKeys.None, Key.None);

    public bool IsEmpty => Key == Key.None;

    private static readonly Dictionary<Key, string> KeyNames = new()
    {
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4",
        [Key.D5] = "5", [Key.D6] = "6", [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
        [Key.Oem3] = "`", [Key.OemMinus] = "-", [Key.OemPlus] = "=", [Key.OemComma] = ",",
        [Key.OemPeriod] = ".", [Key.Oem2] = "/", [Key.Oem1] = ";", [Key.Oem7] = "'",
        [Key.Oem4] = "[", [Key.Oem6] = "]", [Key.Oem5] = "\\",
        [Key.Snapshot] = "PrintScreen", [Key.Return] = "Enter", [Key.Escape] = "Esc",
        [Key.Prior] = "PageUp", [Key.Next] = "PageDown", [Key.Back] = "Backspace",
    };

    private static readonly Dictionary<string, Key> NameToKey = BuildReverse();

    private static Dictionary<string, Key> BuildReverse()
    {
        var d = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in KeyNames) d[v] = k;
        d["~"] = Key.Oem3;
        d["Tilde"] = Key.Oem3;
        d["PrtSc"] = Key.Snapshot;
        d["Escape"] = Key.Escape;
        d["Return"] = Key.Return;
        return d;
    }

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyNames.TryGetValue(Key, out var n) ? n : Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = ModifierKeys.None;
        Key key = Key.None;
        // Split on '+' but allow the key itself to be "+" ("Ctrl++") - rare, treat as OemPlus.
        var tokens = text.Replace("++", "+=").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in tokens)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; continue;
                case "alt": mods |= ModifierKeys.Alt; continue;
                case "shift": mods |= ModifierKeys.Shift; continue;
                case "win": case "windows": case "meta": mods |= ModifierKeys.Windows; continue;
            }
            if (key != Key.None) return false; // two non-modifier keys
            if (NameToKey.TryGetValue(raw, out var k)) key = k;
            else if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
            else if (Enum.TryParse<Key>(raw, ignoreCase: true, out var parsed)) key = parsed;
            else return false;
        }
        if (key == Key.None) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    /// <summary>True if this hotkey matches a WPF key event (uses SystemKey when Alt is held).</summary>
    public bool Matches(KeyEventArgs e)
    {
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        return !IsEmpty && k == Key && Keyboard.Modifiers == Modifiers;
    }
}
