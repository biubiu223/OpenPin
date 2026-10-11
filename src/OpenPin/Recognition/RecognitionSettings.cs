namespace OpenPin.Recognition;

/// <summary>settings.json → "Recognition" (table recognition, formula recognition). Owned by the Recognition module.</summary>
public sealed class RecognitionSettings
{
    /// <summary>Default export format of table recognition: "xlsx" | "csv".</summary>
    public string TableExportFormat { get; set; } = "xlsx";
}
