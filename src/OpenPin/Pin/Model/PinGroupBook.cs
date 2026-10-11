using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPin.Pin.Model;

/// <summary>A pin group (PIN-11). <see cref="Color"/> is "#RRGGBB" or null (no color).</summary>
public sealed class PinGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
}

/// <summary>
/// Pure bookkeeping of pin groups: ordering, unique names, current group, cycling. Persisted as <c>pins/groups.json</c>.
/// The default group always exists, cannot be deleted and is kept first.
/// </summary>
public sealed class PinGroupBook
{
    public const string DefaultGroupId = "default";
    public const int MaxNameLength = 40;

    public List<PinGroup> Groups { get; set; } = new();
    public string CurrentId { get; set; } = DefaultGroupId;

    [JsonIgnore]
    public PinGroup Current => Find(CurrentId) ?? Groups[0];

    public PinGroup? Find(string? id) => id == null ? null : Groups.FirstOrDefault(g => g.Id == id);

    /// <summary>Repairs loaded data: default group present and first, ids unique, names non-empty, current valid.</summary>
    public void EnsureValid(string defaultName)
    {
        Groups ??= new List<PinGroup>();
        Groups.RemoveAll(g => g == null || string.IsNullOrWhiteSpace(g.Id));
        // de-duplicate ids
        var seen = new HashSet<string>();
        Groups.RemoveAll(g => !seen.Add(g.Id));

        var def = Find(DefaultGroupId);
        if (def == null)
        {
            def = new PinGroup { Id = DefaultGroupId, Name = defaultName };
            Groups.Insert(0, def);
        }
        else if (Groups[0] != def)
        {
            Groups.Remove(def);
            Groups.Insert(0, def);
        }

        foreach (var g in Groups)
        {
            g.Name = (g.Name ?? "").Trim();
            if (g.Name.Length == 0) g.Name = g.Id == DefaultGroupId ? defaultName : UniqueName(defaultName, g.Id);
        }
        if (Find(CurrentId) == null) CurrentId = DefaultGroupId;
    }

    public static string NormalizeName(string? name)
    {
        var n = (name ?? "").Trim();
        return n.Length > MaxNameLength ? n[..MaxNameLength] : n;
    }

    public bool IsNameAvailable(string? name, string? exceptId = null)
    {
        var n = NormalizeName(name);
        return n.Length > 0 && !Groups.Any(g => g.Id != exceptId && string.Equals(g.Name, n, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>"base", "base 2", "base 3"… — first name not used yet.</summary>
    public string UniqueName(string baseName, string? exceptId = null)
    {
        var b = NormalizeName(baseName);
        if (b.Length == 0) b = "Group";
        if (IsNameAvailable(b, exceptId)) return b;
        for (int i = 2; ; i++)
        {
            var candidate = $"{b} {i}";
            if (IsNameAvailable(candidate, exceptId)) return candidate;
        }
    }

    /// <summary>Creates a group at the end. Returns null if the name is empty or already used.</summary>
    public PinGroup? Create(string? name, string? color)
    {
        var n = NormalizeName(name);
        if (!IsNameAvailable(n)) return null;
        var g = new PinGroup { Id = "g" + Guid.NewGuid().ToString("N")[..10], Name = n, Color = color };
        Groups.Add(g);
        return g;
    }

    public bool Rename(string id, string? name)
    {
        var g = Find(id);
        var n = NormalizeName(name);
        if (g == null || !IsNameAvailable(n, id)) return false;
        g.Name = n;
        return true;
    }

    public bool SetColor(string id, string? color)
    {
        var g = Find(id);
        if (g == null) return false;
        g.Color = color;
        return true;
    }

    /// <summary>Removes a group (never the default one). If it was current, the default group becomes current.</summary>
    public bool Delete(string id)
    {
        if (id == DefaultGroupId) return false;
        var g = Find(id);
        if (g == null) return false;
        Groups.Remove(g);
        if (CurrentId == id) CurrentId = DefaultGroupId;
        return true;
    }

    /// <summary>Moves a group up (delta &lt; 0) or down. The default group stays first.</summary>
    public bool Move(string id, int delta)
    {
        int i = Groups.FindIndex(g => g.Id == id);
        if (i <= 0) return false; // not found, or default group
        int j = Math.Clamp(i + delta, 1, Groups.Count - 1);
        if (j == i) return false;
        var g = Groups[i];
        Groups.RemoveAt(i);
        Groups.Insert(j, g);
        return true;
    }

    /// <summary>Id of the group after the current one (wraps around).</summary>
    public string NextId()
    {
        if (Groups.Count == 0) return DefaultGroupId;
        int i = Groups.FindIndex(g => g.Id == CurrentId);
        return Groups[(i + 1 + Groups.Count) % Groups.Count].Id;
    }

    // ---------------------------------------------------------------- JSON

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PinGroupBook FromJsonOrNew(string? json, string defaultName)
    {
        PinGroupBook? book = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { book = JsonSerializer.Deserialize<PinGroupBook>(json, Json); }
            catch (JsonException) { book = null; }
        }
        book ??= new PinGroupBook();
        book.EnsureValid(defaultName);
        return book;
    }

    public PinGroupBook Clone() => FromJsonOrNew(ToJson(), Groups.Count > 0 ? Groups[0].Name : "Default");
}
