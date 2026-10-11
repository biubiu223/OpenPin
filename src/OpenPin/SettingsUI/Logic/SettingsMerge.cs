using System.Text.Json;
using System.Text.Json.Nodes;
using OpenPin.Core.Settings;

namespace OpenPin.SettingsUI.Logic;

/// <summary>
/// Draft diffing / merging. The settings window edits a draft copy; other modules keep writing runtime state
/// (last folder, tool options, presets…) into <c>AppServices.Config</c> meanwhile. On apply only the values the user
/// actually changed (baseline → draft) are written onto the CURRENT settings, so that runtime state is not lost.
/// Objects (incl. dictionaries) are merged per key; arrays and scalars are replaced as a whole.
/// </summary>
internal static class SettingsMerge
{
    public static JsonNode ToNode(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings, SettingsService.JsonOptions) ?? new JsonObject();

    public static AppSettings FromNode(JsonNode node) =>
        node.Deserialize<AppSettings>(SettingsService.JsonOptions) ?? new AppSettings();

    /// <summary>Paths ("Capture.MagnifierZoom", "Hotkeys.Bindings.capture") whose values differ between a and b.</summary>
    public static IReadOnlyList<string> Diff(JsonNode? a, JsonNode? b)
    {
        var result = new List<string>();
        DiffInto(a, b, "", result);
        return result;
    }

    private static void DiffInto(JsonNode? a, JsonNode? b, string path, List<string> result)
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in Keys(oa, ob))
                DiffInto(oa[key], ob[key], path.Length == 0 ? key : path + "." + key, result);
            return;
        }
        if (!JsonNode.DeepEquals(a, b)) result.Add(path);
    }

    /// <summary>Returns a new node: <paramref name="current"/> with every baseline→draft change applied.</summary>
    public static JsonNode? Merge(JsonNode? baseline, JsonNode? draft, JsonNode? current)
    {
        if (JsonNode.DeepEquals(baseline, draft)) return current?.DeepClone();
        if (baseline is JsonObject ob && draft is JsonObject od && current is JsonObject oc)
        {
            var result = (JsonObject)oc.DeepClone();
            foreach (var key in Keys(ob, od))
            {
                bool inBase = ob.ContainsKey(key), inDraft = od.ContainsKey(key);
                if (inBase && !inDraft) { result.Remove(key); continue; }
                if (!inBase) { result[key] = od[key]?.DeepClone(); continue; }
                if (JsonNode.DeepEquals(ob[key], od[key])) continue;
                result[key] = Merge(ob[key], od[key], oc.ContainsKey(key) ? oc[key] : ob[key]?.DeepClone());
            }
            return result;
        }
        return draft?.DeepClone();
    }

    /// <summary>Typed convenience over <see cref="Merge(JsonNode?, JsonNode?, JsonNode?)"/>.</summary>
    public static AppSettings Merge(JsonNode baseline, AppSettings draft, AppSettings current)
    {
        var merged = Merge(baseline, ToNode(draft), ToNode(current));
        return merged is null ? new AppSettings() : FromNode(merged);
    }

    private static IEnumerable<string> Keys(JsonObject a, JsonObject b)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (k, _) in a) if (seen.Add(k)) yield return k;
        foreach (var (k, _) in b) if (seen.Add(k)) yield return k;
    }
}
