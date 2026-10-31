using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.Ocr.Layout;
using WatermarkRemover.App.Recognition;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// Backs the settings window: quick-suffix editing with validation, output-name preview, the
/// log panel and save/apply. All user-visible decisions (validation prompts, export, opening the
/// log folder) go through injected services, so this view model can be exercised without UI.
/// Edits land on a working copy of the shared <see cref="AppSettings"/>; only
/// <see cref="Commit"/> (保存) copies them back, so closing the window discards them.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IShellOpenService _shell;
    private readonly AppSettings _target;
    private readonly IRecognitionReportStore _reportStore;
    private int _selectedSuffixIndex = -1;
    private string _newSuffixText = string.Empty;
    private int _logRangeIndex;
    private string _logPreviewText = string.Empty;
    private string _logStatusText = string.Empty;
    private string _reportsPreviewText = string.Empty;
    private string _reportsStatusText = string.Empty;
    private string _searchText = string.Empty;
    private SettingsNavItemViewModel _selectedNav;

    public SettingsViewModel(AppSettings settings)
        : this(settings, DialogService.Default, ShellOpenService.Default)
    {
    }

    internal SettingsViewModel(AppSettings settings, IDialogService dialogs, IShellOpenService shell,
        IRecognitionReportStore? reportStore = null, NetdiskAccountSettingViewModel? netdisk = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _target = settings;
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _reportStore = reportStore ?? new RecognitionReportStore();
        Netdisk = netdisk;

        // Edits land on a working copy so "取消" discards them; Commit copies them back.
        Settings = settings.Clone();

        // Settings nav, registered declaratively: a new section only needs a row here plus a
        // matching ContentControl trigger in SettingsPage — the sidebar/search/title follow.
        NavItems = new ObservableCollection<SettingsNavItemViewModel>
        {
            new("general", "通用设置", "\uE713", "输出 文件名 后缀 目录 自动命名 去除标注 并发"),
            new("scan", "扫描设置", "\uE7F4", "扫描 色彩 黑白 彩色 灰度 自动 连续 批量 ADF 进纸 扫描仪"),
            new("screenshot", "截图设置", "\uE722", "截图 快捷键 全局热键 保存 目录 PNG 区域 标注"),
            new("netdisk", "网盘设置", "\uE753", "网盘 云盘 阿里云盘 授权 令牌 密钥 账号 上传"),
            new("ocr", "OCR 识别", "\uE7C3", "识别 引擎 微信 OCR PP-OCR Windows 文字"),
            new("updates", "软件更新", "\uE72C", "升级 版本 检查更新 云更新 阿里云盘 增量 回滚 startup"),
            new("logs", "日志与排障", "\uE81C", "诊断 排查 导出 预览 记录"),
            new("reports", "识别报告", "\uE8D6", "识别 报告 类型 字段 坐标 复核 训练 朝向"),
        };
        _selectedNav = NavItems[0];
        NavItems[0].IsSelected = true;

        QuickSuffixes = new ObservableCollection<string>(
            settings.QuickSuffixes.Select(s => s.Trim()).Where(s => s.Length > 0));
        _logRangeIndex = 0;

        AddSuffixCommand = new RelayCommand(AddSuffix);
        UpdateSuffixCommand = new RelayCommand(UpdateSuffix, _ => SelectedSuffixIndex >= 0);
        DeleteSuffixCommand = new RelayCommand(DeleteSuffix, _ => SelectedSuffixIndex >= 0);
        MoveUpCommand = new RelayCommand(_ => MoveSelected(-1), _ => SelectedSuffixIndex > 0);
        MoveDownCommand = new RelayCommand(_ => MoveSelected(1), _ => SelectedSuffixIndex >= 0 && SelectedSuffixIndex < QuickSuffixes.Count - 1);
        RefreshLogCommand = new RelayCommand(_ => RefreshLog());
        CopyLogCommand = new RelayCommand(_ => CopyLog());
        ExportLogCommand = new RelayCommand(_ => ExportLog());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        OpenUpdateFolderCommand = new RelayCommand(_ => OpenUpdateFolder());
        CleanUpdateResidueCommand = new RelayCommand(_ => CleanUpdateResidue());
        RefreshUpdateStorage();
        RefreshReportsCommand = new RelayCommand(_ => RefreshReports());
        OpenReportsFolderCommand = new RelayCommand(_ => OpenReportsFolder());
        RefreshCrashReportsCommand = new RelayCommand(_ => RefreshCrashReports());
        OpenCrashFolderCommand = new RelayCommand(_ => OpenCrashFolder());
        UploadCrashReportsCommand = new RelayCommand(_ => _ = UploadCrashReportsAsync());
        RefreshCrashReports();

        ExportLoginPackCommand = new RelayCommand(() => _ = ExportLoginPackAsync());
        ImportLoginPackCommand = new RelayCommand(() => ImportLoginPackRequested?.Invoke(this, EventArgs.Empty));
        LoginPackStatusText = DescribeLoginPack();
    }

    /// <summary>The working copy the window binds to; untouched on cancel.</summary>
    public AppSettings Settings { get; }

    /// <summary>The shared settings (committed state) — cloud updates read account credentials from here.</summary>
    internal AppSettings SourceSettings => _target;

    /// <summary>One-line 阿里云盘 status for the updates section (re-read after the cloud window closes).</summary>
    public string CloudAccountText
    {
        get
        {
            var service = new CloudUpdateService(_target, new DpapiSecretProtector());
            return service.IsConfigured
                ? $"阿里云盘：已登录「{service.AccountName}」，云更新已启用。"
                : "阿里云盘：未登录。每台电脑首次使用请先在「网盘设置」扫码登录一次。";
        }
    }

    public void NotifyCloudChanged() => OnPropertyChanged(nameof(CloudAccountText));

    // ── 登录状态迁移（私有登录包）──

    private string _loginPackStatusText = string.Empty;
    private string _loginPackPassphrase = string.Empty;

    /// <summary>
    /// Passphrase that encrypts an exported pack and opens an encrypted one on the other PC.
    /// Never persisted: it only lives for this window, so a leaked settings file cannot unlock
    /// the cloud copy.
    /// </summary>
    public string LoginPackPassphrase
    {
        get => _loginPackPassphrase;
        set => SetProperty(ref _loginPackPassphrase, value);
    }

    /// <summary>One-line state of the portable aliyunpan login on this machine.</summary>
    public string LoginPackStatusText
    {
        get => _loginPackStatusText;
        private set => SetProperty(ref _loginPackStatusText, value);
    }

    /// <summary>Writes the pack locally and uploads it to /WatermarkRemover/private/.</summary>
    public RelayCommand ExportLoginPackCommand { get; }

    /// <summary>Raises <see cref="ImportLoginPackRequested"/>; the view owns the file picker.</summary>
    public RelayCommand ImportLoginPackCommand { get; }

    /// <summary>Raised when the user asks to import a login pack; the view picks the file.</summary>
    public event EventHandler? ImportLoginPackRequested;

    private async Task ExportLoginPackAsync()
    {
        try
        {
            var service = new CloudUpdateService(_target, new DpapiSecretProtector());
            if (!service.IsConfigured)
            {
                _dialogs.Warn("请先在下方完成阿里云盘扫码登录，再导出登录状态。", "登录状态");
                return;
            }

            // A pack carries live tokens: without a passphrase it would be uploaded in the clear.
            if (string.IsNullOrWhiteSpace(LoginPackPassphrase))
            {
                _dialogs.Warn(
                    "请先输入「登录包口令」。登录包里是账号令牌，必须用口令加密后才能导出/上传。",
                    "登录状态");
                return;
            }

            LoginPackStatusText = "正在导出登录状态到云盘…";
            CloudLoginPackResult result = await service
                .ExportLoginPackAsync(LoginPackPassphrase).ConfigureAwait(true);
            LoginPackStatusText =
                $"已导出（口令加密）：本机 {result.LocalPath}；云端 {result.CloudPath}。" +
                "另一台导入时需要同一口令。";

            // Tokens may have rotated during the export — refresh the account summary.
            NotifyCloudChanged();
            _dialogs.Info(
                "登录状态已导出。\n\n" +
                $"本机文件：{result.LocalPath}\n" +
                $"云端私有文件：{result.CloudPath}\n\n" +
                "这个 login-pack.json 已用口令加密。另一台电脑在「导入登录状态文件」里选中它并输入同一口令即可登录" +
                "（加密包不会在启动时自动导入，必须手动输入口令）。",
                "登录状态");
        }
        catch (Exception ex)
        {
            AppLog.Error("导出阿里云盘登录状态失败", ex);
            LoginPackStatusText = $"导出失败：{ex.Message}";
            _dialogs.Warn($"导出登录状态失败：{ex.Message}", "登录状态");
        }
    }

    /// <summary>Called by the view after the file picker returns a pack path.</summary>
    public void ImportLoginPackFrom(string path)
    {
        try
        {
            CloudUpdateService.ImportLoginPack(path, LoginPackPassphrase);
            if (AliyunpanCliRuntime.TryBuildAccountValues() is { } values)
            {
                Netdisk?.AddImportedCliAccount("阿里云盘（导入）", values);
            }

            LoginPackStatusText = "登录状态已导入，本机已具备阿里云盘登录。";
            NotifyCloudChanged();
            _dialogs.Info(
                "登录状态导入成功。\n\n可以关闭设置后重新检查云更新；" +
                "若程序里已打包 aliyunpan 工具，令牌到期会自动续期。",
                "登录状态");
        }
        catch (Exception ex)
        {
            AppLog.Error("导入阿里云盘登录状态失败", ex);
            LoginPackStatusText = $"导入失败：{ex.Message}";
            _dialogs.Warn($"导入登录状态失败：{ex.Message}", "登录状态");
        }
    }

    private static string DescribeLoginPack()
    {
        try
        {
            return File.Exists(AliyunpanCliRuntime.StableConfigPath)
                ? $"本机登录状态：已就绪（{AliyunpanCliRuntime.StableConfigPath}）"
                : "本机登录状态：未登录。先完成阿里云盘扫码登录，再导出。";
        }
        catch (Exception)
        {
            return "本机登录状态：未知";
        }
    }

    /// <summary>Netdisk account section; accounts persist on their own timeline (instant),
    /// independent of the settings working copy / Commit.</summary>
    public NetdiskAccountSettingViewModel? Netdisk { get; }

    public ObservableCollection<string> QuickSuffixes { get; }

    /// <summary>Left navigation of the settings page, in registration order.</summary>
    public ObservableCollection<SettingsNavItemViewModel> NavItems { get; }

    /// <summary>The section shown on the right; drives its title and content trigger.</summary>
    public SettingsNavItemViewModel SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || !SetProperty(ref _selectedNav, value)) return;
            foreach (var item in NavItems) item.IsSelected = ReferenceEquals(item, value);

            // Engine availability is probed on first visit only: the probe loads engines, so
            // users who never open the OCR section never pay for it.
            if (value.Id == "ocr") StartOcrProbe();

            // Reports change with every run, so the preview re-reads on each visit (small files).
            if (value.Id == "reports") RefreshReports();
        }
    }

    /// <summary>Nav search box text; filters the nav rows (title + keywords).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ApplySearchFilter();
        }
    }

    /// <summary>Current version, shown at the top of the updates section.</summary>
    public string VersionText => $"当前版本：v{UpdateService.CurrentVersion}";

    /// <summary>
    /// Where this copy of the app actually runs. Reinstalling while an old shortcut or a second
    /// copy keeps being opened looks exactly like "updated but still the old version"; this line
    /// makes that difference visible instead of leaving it to a log file.
    /// </summary>
    public string InstallPathText
    {
        get
        {
            string? exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? "运行位置：未知" : $"运行位置：{exe}";
        }
    }

    // ── 更新下载位置与残留清理（updates section, second card）──

    private string _updateResidueSummary = "正在扫描更新缓存…";

    /// <summary>Where update packages land — the Velopack cache for Setup installs, %TEMP% otherwise.</summary>
    public string UpdateStoragePath
    {
        get
        {
            string? packages = UpdateStorage.GetVelopackPackagesDirectory();
            return packages ?? Path.GetTempPath();
        }
    }

    /// <summary>One-line residue tally across both channels, refreshed on entry and after cleaning.</summary>
    public string UpdateResidueSummary
    {
        get => _updateResidueSummary;
        private set => SetProperty(ref _updateResidueSummary, value);
    }

    /// <summary>Opens the primary download location in Explorer.</summary>
    public RelayCommand OpenUpdateFolderCommand { get; }

    /// <summary>Clears leftover packages (old versions, interrupted downloads) after a confirmation.</summary>
    public RelayCommand CleanUpdateResidueCommand { get; }

    private void RefreshUpdateStorage()
    {
        long bytes = 0;
        int count = 0;
        foreach (var location in UpdateStorage.Describe())
        {
            count += location.ResidueCount;
            bytes += location.ResidueBytes;
        }

        UpdateResidueSummary = count == 0
            ? "没有可清理的更新残留。"
            : $"发现 {count} 项更新残留 · 共 {FormatResidueBytes(bytes)}，可安全清理";
    }

    private void CleanUpdateResidue()
    {
        var locations = UpdateStorage.Describe();
        int count = locations.Sum(l => l.ResidueCount);
        long bytes = locations.Sum(l => l.ResidueBytes);
        if (count == 0)
        {
            UpdateResidueSummary = "没有可清理的更新残留。";
            return;
        }

        if (!_dialogs.ConfirmYesNo(
                $"发现 {count} 项更新残留（共 {FormatResidueBytes(bytes)}）。\n\n" +
                "清理不会影响当前已安装的版本；清除缓存后，下一次更新可能需要重新下载完整包。是否清理？",
                "清理更新残留"))
        {
            return;
        }

        var (deleted, freed) = UpdateStorage.Clean();
        AppLog.Info($"更新残留清理：删除 {deleted} 项，释放 {freed} 字节");
        RefreshUpdateStorage();
    }

    private static string FormatResidueBytes(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };

    private void OpenUpdateFolder()
    {
        string? packages = UpdateStorage.GetVelopackPackagesDirectory();
        string dir = packages ?? Path.GetTempPath();
        if (!_shell.TryOpenDirectory(dir, out string? error))
        {
            _dialogs.Warn($"无法打开文件夹：{error ?? dir}", "更新缓存");
        }
    }

    /// <summary>
    /// Output-mode toggle of the general section (radio group in the old window, now VM state):
    /// true = custom directory, false = same directory as the source file.
    /// </summary>
    public bool UseCustomDirectory
    {
        get => Settings.OutputMode == OutputMode.CustomDirectory;
        set
        {
            OutputMode mode = value ? OutputMode.CustomDirectory : OutputMode.SameDirectory;
            if (Settings.OutputMode == mode) return;
            Settings.OutputMode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseSameDirectory));
            OnPropertyChanged(nameof(CustomDirEnabled));
        }
    }

    /// <summary>Companion radio state ("与源文件相同目录"); kept in sync by <see cref="UseCustomDirectory"/>.</summary>
    public bool UseSameDirectory
    {
        get => !UseCustomDirectory;
        set { if (value) UseCustomDirectory = false; }
    }

    /// <summary>Directory path box / browse button enabled state.</summary>
    public bool CustomDirEnabled => UseCustomDirectory;

    private void ApplySearchFilter()
    {
        string query = _searchText.Trim();
        foreach (var item in NavItems)
            item.IsVisible = item.Matches(query);

        // Never leave the right pane orphaned: if the selected row got filtered out, fall back
        // to the first visible row (all hidden = keep the current one).
        if (SelectedNav.IsVisible) return;
        var firstVisible = NavItems.FirstOrDefault(n => n.IsVisible);
        if (firstVisible is not null) SelectedNav = firstVisible;
    }

    /// <summary>Buffer for the "add / replace suffix" text box.</summary>
    public string NewSuffixText
    {
        get => _newSuffixText;
        set => SetProperty(ref _newSuffixText, value);
    }

    public int SelectedSuffixIndex
    {
        get => _selectedSuffixIndex;
        set
        {
            if (SetProperty(ref _selectedSuffixIndex, value))
            {
                UpdateSuffixCommand.RaiseCanExecuteChanged();
                DeleteSuffixCommand.RaiseCanExecuteChanged();
                MoveUpCommand.RaiseCanExecuteChanged();
                MoveDownCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Live example of the output file name under the current suffix.</summary>
    public string SuffixPreviewText =>
        $"示例：扫描件.pdf → 扫描件{OutputNameGenerator.SanitizeSuffix(Settings.OutputSuffix)}.pdf";

    /// <summary>The 0-based concurrency choice shown in the combo box.</summary>
    public int ConcurrencyIndex
    {
        get => Math.Clamp(Settings.MaxConcurrency, 1, 4) - 1;
        set
        {
            Settings.MaxConcurrency = Math.Clamp(value + 1, 1, 4);
            OnPropertyChanged();
        }
    }

    /// <summary>The 0-based log range choice: 1 hour / 24 hours / 7 days.</summary>
    public int LogRangeIndex
    {
        get => _logRangeIndex;
        set
        {
            if (SetProperty(ref _logRangeIndex, value))
                RefreshLog();
        }
    }

    public string LogPreviewText
    {
        get => _logPreviewText;
        private set => SetProperty(ref _logPreviewText, value);
    }

    public string LogStatusText
    {
        get => _logStatusText;
        private set => SetProperty(ref _logStatusText, value);
    }

    public string ReportsPreviewText
    {
        get => _reportsPreviewText;
        private set => SetProperty(ref _reportsPreviewText, value);
    }

    public string ReportsStatusText
    {
        get => _reportsStatusText;
        private set => SetProperty(ref _reportsStatusText, value);
    }

    /// <summary>Rows of the OCR engine group: the auto option plus every registered engine.
    /// Filled on first visit to the section (see <see cref="StartOcrProbe"/>).</summary>
    public ObservableCollection<OcrEngineOptionViewModel> OcrOptions { get; } = new();

    /// <summary>Rows of the layout block group: every registered block (the full-image
    /// passthrough is pinned first in the chain registration).</summary>
    public ObservableCollection<OcrEngineOptionViewModel> LayoutOptions { get; } = new();

    private bool _ocrProbeStarted;

    /// <summary>
    /// Builds both pipeline groups (layout block × text engine) from the chain registrations
    /// (names/descriptions need no probing) and kicks off the one-time background availability
    /// probes. Idempotent — called from <see cref="SelectedNav"/> whenever the OCR section is
    /// selected.
    /// </summary>
    public void StartOcrProbe()
    {
        if (_ocrProbeStarted) return;
        _ocrProbeStarted = true;

        static (Func<AppSettings, string> Read, Action<AppSettings, string> Write) EnginePreference() =>
            (s => s.PreferredOcrEngine, (s, v) => s.PreferredOcrEngine = v);
        static (Func<AppSettings, string> Read, Action<AppSettings, string> Write) LayoutPreference() =>
            (s => s.PreferredLayoutStage, (s, v) => s.PreferredLayoutStage = v);

        var engine = EnginePreference();
        var layout = LayoutPreference();

        OcrOptions.Clear();
        OcrOptions.Add(new OcrEngineOptionViewModel(
            Settings, string.Empty, "自动选择（推荐）",
            "按注册优先级自动使用当前可用的最佳引擎；指定的引擎不可用时也回退到这里。",
            engine.Read, engine.Write, "OcrEngineChoice"));
        foreach (var factory in AppOcrEngineChain.Instance.Factories)
            OcrOptions.Add(new OcrEngineOptionViewModel(
                Settings, factory.Name, factory.Name, factory.Description,
                engine.Read, engine.Write, "OcrEngineChoice"));

        LayoutOptions.Clear();
        foreach (var factory in OcrLayoutStageChain.Instance.Factories)
            LayoutOptions.Add(new OcrEngineOptionViewModel(
                Settings, factory.Name, factory.Name, factory.Description,
                layout.Read, layout.Write, "LayoutStageChoice"));

        foreach (var option in OcrOptions) option.AttachGroup(OcrOptions);
        foreach (var option in LayoutOptions) option.AttachGroup(LayoutOptions);

        // A stale preference (renamed / removed entry) must not leave every radio unchecked.
        if (OcrOptions.All(o => !o.IsSelected)) OcrOptions[0].IsSelected = true;
        if (LayoutOptions.All(o => !o.IsSelected)) LayoutOptions[0].IsSelected = true;

        _ = ProbeAvailabilityAsync();
    }

    /// <summary>Probes every registered engine and layout block off the UI thread, then fills in the statuses.</summary>
    private async Task ProbeAvailabilityAsync()
    {
        try
        {
            ProbeLink<IOcrEngine>[] links = await Task.Run(() =>
            {
                AppOcrEngineChain.Instance.EnsureAllProbed();
                OcrLayoutStageChain.Instance.EnsureAllProbed();
                return AppOcrEngineChain.Instance.Links.ToArray();
            });

            foreach (var option in OcrOptions.Where(o => o.Name.Length > 0))
            {
                ProbeLink<IOcrEngine>? link = links.FirstOrDefault(
                    l => string.Equals(l.Name, option.Name, StringComparison.Ordinal));
                option.UpdateAvailability(
                    link?.Available == true,
                    link is null ? "未注册" : link.Reason);
            }

            UpdateLayoutStatuses();
        }
        catch (Exception ex)
        {
            foreach (var option in OcrOptions.Where(o => o.Name.Length > 0))
                option.UpdateAvailability(false, $"检测失败：{ex.Message}");
            foreach (var option in LayoutOptions.Where(o => o.Name.Length > 0))
                option.UpdateAvailability(false, $"检测失败：{ex.Message}");
        }
    }

    /// <summary>Maps the layout chain's probe results onto the radio rows (UI-thread only).</summary>
    private void UpdateLayoutStatuses()
    {
        ProbeLink<ILayoutStage>[] links = OcrLayoutStageChain.Instance.Links.ToArray();
        foreach (var option in LayoutOptions)
        {
            ProbeLink<ILayoutStage>? link = links.FirstOrDefault(
                l => string.Equals(l.Name, option.Name, StringComparison.Ordinal));
            option.UpdateAvailability(
                link?.Available == true,
                link is null ? "未注册" : link.Reason);
        }
    }

    public RelayCommand AddSuffixCommand { get; }

    public RelayCommand UpdateSuffixCommand { get; }

    public RelayCommand DeleteSuffixCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand RefreshLogCommand { get; }

    public RelayCommand CopyLogCommand { get; }

    public RelayCommand ExportLogCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand RefreshReportsCommand { get; }

    public RelayCommand OpenReportsFolderCommand { get; }

    // ── 崩溃报告 ──

    private string _crashReportText = string.Empty;

    /// <summary>One line: how many crash bundles are waiting and how old the newest one is.</summary>
    public string CrashReportText
    {
        get => _crashReportText;
        private set => SetProperty(ref _crashReportText, value);
    }

    public RelayCommand RefreshCrashReportsCommand { get; }

    public RelayCommand OpenCrashFolderCommand { get; }

    /// <summary>Opt-in upload to /WatermarkRemover/private/crashes; never automatic.</summary>
    public RelayCommand UploadCrashReportsCommand { get; }

    private void RefreshCrashReports()
    {
        IReadOnlyList<FileInfo> reports = CrashReporter.Reports();
        CrashReportText = reports.Count == 0
            ? "本机没有崩溃报告。程序出错时会自动生成一个诊断包保存在这里。"
            : $"{reports.Count} 个崩溃报告待处理（最新 {reports[0].LastWriteTime:MM-dd HH:mm}，" +
              $"共 {Math.Max(1, reports.Sum(r => r.Length) / 1024)} KB）";
    }

    private void OpenCrashFolder()
    {
        if (!_shell.TryOpenDirectory(CrashReporter.Directory, out string? error))
            _dialogs.Warn($"打开失败：{error}", "提示");
    }

    private async Task UploadCrashReportsAsync()
    {
        IReadOnlyList<FileInfo> reports = CrashReporter.Reports();
        if (reports.Count == 0)
        {
            _dialogs.Info("没有待上传的崩溃报告。", "崩溃报告");
            return;
        }

        var service = new CloudUpdateService(_target, new DpapiSecretProtector());
        if (!service.IsConfigured)
        {
            _dialogs.Warn("请先在「网盘设置」完成阿里云盘登录，再上传崩溃报告。", "崩溃报告");
            return;
        }

        try
        {
            int uploaded = await service
                .UploadCrashReportsAsync(reports.Select(r => r.FullName).ToArray())
                .ConfigureAwait(true);
            CrashReportText = $"已上传 {uploaded} 个崩溃报告到云盘私有目录。";
            _dialogs.Info(
                $"已上传 {uploaded} 个崩溃报告到 阿里云盘 /WatermarkRemover/private/crashes。\n\n" +
                "在任意一台已登录的电脑上都能看到，便于统一排查。",
                "崩溃报告");
        }
        catch (Exception ex)
        {
            AppLog.Error("上传崩溃报告失败", ex);
            _dialogs.Warn($"上传崩溃报告失败：{ex.Message}", "崩溃报告");
        }
    }

    /// <summary>Notifies bindings that depend on the output suffix (the preview line).</summary>
    public void NotifySuffixChanged() => OnPropertyChanged(nameof(SuffixPreviewText));

    /// <summary>
    /// Copies the edited working copy back onto the shared settings object (the "保存" action).
    /// The caller persists the shared object afterwards.
    /// </summary>
    public void Commit()
    {
        _target.OutputSuffix = Settings.OutputSuffix;
        _target.OutputMode = Settings.OutputMode;
        _target.OutputDirectory = Settings.OutputDirectory;
        _target.AutoNameByContent = Settings.AutoNameByContent;
        _target.NameFromFirstPageOnly = Settings.NameFromFirstPageOnly;
        _target.RemoveLinkedAnnotations = Settings.RemoveLinkedAnnotations;
        _target.MaxConcurrency = Settings.MaxConcurrency;
        _target.CheckUpdatesOnStartup = Settings.CheckUpdatesOnStartup;
        _target.QuickSuffixes = QuickSuffixes.ToList();
        _target.PreferredOcrEngine = Settings.PreferredOcrEngine;
        _target.PreferredLayoutStage = Settings.PreferredLayoutStage;
        _target.ScanColor = Settings.ScanColor;
        _target.ContinuousScan = Settings.ContinuousScan;
        _target.ScanOutputDirectory = Settings.ScanOutputDirectory?.Trim() ?? string.Empty;
        _target.TransferOutputDirectory = Settings.TransferOutputDirectory?.Trim() ?? string.Empty;
        _target.ScreenshotHotkey = Settings.ScreenshotHotkey;
        _target.ScreenshotOutputDirectory = Settings.ScreenshotOutputDirectory?.Trim() ?? string.Empty;
        _target.NetdiskUploadDrive = Settings.NetdiskUploadDrive?.Trim() ?? string.Empty;
        _target.NetdiskUploadDirectory = Settings.NetdiskUploadDirectory?.Trim() ?? string.Empty;
    }

    private void AddSuffix(object? parameter)
    {
        if (!TryReadToken(NewSuffixText, out string token)) return;
        if (QuickSuffixes.Any(s => string.Equals(s, token, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Warn("该后缀已存在。", "提示");
            return;
        }

        QuickSuffixes.Add(token);
        SelectedSuffixIndex = QuickSuffixes.Count - 1;
        NewSuffixText = string.Empty;
    }

    private void UpdateSuffix(object? parameter)
    {
        int index = SelectedSuffixIndex;
        if (index < 0)
        {
            _dialogs.Warn("请先在列表中选中要修改的后缀。", "提示");
            return;
        }

        if (!TryReadToken(NewSuffixText, out string token)) return;
        if (QuickSuffixes.Where((_, i) => i != index).Any(s => string.Equals(s, token, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Warn("该后缀已存在。", "提示");
            return;
        }

        QuickSuffixes[index] = token;
    }

    private void DeleteSuffix(object? parameter)
    {
        int index = SelectedSuffixIndex;
        if (index < 0)
        {
            _dialogs.Warn("请先在列表中选中要删除的后缀。", "提示");
            return;
        }

        QuickSuffixes.RemoveAt(index);
        if (QuickSuffixes.Count > 0)
            SelectedSuffixIndex = Math.Min(index, QuickSuffixes.Count - 1);
    }

    /// <summary>Moves the selected suffix by the given offset and keeps it selected.</summary>
    private void MoveSelected(int offset)
    {
        int index = SelectedSuffixIndex;
        int target = index + offset;
        if (index < 0 || target < 0 || target >= QuickSuffixes.Count) return;

        (QuickSuffixes[index], QuickSuffixes[target]) = (QuickSuffixes[target], QuickSuffixes[index]);
        SelectedSuffixIndex = target;
    }

    /// <summary>Sanitises the typed token; prompts on empty input.</summary>
    private bool TryReadToken(string raw, out string token)
    {
        token = OutputNameEditor.SanitizeSuffixToken(raw);
        if (token.Length != 0) return true;

        _dialogs.Warn("请输入后缀内容。", "提示");
        return false;
    }

    /// <summary>Logs in [now - range, now) as preview text and a status line.</summary>
    public void RefreshLog()
    {
        try
        {
            IReadOnlyList<LogEntry> entries = LogExportService.Collect(SelectedLogRange());
            LogPreviewText = entries.Count == 0
                ? "（该时间段暂无执行记录）"
                : string.Join(Environment.NewLine, entries.Select(LogFilter.Format));
            LogStatusText = $"共 {entries.Count} 条";
        }
        catch (Exception ex)
        {
            LogPreviewText = $"读取日志失败：{ex.Message}";
            LogStatusText = string.Empty;
        }
    }

    private void CopyLog()
    {
        try
        {
            Clipboard.SetText(LogExportService.BuildReport(LogExportService.Collect(SelectedLogRange()), SelectedLogRange()));
            LogStatusText = "完整诊断日志已复制到剪贴板";
        }
        catch (Exception ex)
        {
            _dialogs.Warn($"复制失败：{ex.Message}", "提示");
        }
    }

    /// <summary>Writes the report to <paramref name="path"/>; the caller picks the location.</summary>
    public void ExportLogTo(string path)
    {
        try
        {
            LogExportService.Export(path, LogExportService.BuildReport(LogExportService.Collect(SelectedLogRange()), SelectedLogRange()));
            LogStatusText = $"已导出：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _dialogs.Warn($"导出失败：{ex.Message}", "提示");
        }
    }

    private void ExportLog()
    {
        string suggested = $"WatermarkRemover-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        // The save-file dialog is a view concern; reuse the export core through the event below.
        ExportRequested?.Invoke(this, suggested);
    }

    /// <summary>Raised when the user asks for a log export; the view picks the file name.</summary>
    public event EventHandler<string>? ExportRequested;

    private void OpenLogFolder()
    {
        if (!_shell.TryOpenDirectory(AppFolders.LogDirectory, out string? error))
            _dialogs.Warn($"打开失败：{error}", "提示");
    }

    /// <summary>Most recent recognition reports as preview text and a status line.</summary>
    public void RefreshReports()
    {
        try
        {
            IReadOnlyList<RecognitionReport> reports = _reportStore.ReadRecent(50);
            ReportsPreviewText = reports.Count == 0
                ? "（暂无识别报告：运行一次 OCR 识别或去水印后自动生成）"
                : string.Join(Environment.NewLine, reports.Select(FormatReport));
            ReportsStatusText = $"最近 {reports.Count} 条 · 保留 {RecognitionReportStore.RetentionDays} 天";
        }
        catch (Exception ex)
        {
            ReportsPreviewText = $"读取识别报告失败：{ex.Message}";
            ReportsStatusText = string.Empty;
        }
    }

    private void OpenReportsFolder()
    {
        if (!_shell.TryOpenDirectory(AppFolders.ReportDirectory, out string? error))
            _dialogs.Warn($"打开失败：{error}", "提示");
    }

    /// <summary>One preview line per report: time · file · type(score) · name · fields · issues.</summary>
    private static string FormatReport(RecognitionReport report)
    {
        string classification = report.Classification is { } c
            ? $"{c.TypeShortName}（{c.Score}）"
            : "未分类";
        string issues = report.Issues.Count > 0 ? $" · 问题 {report.Issues.Count}" : string.Empty;
        return $"{report.Timestamp:yyyy-MM-dd HH:mm:ss} · {Path.GetFileName(report.InputPath)}" +
               $" · {classification} · 姓名={report.PersonName ?? "-"}" +
               $" · 字段 {report.Fields.Count}{issues}";
    }

    internal TimeSpan SelectedLogRange() => LogRangeIndex switch
    {
        1 => TimeSpan.FromHours(24),
        2 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1),
    };
}
