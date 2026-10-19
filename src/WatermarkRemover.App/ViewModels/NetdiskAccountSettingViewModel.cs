using System.Collections.ObjectModel;
using System.Diagnostics;
using WatermarkRemover.Core.Netdisk;

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

        SelectedProvider = Providers.FirstOrDefault();
    }

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
            }
        }
    }

    public bool HasOAuth => SelectedProvider?.OAuth is not null;

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

    private void SaveAccount()
    {
        if (SelectedProvider is null || string.IsNullOrWhiteSpace(AccountName))
        {
            return;
        }

        _upsertAccount(new NetdiskAccountState(SelectedProvider.Id, AccountName.Trim(), CollectCredentials().Values));
        RefreshAccountNames();
        StatusText = $"账号「{AccountName.Trim()}」已保存。";
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
}
