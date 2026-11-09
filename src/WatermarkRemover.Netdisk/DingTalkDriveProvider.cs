using System.Text.Json;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk;

/// <summary>One account slot dws keeps locally (corp + user), i.e. one DingTalk identity.</summary>
public sealed record DwsProfileInfo(
    string Profile,
    string CorpName,
    string UserName,
    bool IsCurrent,
    DateTimeOffset? LastLoginAt)
{
    /// <summary>Stable label shown in pickers: 企业名·人名 (falls back to whichever exists).</summary>
    public string DisplayName => (string.IsNullOrEmpty(CorpName), string.IsNullOrEmpty(UserName)) switch
    {
        (true, true) => Profile,
        (false, true) => CorpName,
        (true, false) => UserName,
        _ => $"{CorpName}·{UserName}",
    };
}

/// <summary>
/// 钉钉云盘 (DingTalk Drive) product implementation. There is nothing to paste or store: the
/// local dws CLI performs the device-flow scan login and keeps every token in its own config, so
/// the account saved here is just the profile selector. <see cref="SupportsCliLogin"/> is the only
/// login path — OAuth/paste-token UI is intentionally absent.
/// </summary>
public sealed class DingTalkDriveProvider : INetdiskProvider
{
    /// <summary>Credential key marking a dws-CLI-backed account.</summary>
    public const string CliLoginKey = DwsCliRunner.CliLoginKey;

    /// <summary>Credential key carrying the dws profile selector (corpId:userId), optional.</summary>
    public const string ProfileKey = DwsCliRunner.ProfileKey;

    public string Id => "dingtalk";

    public string DisplayName => "钉钉云盘";

    /// <summary>No credential form: login happens by scanning with the DingTalk app.</summary>
    public IReadOnlyList<NetdiskCredentialField> CredentialFields { get; } = Array.Empty<NetdiskCredentialField>();

    public INetdiskOAuthFlow? OAuth => null;

    public bool SupportsCliLogin => true;

