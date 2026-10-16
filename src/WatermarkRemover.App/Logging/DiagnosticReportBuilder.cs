using System.Text;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.App.Logging;

/// <summary>
/// App-side composition of the shared diagnostic header: reads the live environment facts and
/// the OCR engine chain, delegates the text layout to <see cref="DiagnosticReport"/> (Core).
/// Every report builder calls this instead of re-assembling the same lines.
/// </summary>
public static class DiagnosticReportBuilder
{
    /// <summary>Version / OS / bitness header, identical in every report.</summary>
    public static void AppendEnvironmentHeader(StringBuilder report) =>
        report.Append(DiagnosticReport.EnvironmentHeader(
            UpdateService.CurrentVersion.ToString(3),
            Environment.OSVersion.VersionString,
            Environment.Is64BitProcess,
            Environment.Is64BitOperatingSystem));

    /// <summary>OCR engine-chain availability dump, under the shared section title.</summary>
    public static void AppendEngineSection(StringBuilder report)
    {
        report.AppendLine(DiagnosticReport.EngineSectionTitle);
        report.AppendLine(OcrDiagnostics.Current);
    }
}
