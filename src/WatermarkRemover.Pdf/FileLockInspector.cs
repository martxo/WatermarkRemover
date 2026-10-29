using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Names the programs that hold a PDF open (WPS, a browser preview, an antivirus scanner) so a
/// locked save says whom to close instead of only that it failed. Backed by the Restart Manager
/// API — the same source Windows uses for "which app is using this file".
/// </summary>
public static class FileLockInspector
{
    private const int ErrorMoreData = 234;
    private const int MaxListedProcesses = 8;

    /// <summary>
    /// Short human-readable list, e.g. "WPS Office（wps.exe，pid 1234）"; "未知程序" when the API
    /// cannot tell (no session, access denied, a scanner that hides its identity).
    /// </summary>
    public static string Describe(string path)
    {
        try
        {
            List<string> holders = Query(path);
            if (holders.Count == 0) return "未知程序";

            string head = string.Join("、", holders.Take(3));
            return holders.Count > 3 ? $"{head} 等 {holders.Count} 个程序" : head;
        }
        catch
        {
            // Diagnostics only: never turn a lock message into a crash.
            return "未知程序";
        }
    }

    private static List<string> Query(string path)
    {
        var holders = new List<string>();
        if (!File.Exists(path)) return holders;

        var key = new StringBuilder(64);
        if (RmStartSession(out uint session, 0, key) != 0) return holders;
        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0) return holders;

            uint needed = 0;
            uint count = 0;
            int result = RmGetList(session, out needed, ref count, null, out _);
            if (result != ErrorMoreData || needed == 0) return holders;

            var infos = new RmProcessInfo[Math.Min(needed, MaxListedProcesses)];
            count = (uint)infos.Length;
            result = RmGetList(session, out needed, ref count, infos, out _);
            if (result != 0) return holders;

            for (int i = 0; i < count; i++)
            {
                int pid = infos[i].Process.dwProcessId;
                string app = infos[i].strAppName;
                string exe = $"pid {pid}";
                try
                {
                    using var process = Process.GetProcessById(pid);
                    exe = $"{process.ProcessName}.exe，pid {pid}";
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // The holder exited between the two calls — keep the pid as the only fact.
                }

                holders.Add(string.IsNullOrWhiteSpace(app) ? exe : $"{app}（{exe}）");
            }

            return holders;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle, uint fileCount, string[] fileNames,
        uint applicationCount, RmUniqueProcess[]? applications,
        uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle, out uint needed, ref uint count,
        [Out] RmProcessInfo[]? processInfo, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RmAppType
    {
        UnknownApp = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public RmAppType ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }
}
