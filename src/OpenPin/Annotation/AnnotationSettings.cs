namespace OpenPin.Annotation;

/// <summary>settings.json → "Annotation". Owned by the Annotation module.</summary>
public sealed class AnnotationSettings
{
    /// <summary>All tools except the highlighter share one color.</summary>
    public bool SharedColor { get; set; } = true;
    /// <summary>Palette (#AARRGGBB or #RRGGBB).</summary>
    public List<string> Palette { get; set; } = new()
    {
        "#FFF5222D", "#FFFA8C16", "#FFFADB14", "#FF52C41A", "#FF1677FF", "#FF722ED1",
        "#FF000000", "#FF8C8C8C", "#FFFFFFFF",
    };
    /// <summary>Allow selecting and editing finished annotations.</summary>
    public bool AllowSecondaryEdit { get; set; } = true;
    public bool RememberLastToolInCapture { get; set; }
    public bool RememberLastToolInPin { get; set; } = true;
    /// <summary>After deleting a serial marker, renumber the remaining ones consecutively (ANN-05).</summary>
    public bool SerialRenumberOnDelete { get; set; } = true;
    /// <summary>Last used tool options (color, sizes, styles…) per tool, serialized by the module.</summary>
    public Dictionary<string, string> ToolState { get; set; } = new();
}
