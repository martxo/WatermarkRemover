using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// Bridge to the locally installed tickstep/aliyunpan CLI. The CLI's author hosts the alipan
/// open-platform app registration and keeps the refresh token server-side, so the CLI stays
/// logged in for months and refreshes by itself. This class reuses that machinery: the login
/// flow reads the CLI's config for a stable device id, and when our access token expires we
/// run one cheap CLI command (<c>who</c>) to trigger its built-in refresh, then read the fresh
/// token back from its config file. Nothing beyond process stdout and the config file touches
/// this class — the CLI keeps owning its own credential lifecycle.
/// </summary>
public static class AliyunpanCliRuntime
{
    /// <summary>Credential bag flag marking an account that renews through the CLI.</summary>
    public const string CliLoginKey = "cli_login";

    /// <summary>Device id shared with the CLI config, used as the login-session key.</summary>
    public const string CliDeviceIdKey = "cli_device_id";

    private const string ConfigFileName = "aliyunpan_config.json";

    /// <summary>
    /// Locates aliyunpan.exe: explicit env override, then the app folder, then the user tools
    /// folder where the manual install lands. Null when the CLI is not present.
    /// </summary>
    public static string? LocateExecutable()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("WATERMARKREMOVER_ALIYUNPAN_CLI"),
            Path.Combine(AppContext.BaseDirectory, "tools", "aliyunpan", "aliyunpan.exe"),
            Path.Combine(AppContext.BaseDirectory, "aliyunpan.exe"),
        };

        var toolsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tools");
        if (Directory.Exists(toolsRoot))
        {
            foreach (var dir in Directory.GetDirectories(toolsRoot, "aliyunpan-*"))
            {
                candidates.Add(Path.Combine(dir, "aliyunpan.exe"));
            }
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Path of the CLI's config file that sits next to its executable.</summary>
    public static string ConfigPathFor(string executablePath) =>
        Path.Combine(Path.GetDirectoryName(executablePath) ?? ".", ConfigFileName);

    /// <summary>
    /// Reads the device id the CLI uses as its identity. Returns null when the CLI or its config
    /// is missing — the login service then generates a random one instead.
    /// </summary>
    public static string? TryReadDeviceId()
    {
        var exe = LocateExecutable();
        if (exe is null || !File.Exists(ConfigPathFor(exe)))
        {
            return null;
        }

        return TryReadConfig(ConfigPathFor(exe), out var deviceId, out _) ? deviceId : null;
    }

    /// <summary>
    /// Renews the access token through the CLI: one <c>who</c> run makes the CLI refresh its
    /// token (server-side refresh token) and persist it; the fresh pair is read back from the
    /// config and returned as a credential bag on top of <paramref name="baseCredentials"/>.
    /// </summary>
    public static Task<NetdiskCredentials> RefreshViaCliAsync(
        NetdiskCredentials baseCredentials,
        CancellationToken cancellationToken = default)
    {
        var executable = LocateExecutable()
            ?? throw new NetdiskAuthException(
                "未找到 aliyunpan 命令行工具，无法自动续期阿里云盘账号。", "CliNotFound");

        RunCliCommand(executable, "who", cancellationToken);

        if (!TryReadConfig(ConfigPathFor(executable), out _, out var token))
        {
            throw new NetdiskAuthException(
                "读取 aliyunpan 配置失败，无法获取续期后的访问令牌。", "CliConfigUnreadable");
        }

        if (string.IsNullOrEmpty(token.AccessToken))
        {
            throw new NetdiskAuthException(
                "aliyunpan 未返回有效访问令牌，可能需要重新扫码登录。", "CliTokenEmpty");
        }

        return Task.FromResult(BuildCredentials(baseCredentials, token.AccessToken, token.ExpiredAt));
    }

    /// <summary>Parses one CLI config file into its device id and active openapi token.</summary>
    public static bool TryReadConfig(
        string configPath,
        out string? deviceId,
        out (string AccessToken, DateTimeOffset ExpiredAt) token)
    {
        deviceId = null;
        token = (string.Empty, DateTimeOffset.MinValue);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            deviceId = root.TryGetProperty("deviceId", out var id) ? id.GetString() : null;

            if (!root.TryGetProperty("userList", out var users) || users.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var activeUid = root.TryGetProperty("activeUID", out var uid) ? uid.GetString() : null;
            foreach (var user in users.EnumerateArray())
            {
                var userId = user.TryGetProperty("userId", out var uidProp) ? uidProp.GetString() : null;
                if (!string.IsNullOrEmpty(activeUid) && !string.Equals(userId, activeUid, StringComparison.Ordinal))
                {
                    continue;
                }

                if (user.TryGetProperty("openapiToken", out var openToken))
                {
                    var accessToken = openToken.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
                    var expired = openToken.TryGetProperty("expired", out var ex) &&
                                  ex.ValueKind == JsonValueKind.Number
                        ? ex.GetInt64()
                        : 0;
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        token = (accessToken, DateTimeOffset.FromUnixTimeSeconds(expired));
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Credential bag for a CLI-renewed token: flag, device id and the expiry.</summary>
    public static NetdiskCredentials BuildCredentials(
        NetdiskCredentials baseCredentials,
        string accessToken,
        DateTimeOffset expiredAt)
    {
        var values = new Dictionary<string, string>(baseCredentials.Values, StringComparer.Ordinal)
        {
            [CliLoginKey] = "true",
            [AliyunTokenService.AccessTokenKey] = accessToken,
            [AliyunTokenService.ExpiresAtKey] = expiredAt.ToString("O", CultureInfo.InvariantCulture),
        };
        return new NetdiskCredentials(values);
    }

    private static void RunCliCommand(string executable, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        if (!process.Start())
        {
            throw new NetdiskAuthException("aliyunpan 命令行工具启动失败。", "CliStartFailed");
        }

        var finished = process.WaitForExit((int)TimeSpan.FromSeconds(60).TotalMilliseconds);
        if (!finished)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the timeout and the kill.
            }

            throw new NetdiskAuthException("aliyunpan 续期超时，请稍后重试。", "CliTimeout");
        }

        if (process.ExitCode != 0)
        {
            throw new NetdiskAuthException(
                "aliyunpan 续期失败，请打开软件网盘设置重新扫码登录。", "CliExitCode");
        }
    }
}
