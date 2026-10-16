using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Logging;
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
    private int _selectedSuffixIndex = -1;
    private string _newSuffixText = string.Empty;
    private int _logRangeIndex;
    private string _logPreviewText = string.Empty;
    private string _logStatusText = string.Empty;

    public SettingsViewModel(AppSettings settings)
        : this(settings, DialogService.Default, ShellOpenService.Default)
    {
    }

    internal SettingsViewModel(AppSettings settings, IDialogService dialogs, IShellOpenService shell)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _target = settings;
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        // Edits land on a working copy so "取消" discards them; Commit copies them back.
        Settings = settings.Clone();

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
    }

    /// <summary>The working copy the window binds to; untouched on cancel.</summary>
    public AppSettings Settings { get; }

    public ObservableCollection<string> QuickSuffixes { get; }

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

    public RelayCommand AddSuffixCommand { get; }

    public RelayCommand UpdateSuffixCommand { get; }

    public RelayCommand DeleteSuffixCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand RefreshLogCommand { get; }

    public RelayCommand CopyLogCommand { get; }

    public RelayCommand ExportLogCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

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
        _target.RemoveLinkedAnnotations = Settings.RemoveLinkedAnnotations;
        _target.MaxConcurrency = Settings.MaxConcurrency;
        _target.CheckUpdatesOnStartup = Settings.CheckUpdatesOnStartup;
        _target.QuickSuffixes = QuickSuffixes.ToList();
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

    internal TimeSpan SelectedLogRange() => LogRangeIndex switch
    {
        1 => TimeSpan.FromHours(24),
        2 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1),
    };
}
