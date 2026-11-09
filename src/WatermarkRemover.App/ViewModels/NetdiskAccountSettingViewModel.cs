using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One row of the dynamic credential form, driven by a provider-declared field.</summary>
public sealed class NetdiskCredentialFieldViewModel : ObservableObject
{
    private string _value = string.Empty;

    public NetdiskCredentialFieldViewModel(NetdiskCredentialField field) => Field = field;

    public NetdiskCredentialField Field { get; }

    public string Key => Field.Key;

    public string Label => Field.Label;

    public string? Hint => Field.Hint;

    public bool IsSecret => Field.Kind == NetdiskFieldKind.Secret;

    public bool IsPlain => !IsSecret;

    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

/// <summary>
/// The 「网盘设置」 group: pick a product, edit its credential form (generated from the provider's
/// field declarations), then test the connection, run the OAuth flow or save the account. A new
/// netdisk product shows up here with its own form without any UI change. Shared by the settings
/// section and the management window's re-authorize panel — one instance, two views.
/// </summary>
public sealed class NetdiskAccountSettingViewModel : ObservableObject
{
    private readonly NetdiskProviderCatalog _catalog;
    private readonly ObservableCollection<NetdiskAccountState> _accounts;
    private readonly Action<NetdiskAccountState> _upsertAccount;
    private readonly Action<string, string> _removeAccount;
    private readonly Action<string, Exception?> _trace;

    private INetdiskProvider? _selectedProvider;
    private string? _selectedAccountName;
    private string _accountName = "默认账号";
    private string _authorizationCode = string.Empty;
    private string _statusText = "填写凭证后可测试连接、授权或保存账号。";
    private string _oauthState = string.Empty;
    private DwsProfileInfo? _selectedDwsProfile;

    public NetdiskAccountSettingViewModel(
        NetdiskProviderCatalog catalog,
        ObservableCollection<NetdiskAccountState> accounts,
        Action<NetdiskAccountState> upsertAccount,
        Action<string, string> removeAccount,
        Action<string, Exception?> trace)
    {
        _catalog = catalog;
        _accounts = accounts;
        _upsertAccount = upsertAccount;
        _removeAccount = removeAccount;
        _trace = trace;

        SaveAccountCommand = new RelayCommand(SaveAccount, () => SelectedProvider is not null && !string.IsNullOrWhiteSpace(AccountName));
        DeleteAccountCommand = new RelayCommand(DeleteAccount, () => SelectedProvider is not null && !string.IsNullOrWhiteSpace(SelectedAccountName));
        TestConnectionCommand = new RelayCommand(() => _ = TestConnectionAsync(), () => SelectedProvider is not null);
        OpenAuthorizePageCommand = new RelayCommand(OpenAuthorizePage, () => SelectedProvider?.OAuth is not null);
        ExchangeCodeCommand = new RelayCommand(() => _ = ExchangeCodeAsync(), () => SelectedProvider?.OAuth is not null && !string.IsNullOrWhiteSpace(AuthorizationCode));
        ScanLoginCommand = new RelayCommand(() => _ = ScanLoginAsync(), () => SelectedProvider?.SupportsCliLogin == true);
        RefreshDwsProfilesCommand = new RelayCommand(
            () => _ = RefreshDwsProfilesAsync(), () => IsDingTalk);
        AddDwsProfileCommand = new RelayCommand(AddDwsProfile,
            () => IsDingTalk && SelectedDwsProfile is not null);

        SelectedProvider = Providers.FirstOrDefault();
    }

    /// <summary>
    /// Raised after an account is saved by login/import. The 网盘管理 window listens so a freshly
    /// authorized product (钉钉, 阿里云盘) becomes the active one instead of staying on the old
    /// product's listing — the "logged in but the page still shows 阿里云盘" trap.
    /// </summary>
    public event Action<string, string>? AccountSaved;

    public IReadOnlyList<INetdiskProvider> Providers => _catalog.All;

    public ObservableCollection<NetdiskCredentialFieldViewModel> Fields { get; } = new();

    /// <summary>Names of the saved accounts of the selected product, for reload/delete.</summary>
    public ObservableCollection<string> AccountNames { get; } = new();

