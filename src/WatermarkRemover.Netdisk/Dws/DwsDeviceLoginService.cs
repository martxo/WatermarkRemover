using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk;

/// <summary>What the device flow shows the user before the scan happens.</summary>
public sealed record DwsDeviceLoginPrompt(string? UserCode, Uri? AuthorizeUrl, bool Completed = false);

/// <summary>
/// Drives <c>dws auth login --device</c> for the app: the CLI prints a user code plus an
/// authorization URL (it opens a browser itself only when a console session owns one), so this
/// service streams the output, extracts both, opens the browser with the pre-filled URL and
/// reports the code back to the UI. The CLI polls the authorization server itself; when the
/// process exits the login is either done or failed — a status check then confirms the outcome.
/// </summary>
public sealed class DwsDeviceLoginService
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

    private static readonly Regex AnsiPattern = new("\x1b\\[[0-9;]*m", RegexOptions.Compiled);

    /// <summary>
    /// Runs the device flow to completion. <paramref name="onPrompt"/> fires once the code and URL
    /// have been parsed (the browser is opened here too), and once more with
    /// <see cref="DwsDeviceLoginPrompt.Completed"/> when the flow ended. Returns only after the
    /// CLI process exits.
    /// </summary>
    public async Task RunAsync(
        DwsCliRunner runner,
        Action<DwsDeviceLoginPrompt> onPrompt,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runner.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("login");
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add("--no-browser");
        if (runner.Profile is not null)
        {
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(runner.Profile);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new NetdiskAuthException("dws 登录进程启动失败。", "DwsStartFailed");
        }

        var userCode = null as string;
        var authorizeUrl = null as Uri;
        var reported = false;

        // The device flow prints everything (code, URLs, polling progress) on STDERR; stdout
        // stays empty — verified against dws v1.0.67. Both streams feed the same parser so a
        // future stdout move cannot silently regress this.
        void HandleLine(string? raw)
        {
            if (raw is null)
            {
                return;
            }

            var line = AnsiPattern.Replace(raw, string.Empty);
            if (userCode is null &&
                line.TrimStart().StartsWith("授权码", StringComparison.Ordinal) &&
                line.Contains(':'))
            {
                var parts = line.Split(':', 2);
                var value = parts.Length == 2 ? parts[1].Trim() : null;
                if (!string.IsNullOrEmpty(value))
                {
                    userCode = value;
                }
            }
            else if (authorizeUrl is null &&
                     Uri.TryCreate(line.Trim(), UriKind.Absolute, out var url) &&
                     url.Scheme is "http" or "https")
            {
                authorizeUrl = url;
            }

            if (!reported && userCode is not null && authorizeUrl is not null)
            {
                reported = true;
                onPrompt(new DwsDeviceLoginPrompt(userCode, authorizeUrl));
            }
        }

        process.OutputDataReceived += (_, args) => HandleLine(args.Data);
        process.ErrorDataReceived += (_, args) => HandleLine(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(LoginTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the timeout and the kill.
            }

            onPrompt(new DwsDeviceLoginPrompt(userCode, null, Completed: true));
            throw new NetdiskAuthException(
                "钉钉登录超时（10 分钟），请重新扫码。", "DwsLoginTimeout");
        }

        onPrompt(new DwsDeviceLoginPrompt(userCode, null, Completed: true));
        if (process.ExitCode != 0)
        {
            throw new NetdiskAuthException(
                "钉钉登录未完成（设备授权流程已取消或失败）。", "DwsLoginFailed");
        }
    }
}
