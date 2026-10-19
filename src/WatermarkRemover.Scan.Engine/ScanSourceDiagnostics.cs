using System.Text;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// Ad-hoc diagnostics for the scan layer, used to explain "no device found" situations in the UI and
/// the run log. Output is Chinese because it is shown to the user; it never throws.
/// </summary>
public static class ScanSourceDiagnostics
{
    /// <summary>Enumerates every scan source and reports what it can see.</summary>
    public static string Probe(Action<string, Exception?>? trace = null)
    {
        var report = new StringBuilder();
        report.AppendLine("=== 扫描来源诊断 ===");

        var wia = new WiaScanSource(trace);
        report.Append(wia.DescribeCapabilities());

        var simulated = new SimulatedScanSource(trace: trace);
        report.AppendLine($"扫描来源：{simulated.Id}（{simulated.DisplayName}）");
        report.AppendLine($"可用：{(simulated.IsAvailableAsync().GetAwaiter().GetResult() ? "是" : "否")}");
        foreach (var device in simulated.DiscoverAsync().GetAwaiter().GetResult())
        {
            report.AppendLine($"- {device.Name}｜DeviceID：{device.DeviceId}");
        }

        report.AppendLine("=== 诊断结束 ===");
        return report.ToString();
    }
}
