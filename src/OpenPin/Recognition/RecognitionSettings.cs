namespace OpenPin.Recognition;

/// <summary>settings.json → "Recognition" (table recognition, formula recognition). Owned by the Recognition module.</summary>
public sealed class RecognitionSettings
{
    /// <summary>Default export format of table recognition: "xlsx" | "csv".</summary>
    public string TableExportFormat { get; set; } = "xlsx";

    /// <summary>Detect ruling (grid) lines in the image to find rows / columns / merged cells.</summary>
    public bool DetectTableLines { get; set; } = true;

    /// <summary>Folder last used to export a table (empty = Documents).</summary>
    public string LastExportFolder { get; set; } = "";
}
