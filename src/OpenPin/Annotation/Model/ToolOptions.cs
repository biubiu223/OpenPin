using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using OpenPin.Core;
using OpenPin.Core.Imaging;

namespace OpenPin.Annotation.Model;

/// <summary>
/// Style options of one tool (union of all tools' fields; each tool uses a subset). Persisted per tool as JSON in
/// <see cref="AnnotationSettings.ToolState"/>.
/// </summary>
internal sealed class ToolOptions
{
    public string Color { get; set; } = "#FFF5222D";
    /// <summary>Line width / brush width / font size / marker diameter / border width depending on the tool (image px).</summary>
    public double Size { get; set; } = 4;

    // Shapes
    public bool Fill { get; set; }
    public LineDash Dash { get; set; }
    public double CornerRadius { get; set; }
    public EllipseVariant EllipseVariant { get; set; }

    // Lines / arrows
    public ArrowStyle ArrowStyle { get; set; }
    public LineEnd StartEnd { get; set; }
    public LineEnd EndEnd { get; set; }
    public LineJoinStyle Join { get; set; }
    public LineCapStyle Cap { get; set; }

    // Serial
    public SerialType SerialType { get; set; }

    // Text / watermark
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Outline { get; set; }
    public string OutlineColor { get; set; } = "#FFFFFFFF";
    public bool Background { get; set; }
    public string BackgroundColor { get; set; } = "#FFFFFFFF";
    public double BackgroundOpacity { get; set; } = 0.85;
    public double BackgroundRadius { get; set; } = 4;
    public double BackgroundPadding { get; set; } = 6;

    // Highlighter / mosaic / eraser / spotlight / magnifier
    public DrawMode Mode { get; set; }
    public HighlightBlend Blend { get; set; }
    public MosaicEffect Effect { get; set; }
    /// <summary>Mosaic/blur strength 1..10.</summary>
    public int Strength { get; set; } = 5;
    public ShapeKind Shape { get; set; }
    /// <summary>Spotlight dimming / watermark opacity (0..1).</summary>
    public double Opacity { get; set; } = 0.6;
    public int Zoom { get; set; } = 2;
    public ConnectorStyle Connector { get; set; } = ConnectorStyle.Solid;

    // Watermark
    public string WatermarkText { get; set; } = "OpenPin $yyyy-MM-dd HH:mm$";
    public WatermarkLayout Layout { get; set; }
    public double Spacing { get; set; } = 80;

    [JsonIgnore]
    public Color ColorValue
    {
        get => Parse(Color, Colors.Red);
        set => Color = ToText(value);
    }

    public static Color Parse(string? text, Color fallback) => ColorText.TryParse(text, out var c) ? c : fallback;
    public static string ToText(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public ToolOptions Clone() => (ToolOptions)MemberwiseClone();

    public static ToolOptions CreateDefault(AnnotationTool tool)
    {
        var o = new ToolOptions();
        switch (tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                o.Size = 4; break;
            case AnnotationTool.Line:
                o.Size = 4; break;
            case AnnotationTool.Arrow:
                o.Size = 6; break;
            case AnnotationTool.Serial:
                o.Size = 28; break;
            case AnnotationTool.Pencil:
                o.Size = 4; break;
            case AnnotationTool.Highlighter:
                o.Size = 20; o.Color = "#FFFADB14"; break;
            case AnnotationTool.Mosaic:
                o.Size = 30; o.Mode = DrawMode.Rect; break;
            case AnnotationTool.Text:
                o.Size = 20; break;
            case AnnotationTool.Eraser:
                o.Size = 20; break;
            case AnnotationTool.Spotlight:
                o.Opacity = 0.6; break;
            case AnnotationTool.Watermark:
                o.Size = 18; o.Color = "#FF000000"; o.Opacity = 0.18; break;
            case AnnotationTool.Magnifier:
                o.Size = 3; o.Zoom = 2; o.Shape = ShapeKind.Rectangle; break;
        }
        return o;
    }

    /// <summary>Size limits per tool (min, max, step).</summary>
    public static (double Min, double Max, double Step) SizeRange(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Serial => (12, 120, 2),
        AnnotationTool.Text => (8, 200, 2),
        AnnotationTool.Watermark => (8, 120, 2),
        AnnotationTool.Highlighter => (4, 120, 2),
        AnnotationTool.Mosaic => (6, 200, 4),
        AnnotationTool.Eraser => (4, 200, 4),
        AnnotationTool.Magnifier => (0, 20, 1),
        _ => (1, 60, 1),
    };
}

/// <summary>Per-tool options with persistence and the "shared color" rule (ANN-14).</summary>
internal sealed class ToolOptionsStore
{
    private static readonly AnnotationSettings Fallback = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<AnnotationTool, ToolOptions> _map = new();

