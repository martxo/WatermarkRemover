namespace WatermarkRemover.Core.Logging;

/// <summary>
/// Stable category tags that make a flat text log filterable by subsystem. Kept as constants so
/// call sites cannot drift apart (a typo would silently split a subsystem's history in two).
/// </summary>
public static class LogCategory
{
    public const string Scan = "SCAN";
    public const string Naming = "NAMING";
    public const string Job = "JOB";
    public const string Preview = "PREVIEW";
    public const string Ocr = "OCR";
    public const string Ui = "UI";
    public const string Settings = "SETTINGS";
    public const string Transfer = "TRANSFER";
    public const string Netdisk = "NETDISK";
    public const string Update = "UPDATE";
    public const string File = "FILE";
}
