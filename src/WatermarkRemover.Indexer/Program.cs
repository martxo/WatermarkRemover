using System.Diagnostics;
using WatermarkRemover.Index.Contracts;
using WatermarkRemover.Index.Indexing;
using WatermarkRemover.Index.Ipc;
using WatermarkRemover.Index.Setup;

namespace WatermarkRemover.Indexer;

/// <summary>
/// Host executable for the file index. Three roles, selected by the first argument:
///   (none) / --host : run the privileged index host (started by the logon task)
///   --install       : copy this exe to ProgramData, register the logon task, start the host
///   --uninstall     : stop the host, remove the task and the installed copy
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitNotElevated = 5;

    private static int Main(string[] args)
    {
        string command = args.Length == 0 ? "--host" : args[0];

        try
        {
            return command switch
            {
                "--host" => RunHost(),
                "--install" => Install(),
                "--uninstall" => Uninstall(),
                _ => ExitFailed,
            };
        }
        catch (Exception ex)
        {
            TryLog(ex.ToString());
            return ExitFailed;
        }
    }

    private static int RunHost()
    {
        using var mutex = new Mutex(true, @"Local\WatermarkRemover.Indexer.v1", out bool createdNew);
        if (!createdNew) return ExitOk;

        using var cancellation = new CancellationTokenSource();

        var service = new IndexHostService(IndexLayout.StateDirectory);
        service.StartAll(cancellation.Token);

        var server = new IndexPipeServer(service, cancellation.Cancel);
        try
        {
            server.RunAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown requested over the pipe.
        }

        return ExitOk;
    }

    private static int Install()
    {
        if (!IndexSetupService.IsElevated()) return ExitNotElevated;

        TryShutdownRunningHost();

        Directory.CreateDirectory(IndexLayout.InstallDirectory);
        Directory.CreateDirectory(IndexLayout.StateDirectory);

        string self = Environment.ProcessPath ?? string.Empty;
        if (self.Length == 0) return ExitFailed;

        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(IndexLayout.InstalledExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, IndexLayout.InstalledExe, overwrite: true);

        string version = FileVersionInfo.GetVersionInfo(IndexLayout.InstalledExe).FileVersion ?? "0.0.0";
        File.WriteAllText(IndexLayout.VersionFile, version);

        if (!CreateLogonTask(IndexLayout.InstalledExe)) return ExitFailed;

        StartHost(IndexLayout.InstalledExe);
        return WaitForPipe(TimeSpan.FromSeconds(15)) ? ExitOk : ExitFailed;
    }

    private static int Uninstall()
    {
        if (!IndexSetupService.IsElevated()) return ExitNotElevated;

        TryShutdownRunningHost();
        RunSchtasks("/Delete", "/TN", IndexLayout.TaskName, "/F");

        TryDeleteDirectory(IndexLayout.InstallDirectory);
        TryDeleteDirectory(IndexLayout.StateDirectory);
        return ExitOk;
    }

    private static bool CreateLogonTask(string exePath)
    {
        // The action has no arguments: the exe defaults to --host. Keeping the whole command a
        // single space-free path avoids schtasks' notoriously fragile /TR quoting.
        return RunSchtasks(
            "/Create", "/F",
            "/TN", IndexLayout.TaskName,
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/TR", exePath) == ExitOk;
    }

    private static void StartHost(string exePath)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--host");

        using Process? process = Process.Start(startInfo);
        process?.WaitForExit(200);
    }

    private static bool WaitForPipe(TimeSpan timeout)
    {
        var client = new IndexPipeClient();
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            IndexResponse? response = client
                .SendAsync(new IndexRequest { Cmd = "status" }, TimeSpan.FromMilliseconds(600))
                .GetAwaiter().GetResult();

            if (response is { Ok: true }) return true;
            Thread.Sleep(300);
        }

        return false;
    }

    private static void TryShutdownRunningHost()
    {
        var client = new IndexPipeClient();
        IndexResponse? response = client
            .SendAsync(new IndexRequest { Cmd = "shutdown" }, TimeSpan.FromMilliseconds(800))
            .GetAwaiter().GetResult();

        if (response is { Ok: true }) Thread.Sleep(1200);
    }

    private static int RunSchtasks(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null) return -1;

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            TryLog($"schtasks {string.Join(' ', arguments)} -> {process.ExitCode} {output.Trim()} {error.Trim()}");
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            TryLog("schtasks failed: " + ex);
            return -1;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            TryLog($"delete {path} failed: {ex.Message}");
        }
    }

    private static void TryLog(string message)
    {
        try
        {
            Directory.CreateDirectory(IndexLayout.DataDirectory);
            File.AppendAllText(
                Path.Combine(IndexLayout.DataDirectory, "indexer.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never be the reason a service operation fails.
        }
    }
}
