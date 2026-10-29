using WatermarkRemover.Pdf;

namespace WatermarkRemover.Tests.Pdf;

/// <summary>
/// A "file is in use" message is only actionable when it names whom to close; these tests keep
/// the Restart Manager lookup honest (it also runs inside the app's save failures).
/// </summary>
public class FileLockInspectorTests
{
    private static string NewFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "wm-lock-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(path, "lock probe");
        return path;
    }

    [Fact]
    public void A_locked_file_names_the_holding_process()
    {
        string path = NewFile();
        try
        {
            using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string holders = FileLockInspector.Describe(path);

            Assert.NotEqual("未知程序", holders);
            Assert.Contains("pid", holders, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unlocked_file_reports_no_holder()
    {
        string path = NewFile();
        try
        {
            // Restart Manager lists processes, not handles: an open file with no other holder
            // (this test) yields nothing, which the message renders as "未知程序".
            Assert.Equal("未知程序", FileLockInspector.Describe(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
