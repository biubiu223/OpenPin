using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPin.Pin.Logic;

namespace OpenPin.Pin.Model;

public enum PinKind { Image, Text, Files, Color }

/// <summary>
/// Non-destructive image adjustments of an image pin (PIN-10). The displayed image is
/// <c>Mirror(Rotate(edited, Rotation))</c> followed by the per-pixel filters.
/// Rotation/flip are kept normalized as an element of the dihedral group (rotation first, then optional horizontal mirror).
/// </summary>
public sealed class ImageAdjustments
{
    /// <summary>Clockwise rotation in degrees: 0, 90, 180 or 270.</summary>
    public int Rotation { get; set; }
    /// <summary>Horizontal mirror applied after the rotation.</summary>
    public bool Mirror { get; set; }
    public bool Grayscale { get; set; }
    public bool Invert { get; set; }
    /// <summary>Brightness offset in percent, -100…100.</summary>
    public int Brightness { get; set; }

    [JsonIgnore]
    public bool IsIdentity => Rotation == 0 && !Mirror && !Grayscale && !Invert && Brightness == 0;

    [JsonIgnore]
    public bool HasPixelFilters => Grayscale || Invert || Brightness != 0;

    /// <summary>True when width and height are swapped relative to the source.</summary>
    [JsonIgnore]
    public bool SwapsAxes => Rotation is 90 or 270;

    public void RotateRight() => Rotation = Norm(Rotation + (Mirror ? -90 : 90));
    public void RotateLeft() => Rotation = Norm(Rotation + (Mirror ? 90 : -90));
    public void FlipHorizontal() => Mirror = !Mirror;

    /// <summary>Vertical flip == horizontal flip ∘ 180° rotation (both commute).</summary>
    public void FlipVertical()
    {
        Mirror = !Mirror;
        Rotation = Norm(Rotation + 180);
    }

    public void ChangeBrightness(int delta) => Brightness = Math.Clamp(Brightness + delta, -100, 100);

    public void Reset()
    {
        Rotation = 0; Mirror = false; Grayscale = false; Invert = false; Brightness = 0;
    }

    public ImageAdjustments Clone() => (ImageAdjustments)MemberwiseClone();

    private static int Norm(int deg) => ((deg % 360) + 360) % 360;
}

/// <summary>
/// Persisted state of one pin: <c>pins/&lt;Id&gt;/manifest.json</c> (+ <c>original.png</c>, <c>edited.png</c> for image pins).
/// All positions are PHYSICAL pixels (virtual-screen coordinates).
/// </summary>
public sealed class PinManifest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = "";
    public PinKind Kind { get; set; }
    public string GroupId { get; set; } = PinGroupBook.DefaultGroupId;

    /// <summary>Screen position (physical px) of the content's top-left corner (content unit 0,0).</summary>
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    /// <summary>User zoom, 1 = 100%.</summary>
    public double Zoom { get; set; } = 1;
    /// <summary>
    /// Physical pixels per content unit at 100% zoom. 1 for images (1 image px = 1 physical px);
    /// for text / files / color pins (laid out in DIPs) the monitor scale at creation, kept constant afterwards
    /// so the physical size never changes when the pin moves between monitors.
    /// </summary>
    public double BaseScale { get; set; } = 1;
    /// <summary>0.1 … 1.</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>Opacity to go back to when click-through is turned off (0 = unchanged).</summary>
    public double OpacityBeforeClickThrough { get; set; }

    public ImageAdjustments Adjust { get; set; } = new();
    /// <summary>True when <c>edited.png</c> (annotations / crop baked in) exists and differs from the original.</summary>
    public bool HasEditedImage { get; set; }

    /// <summary>Thumbnail mode viewport in content units (x, y, w, h); null = whole content visible.</summary>
    public double[]? Viewport { get; set; }

    public string? Title { get; set; }
    public bool Locked { get; set; }
    public bool Topmost { get; set; } = true;
    public bool Shadow { get; set; } = true;
    public bool ClickThrough { get; set; }

    public bool IsOpen { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ClosedAt { get; set; }

    // ---- content (non-image kinds keep their content in the manifest)
    public string? Text { get; set; }
    public string? Html { get; set; }
    public bool IgnoreFormat { get; set; }
    public List<string>? Files { get; set; }
    /// <summary>#AARRGGBB for color pins.</summary>
    public string? Color { get; set; }
    public string? ColorOriginalText { get; set; }
    public string? SourcePath { get; set; }
    /// <summary>Hash of the pinned content (duplicate detection).</summary>
    public string? ContentHash { get; set; }

    [JsonIgnore]
    public bool IsThumbnail => Viewport is { Length: 4 };

    public PinManifest Clone()
    {
        var c = (PinManifest)MemberwiseClone();
        c.Adjust = Adjust.Clone();
        c.Viewport = Viewport?.ToArray();
        c.Files = Files?.ToList();
        return c;
    }

    // ---------------------------------------------------------------- JSON

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses and validates a manifest; returns null (never throws) for corrupt/unknown data.</summary>
    public static PinManifest? FromJson(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize<PinManifest>(json, Json);
            if (m == null || string.IsNullOrWhiteSpace(m.Id)) return null;
            m.Sanitize();
            return m;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Clamp values that could make a window unusable (hand-edited or old files).</summary>
    public void Sanitize()
    {
        Adjust ??= new ImageAdjustments();
        if (!double.IsFinite(Zoom) || Zoom <= 0) Zoom = 1;
        Zoom = Math.Clamp(Zoom, PinGeometry.MinZoom, PinGeometry.MaxZoom);
        if (!double.IsFinite(BaseScale) || BaseScale <= 0) BaseScale = 1;
        BaseScale = Math.Clamp(BaseScale, 0.5, 5);
        if (!double.IsFinite(Opacity)) Opacity = 1;
        Opacity = Math.Clamp(Opacity, 0.1, 1);
        if (Viewport != null && (Viewport.Length != 4 || Viewport.Any(v => !double.IsFinite(v)) || Viewport[2] <= 0 || Viewport[3] <= 0))
            Viewport = null;
        if (string.IsNullOrWhiteSpace(GroupId)) GroupId = PinGroupBook.DefaultGroupId;
        Adjust.Rotation = ((Adjust.Rotation % 360) + 360) % 360 / 90 * 90;
        Adjust.Brightness = Math.Clamp(Adjust.Brightness, -100, 100);
    }

    public static string NewId() => $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
}
