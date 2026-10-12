namespace OpenPin.Core.Settings;

/// <summary>
/// Upgrades settings loaded from older settings.json files. Each step upgrades from version N-1 to N;
/// to add one, append it to <see cref="Steps"/> (its version becomes the new <see cref="CurrentVersion"/>).
/// </summary>
public static class SettingsMigrations
{
    private static readonly (int Version, Action<AppSettings> Upgrade)[] Steps =
    {
        // v2: the default theme became "follow system". Files written before stored the old default "Light"
        // explicitly, so treat Light as "never chosen" and switch it to System.
        (2, s =>
        {
            if (s.General.Theme == ThemeMode.Light) s.General.Theme = ThemeMode.System;
        }),
    };

    public static int CurrentVersion => Steps[^1].Version;

    /// <summary>Applies all steps newer than <c>settings.Version</c>. Returns true if anything ran.</summary>
    public static bool Migrate(AppSettings settings)
    {
        bool changed = false;
        foreach (var (version, upgrade) in Steps)
        {
            if (settings.Version >= version) continue;
            upgrade(settings);
            settings.Version = version;
            changed = true;
        }
        return changed;
    }
}