    public async Task<INetdiskSession> ConnectAsync(
        NetdiskCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var executable = DwsCliRunner.LocateExecutable()
            ?? throw new NetdiskAuthException(DwsCliRunner.InstallHint, "DwsNotFound");

        if (!await IsAuthenticatedAsync(executable, credentials, cancellationToken).ConfigureAwait(false))
        {
            throw new NetdiskAuthException(
                "钉钉尚未登录或登录已失效，请在网盘设置中点击「扫码登录」。", "DwsNotLoggedIn");
        }

        var runner = new DwsCliRunner(executable, credentials.Get(ProfileKey));
        var account = await ResolveAccountAsync(runner, cancellationToken).ConfigureAwait(false);

        // Space discovery is a convenience layer: a permission/API hiccup must not block the
        // whole connection — the CLI's own default space still works.
        DingTalkSpaces spaces;
        try
        {
            spaces = await DingTalkDriveSession.DiscoverSpacesAsync(runner, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NetdiskException or System.Text.Json.JsonException or IOException)
        {
            spaces = DingTalkSpaces.Default;
        }

        return new DingTalkDriveSession(runner, credentials, account, spaces);
    }

    /// <summary>Read-only login check; never mutates or refreshes dws's stored state.</summary>
    public static async Task<bool> IsAuthenticatedAsync(
        string executable,
        NetdiskCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var runner = new DwsCliRunner(executable, credentials.Get(ProfileKey));
        var result = await runner.RunAsync(
            new[] { "auth", "status", "--readonly" }, DwsRunOptions.Default, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            return DwsJson.FirstBool(doc.RootElement, "authenticated");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every identity dws keeps on this machine. DingTalk accounts are independent per profile,
    /// so the settings UI lists these to add/switch accounts without another scan.
    /// </summary>
    public static Task<IReadOnlyList<DwsProfileInfo>> ListProfilesAsync(
        string executable,
        CancellationToken cancellationToken = default)
        => ListProfilesAsync(new DwsCliRunner(executable), cancellationToken);

    /// <summary>Same listing against a caller-supplied runner (tests substitute canned output).</summary>
    public static async Task<IReadOnlyList<DwsProfileInfo>> ListProfilesAsync(
        DwsCliRunner runner,
        CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            new[] { "profile", "list" }, DwsRunOptions.Default, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;
        var profiles = new List<DwsProfileInfo>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("profiles", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return profiles;
        }

        foreach (var item in array.EnumerateArray())
        {
            var key = DwsJson.FirstString(item, "profile", "corpId");
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            profiles.Add(new DwsProfileInfo(
                key!,
                DwsJson.FirstString(item, "corpName", "corpId") ?? string.Empty,
                DwsJson.FirstString(item, "userName", "userId") ?? string.Empty,
                DwsJson.FirstBool(item, "isCurrent"),
                ParseTime(DwsJson.FirstString(item, "lastLoginAt"))));
        }

        return profiles;
    }

    /// <summary>Identity of one specific profile (re-auth keeps the same account slot).</summary>
    public static async Task<DwsProfileInfo?> FindProfileAsync(
        string executable,
        string profile,
        CancellationToken cancellationToken = default)
    {
        var all = await ListProfilesAsync(executable, cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(p => string.Equals(p.Profile, profile, StringComparison.Ordinal));
    }

    /// <summary>
    /// The identity a login actually authorized. Profiles whose last-login timestamp moved (or
    /// that appeared) are the candidates; if exactly one moved it wins — so scanning a second
    /// DingTalk account while another is selected does NOT overwrite the selected account. When
    /// several moved (or none), an explicit re-auth target wins, then dws's current profile.
    /// </summary>
    public static DwsProfileInfo? PickAuthorizedProfile(
        IReadOnlyList<DwsProfileInfo> before,
        IReadOnlyList<DwsProfileInfo> after,
        string? reauthProfile)
    {
        var moved = after.Where(profile =>
        {
            var previous = before.FirstOrDefault(p =>
                string.Equals(p.Profile, profile.Profile, StringComparison.Ordinal));
            return previous is null ||
                   (profile.LastLoginAt is { } now && previous.LastLoginAt != now);
        }).ToList();

        if (moved.Count == 1)
        {
            return moved[0];
        }

        if (moved.Count > 1)
        {
            return moved.FirstOrDefault(p =>
                       string.Equals(p.Profile, reauthProfile, StringComparison.Ordinal))
                   ?? moved.FirstOrDefault(p => p.IsCurrent)
                   ?? moved[0];
        }

        if (!string.IsNullOrEmpty(reauthProfile))
        {
            var targeted = after.FirstOrDefault(p =>
                string.Equals(p.Profile, reauthProfile, StringComparison.Ordinal));
            if (targeted is not null)
            {
                return targeted;
            }
        }

        return after.FirstOrDefault(p => p.IsCurrent);
    }

    /// <summary>ISO-8601 timestamp out of dws (offset included); null when absent/unparseable.</summary>
    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>
    /// Identity for the management window's header: the dws profile marked current
    /// (corpName·userName). Falls back to a generic label when the CLI answer is unusable.
    /// </summary>
    public static async Task<NetdiskAccountInfo> ResolveAccountAsync(
        DwsCliRunner runner,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await runner.RunAsync(
                new[] { "profile", "list" }, DwsRunOptions.Default, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("profiles", out var profiles) &&
                profiles.ValueKind == JsonValueKind.Array)
            {
                foreach (var profile in profiles.EnumerateArray())
                {
                    if (!DwsJson.FirstBool(profile, "isCurrent"))
                    {
                        continue;
                    }

                    var corp = DwsJson.FirstString(profile, "corpName", "corpId") ?? "钉钉";
                    var user = DwsJson.FirstString(profile, "userName", "userId", "profile");
                    return new NetdiskAccountInfo(
                        DwsJson.FirstString(profile, "profile") ?? corp,
                        user is null ? corp : $"{corp}·{user}");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NetdiskException or IOException)
        {
            // Identity is cosmetic; the drive operations still work without it.
        }

        // Empty UserId = no usable profile selector; callers must not store one.
        return new NetdiskAccountInfo(string.Empty, "钉钉用户");
    }
}
