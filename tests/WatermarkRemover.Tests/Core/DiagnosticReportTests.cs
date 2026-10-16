using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.Tests.Core;

public class DiagnosticReportTests
{
    [Fact]
    public void EnvironmentHeaderContainsVersionOsAndBitness()
    {
        string header = DiagnosticReport.EnvironmentHeader("1.9.0", "Microsoft Windows NT 10.0", true, true);

        Assert.Contains("应用版本：v1.9.0", header);
        Assert.Contains("系统：Microsoft Windows NT 10.0", header);
        Assert.Contains("进程：64 位（64 位系统）", header);
    }

    [Fact]
    public void EnvironmentHeaderMarks32BitProcess()
    {
        string header = DiagnosticReport.EnvironmentHeader("1.0.0", "os", false, true);

        Assert.Contains("进程：32 位（64 位系统）", header);
    }

    [Fact]
    public void EngineSectionTitleIsStable()
    {
        Assert.Equal("---- OCR 引擎 ----", DiagnosticReport.EngineSectionTitle);
    }
}