    public INetdiskProvider? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                RebuildForm();
                OnPropertyChanged(nameof(HasOAuth));
                OnPropertyChanged(nameof(HasCliLogin));
                OnPropertyChanged(nameof(IsDingTalk));
                OnPropertyChanged(nameof(IsTianyi));
                RefreshDwsProfilesCommand.RaiseCanExecuteChanged();
                AddDwsProfileCommand.RaiseCanExecuteChanged();
                if (IsDingTalk)
                {
                    _ = RefreshDwsProfilesAsync();
                }
            }
        }
    }

    public bool HasOAuth => SelectedProvider?.OAuth is not null;

    /// <summary>Whether the assisted scan login button is offered for the selected product.</summary>
    public bool HasCliLogin => SelectedProvider?.SupportsCliLogin == true;

    /// <summary>钉钉 shows the local dws profile picker (multi-account, independent drives).</summary>
    public bool IsDingTalk => SelectedProvider is DingTalkDriveProvider;

    /// <summary>天翼云盘 offers the embedded-browser login that harvests the Cookie automatically.</summary>
    public bool IsTianyi => SelectedProvider is TianyiDriveProvider;

    /// <summary>Every identity dws already keeps on this machine.</summary>
    public ObservableCollection<DwsProfileInfo> DwsProfiles { get; } = new();

    public DwsProfileInfo? SelectedDwsProfile
    {
        get => _selectedDwsProfile;
        set
        {
            if (SetProperty(ref _selectedDwsProfile, value))
            {
                AddDwsProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Picking a saved account reloads its values into the form.</summary>
    public string? SelectedAccountName
    {
        get => _selectedAccountName;
        set
        {
            if (SetProperty(ref _selectedAccountName, value) && value is not null)
            {
                LoadAccount(value);
            }
        }
    }

    public string AccountName { get => _accountName; set => SetProperty(ref _accountName, value); }

    public string AuthorizationCode { get => _authorizationCode; set => SetProperty(ref _authorizationCode, value); }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public RelayCommand SaveAccountCommand { get; }

    public RelayCommand DeleteAccountCommand { get; }

    public RelayCommand TestConnectionCommand { get; }

    public RelayCommand OpenAuthorizePageCommand { get; }

    public RelayCommand ExchangeCodeCommand { get; }

    public RelayCommand ScanLoginCommand { get; }

    public RelayCommand RefreshDwsProfilesCommand { get; }

    public RelayCommand AddDwsProfileCommand { get; }

    private void RebuildForm()
    {
        Fields.Clear();
        if (SelectedProvider is not null)
        {
            foreach (var field in SelectedProvider.CredentialFields)
            {
                Fields.Add(new NetdiskCredentialFieldViewModel(field));
            }
        }

        AccountNames.Clear();
        foreach (var name in _accounts
            .Where(a => a.ProviderId == SelectedProvider?.Id)
            .Select(a => a.Name)
            .Distinct(StringComparer.Ordinal))
        {
            AccountNames.Add(name);
        }

        _selectedAccountName = null;
        OnPropertyChanged(nameof(SelectedAccountName));
    }

    private void LoadAccount(string name)
    {
        var account = _accounts.FirstOrDefault(a => a.ProviderId == SelectedProvider?.Id && a.Name == name);
        if (account is null)
        {
            return;
        }

        AccountName = account.Name;
        foreach (var field in Fields)
        {
            field.Value = account.Values.TryGetValue(field.Key, out var value) ? value : string.Empty;
        }

        StatusText = $"已载入账号「{name}」。";
    }

    private NetdiskCredentials CollectCredentials()
    {
        var values = Fields
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        return new NetdiskCredentials(values);
    }

    /// <summary>
    /// Adds an account produced by a portable login pack (CLI-hosted credentials) and refreshes
    /// the account-name list, so the 网盘 section shows it without a restart.
    /// </summary>
    public void AddImportedCliAccount(string name, IReadOnlyDictionary<string, string> values)
    {
        _upsertAccount(new NetdiskAccountState("alipan", name, values));
        RefreshAccountNames();
    }

    private void SaveAccount()
    {
        if (SelectedProvider is null || string.IsNullOrWhiteSpace(AccountName))
        {
            return;
        }

        _upsertAccount(new NetdiskAccountState(SelectedProvider.Id, AccountName.Trim(), CollectCredentials().Values));
        RefreshAccountNames();
        StatusText = $"账号「{AccountName.Trim()}」已保存。";
        AccountSaved?.Invoke(SelectedProvider.Id, AccountName.Trim());
    }

    private void DeleteAccount()
    {
        if (SelectedProvider is null || string.IsNullOrWhiteSpace(SelectedAccountName))
        {
            return;
        }

        _removeAccount(SelectedProvider.Id, SelectedAccountName);
        RefreshAccountNames();
        StatusText = $"账号「{SelectedAccountName}」已删除。";
        _selectedAccountName = null;
        OnPropertyChanged(nameof(SelectedAccountName));
    }

    private void RefreshAccountNames()
    {
        AccountNames.Clear();
        foreach (var name in _accounts
            .Where(a => a.ProviderId == SelectedProvider?.Id)
            .Select(a => a.Name)
            .Distinct(StringComparer.Ordinal))
        {
            AccountNames.Add(name);
        }
    }

    private async Task TestConnectionAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        StatusText = "正在测试连接…";
        try
        {
            await using var session = await SelectedProvider.ConnectAsync(CollectCredentials());
            var quota = await session.GetQuotaAsync();
            StatusText =
                $"连接成功：{session.Account.DisplayName}，已用 {NetdiskItemViewModel.FormatBytes(quota.UsedBytes)} / 共 {NetdiskItemViewModel.FormatBytes(quota.TotalBytes)}。";
        }
        catch (Exception ex)
        {
            _trace($"网盘「{SelectedProvider.DisplayName}」测试连接失败：{ex.Message}", ex);
            StatusText = $"连接失败：{ex.Message}";
        }
    }

    private void OpenAuthorizePage()
    {
        if (SelectedProvider?.OAuth is null)
        {
            return;
        }

        try
        {
            _oauthState = Guid.NewGuid().ToString("N");
            var url = SelectedProvider.OAuth.BuildAuthorizeUrl(CollectCredentials(), _oauthState);
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            StatusText = "已打开授权页，请把回调地址中的授权码粘贴到下方后点击「兑换授权码」。";
        }
        catch (Exception ex)
        {
            _trace("打开网盘授权页失败", ex);
            StatusText = $"打开授权页失败：{ex.Message}";
        }
    }

    private async Task ExchangeCodeAsync()
    {
        if (SelectedProvider?.OAuth is null)
        {
            return;
        }

        try
        {
            var credentials = await SelectedProvider.OAuth.ExchangeCodeAsync(
                CollectCredentials(), AuthorizationCode.Trim(), _oauthState);
            foreach (var field in Fields)
            {
                if (credentials.Values.TryGetValue(field.Key, out var value))
                {
                    field.Value = value;
                }
            }

            AuthorizationCode = string.Empty;
            SaveAccount();
            StatusText = "授权成功，令牌已写入并保存。";
        }
        catch (Exception ex)
        {
            _trace("网盘授权码兑换失败", ex);
            StatusText = $"授权失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Assisted scan login for CLI-hosted products. 阿里云盘 replays the CLI's browser dance over
    /// HTTP; 钉钉 delegates to its official CLI device flow. On success the account is saved;
    /// renewal later happens automatically through the local CLI.
    /// </summary>
    private async Task ScanLoginAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        if (SelectedProvider is DingTalkDriveProvider)
        {
            await ScanLoginDingTalkAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            StatusText = "正在创建登录会话…";
            var loginService = new AliyunpanCliLoginService(new HttpClient());
            var deviceId = AliyunpanCliRuntime.TryReadDeviceId() ?? Guid.NewGuid().ToString("N");
            var ticket = await loginService.BeginAsync(deviceId).ConfigureAwait(true);
            Process.Start(new ProcessStartInfo(ticket.AuthorizeUrl.ToString()) { UseShellExecute = true });
            StatusText = "已打开授权页：完成「允许授权 + App 扫码」两次登录后，本页自动完成（约 1 分钟内）。";

            var result = await loginService.WaitForTokenAsync(ticket.TicketId).ConfigureAwait(true);

            // The scan flow saves by itself, so an untitled account still needs a usable name —
            // the product's display name is the natural default ("阿里云盘").
            if (string.IsNullOrWhiteSpace(AccountName))
            {
                AccountName = SelectedProvider.DisplayName;
            }

            var values = new Dictionary<string, string>(CollectCredentials().Values, StringComparer.Ordinal)
            {
                [AliyunpanCliRuntime.CliLoginKey] = "true",
                [AliyunpanCliRuntime.CliDeviceIdKey] = deviceId,
                [AliyunTokenService.AccessTokenKey] = result.AccessToken,
                [AliyunTokenService.ExpiresAtKey] = result.ExpiresAt.ToString("O"),
            };
            _upsertAccount(new NetdiskAccountState(SelectedProvider.Id, AccountName.Trim(), values));
            RefreshAccountNames();

            foreach (var field in Fields)
            {
                if (values.TryGetValue(field.Key, out var value))
                {
                    field.Value = value;
                }
            }

            StatusText = $"登录成功，账号「{AccountName.Trim()}」已保存。令牌续期将由本机 aliyunpan 工具自动完成。";
            AccountSaved?.Invoke(SelectedProvider.Id, AccountName.Trim());
        }
        catch (Exception ex)
        {
            _trace("网盘扫码登录失败", ex);
            StatusText = $"扫码登录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 钉钉云盘 login: the official dws CLI runs its device flow (prints a user code + URL), this
    /// method opens the authorization page and surfaces the code; dws itself polls, exchanges and
    /// stores the tokens — the app never holds one. Afterwards the account is saved with just the
    /// profile selector, and later sessions renew tokens through the CLI automatically.
    /// </summary>
    private async Task ScanLoginDingTalkAsync()
    {
        var ui = SynchronizationContext.Current ?? new SynchronizationContext();
        // Status goes to the panel; the same text lands in the log so a stuck login can be
        // traced afterwards (this path had zero logging once and gave the user a silent hang).
        void SetStatus(string text)
        {
            _trace($"钉钉登录：{text}", null);
            ui.Post(_ => StatusText = text, null);
        }

        try
        {
            var executable = DwsCliRunner.LocateExecutable();
            if (executable is null)
            {
                StatusText = DwsCliRunner.InstallHint;
                return;
            }

            // Re-authorizing an existing account must target that account's own dws profile;
            // a brand-new account lets dws bind whoever scans.
            var existing = _accounts.FirstOrDefault(a =>
                a.ProviderId == SelectedProvider!.Id && a.Name == SelectedAccountName);
            var reauthProfile = existing?.Values.GetValueOrDefault(DingTalkDriveProvider.ProfileKey);
            var before = await DingTalkDriveProvider.ListProfilesAsync(executable).ConfigureAwait(true);

            SetStatus("正在发起钉钉扫码登录（设备授权）…");
            var runner = new DwsCliRunner(executable, reauthProfile);
            await new DwsDeviceLoginService().RunAsync(runner, prompt =>
            {
                if (prompt.Completed)
                {
                    SetStatus("授权流程已结束，正在确认登录状态…");
                    return;
                }

                if (prompt.AuthorizeUrl is { } url)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        _trace("打开钉钉授权页失败", ex);
                    }

                    SetStatus($"授权码 {prompt.UserCode}：浏览器已打开授权页，请用钉钉 App 扫码并确认。");
                }
                else
                {
                    SetStatus($"请在浏览器打开钉钉授权页并输入授权码 {prompt.UserCode}。");
                }
            }).ConfigureAwait(true);

            var after = await DingTalkDriveProvider.ListProfilesAsync(executable).ConfigureAwait(true);
            var profile = DingTalkDriveProvider.PickAuthorizedProfile(before, after, reauthProfile);
            if (profile is null)
            {
                throw new NetdiskAuthException(
                    "设备授权流程已结束，但没有解析到本次授权的钉钉账号。", "DwsProfileNotFound");
            }

            var values = new Dictionary<string, string>(CollectCredentials().Values, StringComparer.Ordinal)
            {
                [DingTalkDriveProvider.CliLoginKey] = "true",
                [DingTalkDriveProvider.ProfileKey] = profile.Profile,
            };

            // One app account per dws profile. A new identity gets the profile's own label
            // (企业·姓名) so a second scan cannot overwrite the first account; re-auth keeps
            // the name the user already saved.
            string accountName = existing?.Name ?? profile.DisplayName;

            _upsertAccount(new NetdiskAccountState(SelectedProvider!.Id, accountName, values));
            RefreshAccountNames();
            SelectedAccountName = accountName;
            AccountName = accountName;

            DwsProfiles.Clear();
            foreach (var item in after)
            {
                DwsProfiles.Add(item);
            }

            SelectedDwsProfile = DwsProfiles.FirstOrDefault(p =>
                string.Equals(p.Profile, profile.Profile, StringComparison.Ordinal));

            StatusText = $"登录成功：{profile.DisplayName}，账号「{accountName}」已保存。令牌续期由本机 dws 工具自动完成。";
            AccountSaved?.Invoke(SelectedProvider.Id, accountName);
        }
        catch (Exception ex)
        {
            _trace("钉钉云盘扫码登录失败", ex);
            StatusText = $"钉钉扫码登录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Applies a Cookie harvested by the embedded browser login, then saves the account. The
    /// cookie is a secret field, so persistence encrypts it through the same DPAPI path.
    /// </summary>
    public void ApplyTianyiCookie(string cookie)
    {
        if (SelectedProvider is not TianyiDriveProvider || string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }

        var field = Fields.FirstOrDefault(f => f.Key == TianyiDriveProvider.CookieKey);
        if (field is null)
        {
            return;
        }

        field.Value = cookie.Trim();

        // A brand-new form still carries the "默认账号" placeholder; give the account a telling
        // name instead so a second login does not silently overwrite the first one.
        if (string.IsNullOrWhiteSpace(AccountName) ||
            string.Equals(AccountName.Trim(), "默认账号", StringComparison.Ordinal))
        {
            AccountName = "天翼云盘";
        }

        SaveAccount();
        StatusText = $"已自动获取 Cookie，账号「{AccountName.Trim()}」已保存。如需添加第二个天翼账号，请先修改账号名再登录。";
    }

    /// <summary>Lists the identities dws already holds (no scan needed).</summary>
    private async Task RefreshDwsProfilesAsync()
    {
        if (!IsDingTalk)
        {
            return;
        }

        var executable = DwsCliRunner.LocateExecutable();
        if (executable is null)
        {
            StatusText = DwsCliRunner.InstallHint;
            return;
        }

        try
        {
            var profiles = await DingTalkDriveProvider.ListProfilesAsync(executable).ConfigureAwait(true);
            DwsProfiles.Clear();
            foreach (var profile in profiles)
            {
                DwsProfiles.Add(profile);
            }

            SelectedDwsProfile = DwsProfiles.FirstOrDefault(p => p.IsCurrent) ?? DwsProfiles.FirstOrDefault();
            if (DwsProfiles.Count > 0)
            {
                _trace($"钉钉本机登录账号：{DwsProfiles.Count} 个", null);
            }
        }
        catch (Exception ex)
        {
            _trace("读取钉钉本机登录账号失败", ex);
            StatusText = $"读取本机钉钉账号失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Adds an already-authorized dws profile as an app account without scanning — this is how a
    /// second/third DingTalk account (each with its own drive) gets its own row.
    /// </summary>
    private void AddDwsProfile()
    {
        if (SelectedProvider is null || SelectedDwsProfile is null)
        {
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DingTalkDriveProvider.CliLoginKey] = "true",
            [DingTalkDriveProvider.ProfileKey] = SelectedDwsProfile.Profile,
        };

        // Same dws profile = same DingTalk identity: update that account instead of adding a
        // duplicate row for a drive the user already has.
        var existing = _accounts.FirstOrDefault(a =>
            a.ProviderId == SelectedProvider.Id &&
            string.Equals(a.Values.GetValueOrDefault(DingTalkDriveProvider.ProfileKey),
                SelectedDwsProfile.Profile, StringComparison.Ordinal));

        string accountName = existing?.Name ?? SelectedDwsProfile.DisplayName;
        _upsertAccount(new NetdiskAccountState(SelectedProvider.Id, accountName, values));
        RefreshAccountNames();
        SelectedAccountName = accountName;
        AccountName = accountName;
        StatusText = existing is null
            ? $"已添加钉钉账号「{accountName}」（各账号数据互相独立）。"
            : $"已更新钉钉账号「{accountName}」。";
        AccountSaved?.Invoke(SelectedProvider.Id, accountName);
    }
}
