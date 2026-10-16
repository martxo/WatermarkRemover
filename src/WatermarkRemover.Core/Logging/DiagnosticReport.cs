using System.Text;

namespace WatermarkRemover.Core.Logging;

/// <summary>
/// Pure text building blocks shared by every diagnostic report the app can produce (the
/// copyable per-job error report and the exported log report). Formatting lives here so both
/// stay identical; the caller supplies the values and appends report-specific sections.
/// </summary>
public static class DiagnosticReport
{
    /// <summary>Section title of the OCR engine-chain availability dump.</summary>
    public const string EngineSectionTitle = "---- OCR 引擎 ----";

    /// <summary>
    /// Environment header: app version, OS and process bitness — the three lines every bug
    /// report starts with so a report can be triaged without round-trips.
    /// </summary>
    public static string EnvironmentHeader(
        string appVersion, string osVersion, bool is64BitProcess, bool is64BitOperatingSystem)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"应用版本：v{appVersion}");
        sb.AppendLine($"系统：{osVersion}");
        sb.AppendLine($"进程：{(is64BitProcess ? "64" : "32")} 位（{(is64BitOperatingSystem ? "64" : "32")} 位系统）");
        return sb.ToString();
    }
}
