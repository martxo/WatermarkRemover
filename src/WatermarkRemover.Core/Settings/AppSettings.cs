namespace WatermarkRemover.Core.Settings;

/// <summary>Where the cleaned file is written.</summary>
public enum OutputMode
{
    /// <summary>Write next to the input file.</summary>
    SameDirectory = 0,

    /// <summary>Write into <see cref="AppSettings.OutputDirectory"/>.</summary>
    CustomDirectory = 1,
}

/// <summary>User preferences, persisted as JSON alongside the application.</summary>
public sealed class AppSettings
{
    /// <summary>Suffix appended to the input file name to form the output name.</summary>
    public string OutputSuffix { get; set; } = "_no-watermark";

    public OutputMode OutputMode { get; set; } = OutputMode.SameDirectory;

    /// <summary>Used when <see cref="OutputMode"/> is <see cref="OutputMode.CustomDirectory"/>.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Remove link annotations that belong to the detected watermark.</summary>
    public bool RemoveLinkedAnnotations { get; set; } = true;

    /// <summary>How many files are processed at the same time.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Check the release channel for a newer version when the app starts.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;
}
