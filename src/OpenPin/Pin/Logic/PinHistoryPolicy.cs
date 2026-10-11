namespace OpenPin.Pin.Logic;

/// <summary>Pin history rules (PIN-03 / PIN-06).</summary>
public static class PinHistoryPolicy
{
    /// <summary>
    /// Given the closed pins, returns the ids that exceed <paramref name="maxCount"/> (oldest first to go).
    /// Pins without a close time count as oldest.
    /// </summary>
    public static IReadOnlyList<string> SelectEvictions(IEnumerable<(string Id, DateTime? ClosedAt)> closed, int maxCount)
    {
        if (maxCount < 0) maxCount = 0;
        return closed
            .OrderByDescending(c => c.ClosedAt ?? DateTime.MinValue)
            .Skip(maxCount)
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>Most recently closed pin of a group, or null.</summary>
    public static string? MostRecent(IEnumerable<(string Id, string GroupId, DateTime? ClosedAt)> closed, string groupId) =>
        closed.Where(c => c.GroupId == groupId)
              .OrderByDescending(c => c.ClosedAt ?? DateTime.MinValue)
              .Select(c => c.Id)
              .FirstOrDefault();
}
