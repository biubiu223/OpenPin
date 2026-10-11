using System.Windows.Input;
using OpenPin.Core.Hotkeys;

namespace OpenPin.SettingsUI.Logic;

internal enum HotkeyIssueKind { None, Invalid, Duplicate, InUse, RegisterFailed }

/// <summary>A problem with one action's hotkey. <see cref="OtherActions"/> = action ids sharing the same hotkey.</summary>
internal sealed record HotkeyIssue(HotkeyIssueKind Kind, IReadOnlyList<string> OtherActions)
{
    public static readonly HotkeyIssue None = new(HotkeyIssueKind.None, Array.Empty<string>());
}

/// <summary>Hotkey conflict detection for the hotkeys page (SET-02).</summary>
internal static class HotkeyConflicts
{
    /// <summary>
    /// Analyzes all bindings. Precedence: invalid text → duplicate inside OpenPin → used by another program
    /// (<paramref name="isAvailable"/> = <c>HotkeyService.IsAvailable</c>) → failed at the last apply.
    /// Unbound (empty) actions have no issue.
    /// </summary>
    public static Dictionary<string, HotkeyIssue> Analyze(
        IReadOnlyDictionary<string, string> bindings,
        Func<Hotkey, bool>? isAvailable = null,
        IReadOnlyCollection<string>? failedAtApply = null)
    {
        var result = new Dictionary<string, HotkeyIssue>();
        var parsed = new Dictionary<string, Hotkey>();
        foreach (var (action, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) { result[action] = HotkeyIssue.None; continue; }
            if (Hotkey.TryParse(text, out var hk)) parsed[action] = hk;
            else result[action] = new HotkeyIssue(HotkeyIssueKind.Invalid, Array.Empty<string>());
        }

        foreach (var group in parsed.GroupBy(p => p.Value))
        {
            var ids = group.Select(p => p.Key).OrderBy(id => id, StringComparer.Ordinal).ToList();
            foreach (var id in ids)
            {
                if (ids.Count > 1)
                    result[id] = new HotkeyIssue(HotkeyIssueKind.Duplicate, ids.Where(o => o != id).ToList());
                else if (isAvailable != null && !SafeAvailable(isAvailable, group.Key))
                    result[id] = new HotkeyIssue(HotkeyIssueKind.InUse, Array.Empty<string>());
                else if (failedAtApply != null && failedAtApply.Contains(id))
                    result[id] = new HotkeyIssue(HotkeyIssueKind.RegisterFailed, Array.Empty<string>());
                else
                    result[id] = HotkeyIssue.None;
            }
        }
        return result;
    }

    private static bool SafeAvailable(Func<Hotkey, bool> isAvailable, Hotkey hk)
    {
        try { return isAvailable(hk); }
        catch { return true; } // probing must never break the page
    }

    /// <summary>Number of actions with a problem.</summary>
    public static int CountProblems(IReadOnlyDictionary<string, HotkeyIssue> issues) =>
        issues.Values.Count(i => i.Kind != HotkeyIssueKind.None);
}

internal enum RecorderOutcome { Pending, Cancel, Clear, Set, Rejected }

/// <summary>Result of one key press inside the hotkey recorder.</summary>
internal readonly record struct RecorderResult(RecorderOutcome Outcome, Hotkey Hotkey, string Preview);

/// <summary>Pure key interpretation of the hotkey recorder control (testable without WPF input).</summary>
internal static class HotkeyRecorderLogic
{
    private static readonly HashSet<Key> ModifierOnly = new()
    {
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin,
        Key.System, Key.ImeProcessed, Key.DeadCharProcessed, Key.None,
    };

    /// <summary>
    /// Interprets a key press. Esc / Backspace / Delete without modifiers cancel / clear. Pure modifier presses only
    /// update the preview ("Ctrl+Shift+…"). Keys that would be unusable as a global hotkey without a modifier
    /// (letters, digits, Space…) are rejected.
    /// </summary>
    public static RecorderResult Interpret(Key key, ModifierKeys modifiers)
    {
        if (ModifierOnly.Contains(key))
            return new RecorderResult(RecorderOutcome.Pending, Hotkey.None, ModifierPreview(modifiers));

        if (modifiers == ModifierKeys.None)
        {
            if (key == Key.Escape) return new RecorderResult(RecorderOutcome.Cancel, Hotkey.None, "");
            if (key is Key.Back or Key.Delete) return new RecorderResult(RecorderOutcome.Clear, Hotkey.None, "");
        }

        var hk = new Hotkey(modifiers, key);
        if (!IsAcceptableGlobal(hk))
            return new RecorderResult(RecorderOutcome.Rejected, hk, hk.ToString());
        return new RecorderResult(RecorderOutcome.Set, hk, hk.ToString());
    }

    /// <summary>Global hotkeys need a modifier, except function keys, PrintScreen, Pause, Scroll Lock and similar.</summary>
    public static bool IsAcceptableGlobal(Hotkey hk)
    {
        if (hk.IsEmpty || ModifierOnly.Contains(hk.Key)) return false;
        if (hk.Modifiers != ModifierKeys.None)
        {
            // Shift alone + a printable key would block typing that character everywhere.
            if (hk.Modifiers == ModifierKeys.Shift && !IsStandaloneKey(hk.Key)) return false;
            return true;
        }
        return IsStandaloneKey(hk.Key);
    }

    private static bool IsStandaloneKey(Key k) =>
        k is >= Key.F1 and <= Key.F24 or Key.Snapshot or Key.Pause or Key.Scroll or Key.Insert;

    public static string ModifierPreview(ModifierKeys m)
    {
        var parts = new List<string>(4);
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }
}