    /// <summary>Raised after any option changed (argument: the tool whose options changed).</summary>
    public event Action<AnnotationTool>? Changed;

    public static AnnotationSettings Settings
    {
        get
        {
            try
            {
                // AppServices.Settings is null in unit tests / tools that don't run App.OnStartup.
                var svc = AppServices.Settings;
                return svc?.Current?.Annotation ?? Fallback;
            }
            catch
            {
                return Fallback;
            }
        }
    }

    /// <summary>Tools that share one color when <see cref="AnnotationSettings.SharedColor"/> is on (highlighter excluded).</summary>
    public static bool UsesSharedColor(AnnotationTool t) => t is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Line
        or AnnotationTool.Arrow or AnnotationTool.Serial or AnnotationTool.Pencil or AnnotationTool.Text or AnnotationTool.Magnifier;

    private static AnnotationTool Key(AnnotationTool t) => t == AnnotationTool.Ellipse ? AnnotationTool.Rectangle : t;

    public ToolOptions Get(AnnotationTool tool)
    {
        tool = Key(tool);
        if (_map.TryGetValue(tool, out var o)) return o;
        o = Load(tool);
        _map[tool] = o;
        return o;
    }

    private static ToolOptions Load(AnnotationTool tool)
    {
        try
        {
            if (Settings.ToolState.TryGetValue(tool.ToString(), out var json) && !string.IsNullOrWhiteSpace(json))
            {
                var o = JsonSerializer.Deserialize<ToolOptions>(json, Json);
                if (o != null) return o;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Bad annotation tool state for {tool}: {ex.Message}");
        }
        return ToolOptions.CreateDefault(tool);
    }

    /// <summary>Persist and notify after the caller modified <see cref="Get"/>'s object.</summary>
    public void Commit(AnnotationTool tool)
    {
        tool = Key(tool);
        var o = Get(tool);
        if (UsesSharedColor(tool) && Settings.SharedColor)
        {
            foreach (var t in Enum.GetValues<AnnotationTool>())
            {
                if (t == tool || t == AnnotationTool.Ellipse || !UsesSharedColor(t)) continue;
                var other = Get(t);
                if (other.Color != o.Color) { other.Color = o.Color; Persist(t, other); }
            }
        }
        Persist(tool, o);
        Changed?.Invoke(tool);
    }

    private static void Persist(AnnotationTool tool, ToolOptions o)
    {
        try { Settings.ToolState[tool.ToString()] = JsonSerializer.Serialize(o, Json); }
        catch (Exception ex) { Log.Error("Failed to store annotation tool state", ex); }
    }

    /// <summary>Last used geometry tool (rectangle or ellipse) for the shared toolbar button.</summary>
    public AnnotationTool LastGeometryTool
    {
        get => Settings.ToolState.TryGetValue("GeometryTool", out var v) && v == nameof(AnnotationTool.Ellipse)
            ? AnnotationTool.Ellipse : AnnotationTool.Rectangle;
        set => Settings.ToolState["GeometryTool"] = value.ToString();
    }
}
