using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPin.Core.Settings;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON. Access via <c>AppServices.Settings.Current</c>.</summary>
public sealed class SettingsService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised (on the caller's thread) after <see cref="Save"/> or <see cref="Replace"/>.</summary>
    public event EventHandler? Changed;

    public SettingsService(string file) => _file = file;

    public void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file), JsonOptions);
                if (loaded != null)
                {
                    // Make sure newly introduced default hotkeys exist for upgraded configs.
                    foreach (var (k, v) in HotkeySettings.DefaultBindings())
                        loaded.Hotkeys.Bindings.TryAdd(k, v);
                    Current = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings, using defaults", ex);
            try { File.Copy(_file, _file + ".broken", overwrite: true); } catch { /* ignore */ }
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replace the whole settings object (e.g. from the settings window "Apply"/"Reset").</summary>
    public void Replace(AppSettings settings)
    {
        Current = settings;
        Save();
    }

    /// <summary>Deep copy through JSON (used by the settings window to edit a draft).</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, JsonOptions), JsonOptions)!;
}
