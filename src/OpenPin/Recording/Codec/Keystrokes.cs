namespace OpenPin.Recording.Codec;

/// <summary>Which keystrokes the overlay shows.</summary>
public enum KeystrokeFilter { All, CombosOnly }

/// <summary>A label ("Ctrl + C") visible in [Start, End).</summary>
internal readonly record struct KeySegment(double Start, double End, string Label);

/// <summary>UI-free keystroke overlay logic: virtual-key names and the timeline of labels to display.</summary>
internal static class Keystrokes
{
    /// <summary>How long a label stays after its key was released.</summary>
    public const double HoldMs = 1200;

    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    [Flags]
    private enum Mods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

    private static Mods ModOf(int vk) => vk switch
    {
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => Mods.Ctrl,
        VK_MENU or VK_LMENU or VK_RMENU => Mods.Alt,
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT => Mods.Shift,
        VK_LWIN or VK_RWIN => Mods.Win,
        _ => Mods.None,
    };

    public static bool IsModifier(int vk) => ModOf(vk) != Mods.None;

    private static string ModText(Mods m)
    {
        var parts = new List<string>(4);
        if (m.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(Mods.Win)) parts.Add("Win");
        if (m.HasFlag(Mods.Alt)) parts.Add("Alt");
        if (m.HasFlag(Mods.Shift)) parts.Add("Shift");
        return string.Join(" + ", parts);
    }

    /// <summary>Display name of a (non-modifier) virtual key. <paramref name="fallback"/> resolves OEM keys (keyboard layout).</summary>
    public static string KeyName(int vk, Func<int, string?>? fallback = null)
    {
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();         // A–Z
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();         // 0–9
        if (vk is >= 0x60 and <= 0x69) return "Num " + (vk - 0x60);          // numpad digits
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);             // F1–F24
        switch (vk)
        {
            case 0x08: return "Backspace";
            case 0x09: return "Tab";
            case 0x0D: return "Enter";
            case 0x13: return "Pause";
            case 0x14: return "Caps Lock";
            case 0x1B: return "Esc";
            case 0x20: return "Space";
            case 0x21: return "PgUp";
            case 0x22: return "PgDn";
            case 0x23: return "End";
            case 0x24: return "Home";
            case 0x25: return "←";
            case 0x26: return "↑";
            case 0x27: return "→";
            case 0x28: return "↓";
            case 0x2C: return "PrtSc";
            case 0x2D: return "Insert";
            case 0x2E: return "Delete";
            case 0x5D: return "Menu";
            case 0x6A: return "Num *";
            case 0x6B: return "Num +";
            case 0x6D: return "Num -";
            case 0x6E: return "Num .";
            case 0x6F: return "Num /";
            case 0x90: return "Num Lock";
            case 0x91: return "Scroll Lock";
            case 0xBA: return ";";
            case 0xBB: return "=";
            case 0xBC: return ",";
            case 0xBD: return "-";
            case 0xBE: return ".";
            case 0xBF: return "/";
            case 0xC0: return "`";
            case 0xDB: return "[";
            case 0xDC: return "\\";
            case 0xDD: return "]";
            case 0xDE: return "'";
        }
        if (IsModifier(vk)) return ModText(ModOf(vk));
        return fallback?.Invoke(vk) ?? $"0x{vk:X2}";
    }

    /// <summary>
    /// Turn raw key events (sorted by time) into display segments. A non-modifier key press shows "modifiers + key";
    /// with <see cref="KeystrokeFilter.CombosOnly"/> only presses with Ctrl / Alt / Win held are shown (Shift alone does not
    /// count — that is just typing). In <see cref="KeystrokeFilter.All"/> mode a lone modifier shows while held.
    /// A label lasts until <see cref="HoldMs"/> after its key is released or until the next label replaces it.
    /// </summary>
    public static List<KeySegment> BuildTimeline(IReadOnlyList<KeyEvent> events, KeystrokeFilter filter, Func<int, string?>? fallback = null)
    {
        var result = new List<KeySegment>();
        var down = new HashSet<int>();
        Mods mods = Mods.None;
        // current open segment
        string? label = null;
        double start = 0, end = double.PositiveInfinity;
        int ownerVk = -1; // key whose release starts the hold countdown
        bool modifierOnly = false;

        void Close(double t)
        {
            if (label != null && t > start) result.Add(new KeySegment(start, Math.Min(t, end), label));
            label = null;
        }

        foreach (var e in events)
        {
            // an open segment whose hold already expired
            if (label != null && e.TimeMs >= end) Close(end);

            var m = ModOf(e.Vk);
            if (e.Down)
            {
                if (!down.Add(e.Vk)) continue; // auto-repeat
                if (m != Mods.None)
                {
                    mods |= m;
                    if (filter == KeystrokeFilter.All && (label == null || modifierOnly))
                    {
                        Close(e.TimeMs);
                        label = ModText(mods);
                        start = e.TimeMs;
                        end = double.PositiveInfinity;
                        ownerVk = -1;
                        modifierOnly = true;
                    }
                    continue;
                }
                bool combo = (mods & (Mods.Ctrl | Mods.Alt | Mods.Win)) != 0;
                if (filter == KeystrokeFilter.CombosOnly && !combo) continue;
                var name = KeyName(e.Vk, fallback);
                Close(e.TimeMs);
                label = mods == Mods.None ? name : ModText(mods) + " + " + name;
                start = e.TimeMs;
                end = double.PositiveInfinity;
                ownerVk = e.Vk;
                modifierOnly = false;
            }
            else
            {
                down.Remove(e.Vk);
                if (m != Mods.None)
                {
                    mods &= ~m;
                    // keep L/R variants consistent: if the other side is still down, the modifier stays
                    foreach (var vk in down) mods |= ModOf(vk);
                    if (label != null && modifierOnly)
                    {
                        if (mods == Mods.None) end = Math.Min(end, e.TimeMs + HoldMs);
                        else if (filter == KeystrokeFilter.All) { Close(e.TimeMs); label = ModText(mods); start = e.TimeMs; end = double.PositiveInfinity; modifierOnly = true; }
                    }
                    continue;
                }
                if (label != null && e.Vk == ownerVk) end = Math.Min(end, e.TimeMs + HoldMs);
            }
        }
        if (label != null)
        {
            // never-released keys (recording stopped while held): show for the hold time
            if (double.IsPositiveInfinity(end)) end = start + HoldMs;
            result.Add(new KeySegment(start, end, label));
        }
        return result;
    }

    /// <summary>Label visible at <paramref name="timeMs"/>, or null.</summary>
    public static KeySegment? At(IReadOnlyList<KeySegment> segments, double timeMs)
    {
        int lo = 0, hi = segments.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var s = segments[mid];
            if (timeMs < s.Start) hi = mid - 1;
            else if (timeMs >= s.End) lo = mid + 1;
            else return s;
        }
        return null;
    }
}
