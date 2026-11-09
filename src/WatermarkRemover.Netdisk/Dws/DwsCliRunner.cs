using System.Text.Json;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk;

/// <summary>One finished dws CLI invocation: exit code plus captured output.</summary>
public sealed record DwsCommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Per-invocation knobs on top of the runner's fixed global flags.</summary>
public sealed record DwsRunOptions(
    string? WorkingDirectory = null,
    bool Yes = false,
    int? TimeoutSeconds = null,
    TimeSpan? ProcessTimeout = null)
{
    public static DwsRunOptions Default { get; } = new();
}

/// <summary>
/// Bridge to the locally installed DingTalk Workspace CLI (<c>dws</c>). dws hosts the DingTalk
/// app registration and owns the whole token lifecycle (device-flow login, ~2h access token,
/// ~30d refresh token) inside its own config — this app never sees a credential. Every drive
/// operation is one CLI process with JSON output; the runner only adds global flags. Not sealed:
/// tests substitute canned CLI answers by overriding <see cref="RunAsync"/>.
/// </summary>
public class DwsCliRunner
{
    /// <summary>Credential bag flag marking an account that authenticates through the local dws CLI.</summary>
    public const string CliLoginKey = "dws_cli_login";

    /// <summary>Credential bag key carrying the dws profile selector (corpId:userId), optional.</summary>
    public const string ProfileKey = "dws_profile";

    private const int DefaultProcessTimeoutSeconds = 120;

    public DwsCliRunner(string executablePath, string? profile = null)
    {
        ExecutablePath = executablePath;
        Profile = string.IsNullOrWhiteSpace(profile) ? null : profile;
    }

    public string ExecutablePath { get; }

    /// <summary>Profile selector appended as --profile; null = whatever dws considers current.</summary>
    public string? Profile { get; }

    /// <summary>
    /// Runs one dws command with JSON output. Arguments are the command words only ("drive +list",
    /// "--folder x"); the global flags are appended here so callers cannot forget them. Virtual so
    /// tests can substitute canned CLI answers.
    /// </summary>
    public virtual async Task<DwsCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        DwsRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= DwsRunOptions.Default;
        var argv = BuildArguments(arguments, options, Profile);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = options.WorkingDirectory ?? AppContext.BaseDirectory,
        };
        foreach (var arg in argv)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new NetdiskAuthException("dws 命令行工具启动失败。", "DwsStartFailed");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var processTimeout = options.ProcessTimeout ?? TimeSpan.FromSeconds(DefaultProcessTimeoutSeconds);
        using var timeoutSource = new CancellationTokenSource(processTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw new NetdiskException("dws 命令执行超时，请稍后重试。", "DwsTimeout");
        }

        return new DwsCommandResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    /// <summary>
    /// Command words plus the global flags every call needs. Extracted so the per-account
    /// <c>--profile</c> routing (what keeps two DingTalk accounts' data apart) is unit-testable.
    /// </summary>
    internal static List<string> BuildArguments(
        IReadOnlyList<string> arguments,
        DwsRunOptions options,
        string? profile)
    {
        var argv = new List<string>(arguments)
        {
            "--format", "json",
        };
        if (options.Yes)
        {
            argv.Add("--yes");
        }

        if (options.TimeoutSeconds is { } timeout)
        {
            argv.Add("--timeout");
            argv.Add(timeout.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(profile))
        {
            argv.Add("--profile");
            argv.Add(profile);
        }

        return argv;
    }

    private static void KillQuietly(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (System.InvalidOperationException)
        {
            // Already exited between the timeout and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Termination refused (rights/race) — the timeout error is what matters.
        }
    }

    /// <summary>
    /// Locates dws.exe: explicit env override, the app's bundled tools folder, the user tools
    /// folder where a manual install lands, then PATH. Null when the CLI is not present.
    /// </summary>
    public static string? LocateExecutable()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("WATERMARKREMOVER_DWS_CLI"),
            Path.Combine(AppContext.BaseDirectory, "tools", "dws", "dws.exe"),
        };

        var toolsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tools");
        if (Directory.Exists(toolsRoot))
        {
            foreach (var dir in Directory.GetDirectories(toolsRoot, "dws-*"))
            {
                candidates.Add(Path.Combine(dir, "dws.exe"));
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(dir, "dws.exe"));
        }

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    /// <summary>Install hint shown everywhere a missing CLI blocks the user.</summary>
    public const string InstallHint =
        "未找到 dws 命令行工具（钉钉官方 CLI）。请在 PowerShell 执行 " +
        "irm https://gitee.com/DingTalk-Real-AI/dingtalk-workspace-cli/raw/main/scripts/install.ps1 | iex " +
        "完成安装，然后重新打开本窗口。";

    /// <summary>Parses a dws JSON payload; dws prints exactly one JSON document on success.</summary>
    public static JsonDocument ParseJsonOrThrow(DwsCommandResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new NetdiskException($"{operation}失败：{DescribeError(result)}", "DwsCommandFailed");
        }

        try
        {
            return JsonDocument.Parse(result.StdOut);
        }
        catch (JsonException ex)
        {
            throw new NetdiskException(
                $"{operation}返回了无法解析的内容：{ex.Message}", "DwsBadJson");
        }
    }

    /// <summary>Best-effort human message out of dws's structured error envelope.</summary>
    public static string DescribeError(DwsCommandResult result)
    {
        foreach (var text in new[] { result.StdOut, result.StdErr })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? text;
                }
            }
            catch (JsonException)
            {
                return text.Length > 300 ? text[..300] : text;
            }
        }

        return $"退出码 {result.ExitCode}";
    }
}
