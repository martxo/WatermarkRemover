namespace WatermarkRemover.Core.Logging;

/// <summary>
/// Log severity, ordered from most to least verbose. The app's file logger compares each entry
/// against its configured minimum level; <see cref="Error"/> is always written.
/// </summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,

    /// <summary>Silences everything except <see cref="Error"/> — used to turn logging down on site.</summary>
    Off = 5,
}
