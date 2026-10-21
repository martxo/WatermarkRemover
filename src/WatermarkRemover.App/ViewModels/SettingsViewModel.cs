using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Layout;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Recognition;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Settings;

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
            new("scan", "扫描设置", "\uE7F4", "扫描 色彩 黑白 彩色 灰度 自动 扫描仪"),
            new("netdisk", "网盘设置", "\uE753", "网盘 云盘 阿里云盘 授权 令牌 密钥 账号 上传"),
            new("ocr", "OCR 识别", "\uE7C3", "识别 引擎 微信 OCR PP-OCR Windows 文字"),
            new("updates", "软件更新", "\uE72C", "升级 版本 检查更新 startup"),
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
        RefreshReportsCommand = new RelayCommand(_ => RefreshReports());
        OpenReportsFolderCommand = new RelayCommand(_ => OpenReportsFolder());
    }

    /// <summary>The working copy the window binds to; untouched on cancel.</summary>
    public AppSettings Settings { get; }

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
        foreach (var factory in AppLayoutStageChain.Instance.Factories)
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
            OcrEngineLink[] links = await Task.Run(() =>
            {
                AppOcrEngineChain.Instance.EnsureAllProbed();
                AppLayoutStageChain.Instance.EnsureAllProbed();
                return AppOcrEngineChain.Instance.Links.ToArray();
            });

            foreach (var option in OcrOptions.Where(o => o.Name.Length > 0))
            {
                OcrEngineLink? link = links.FirstOrDefault(
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
        LayoutStageLink[] links = AppLayoutStageChain.Instance.Links.ToArray();
        foreach (var option in LayoutOptions)
        {
            LayoutStageLink? link = links.FirstOrDefault(
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
        _target.ScanOutputDirectory = Settings.ScanOutputDirectory?.Trim() ?? string.Empty;
        _target.TransferOutputDirectory = Settings.TransferOutputDirectory?.Trim() ?? string.Empty;
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
