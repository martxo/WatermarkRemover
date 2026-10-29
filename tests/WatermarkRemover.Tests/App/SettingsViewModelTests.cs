using WatermarkRemover.App.Ocr;
using WatermarkRemover.Ocr.Layout;
using WatermarkRemover.App.Recognition;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.App;

public class SettingsViewModelTests
{
    private sealed class FakeDialogs : IDialogService
    {
        public List<string> Warnings { get; } = new();

        public bool Confirm(string message, string title) => true;

        public bool ConfirmYesNo(string message, string title) => true;

        public void Info(string message, string title) { }

        public void Warn(string message, string title) => Warnings.Add(message);

        public void Error(string message, string title) { }
    }

    private sealed class FakeShell : IShellOpenService
    {
        public string? OpenedDirectory { get; private set; }

        public bool TryOpenDirectory(string directory, out string? error)
        {
            OpenedDirectory = directory;
            error = null;
            return true;
        }
    }

    private static (SettingsViewModel Vm, FakeDialogs Dialogs, AppSettings Target) NewVm(
        Action<AppSettings>? configure = null)
    {
        var target = new AppSettings { QuickSuffixes = new List<string>() };
        configure?.Invoke(target);
        var dialogs = new FakeDialogs();
        var vm = new SettingsViewModel(target, dialogs, new FakeShell());
        return (vm, dialogs, target);
    }

    [Fact]
    public void AddSuffixAppendsTokenAndSelectsIt()
    {
        var (vm, dialogs, _) = NewVm();

        vm.NewSuffixText = "  结清证明  ";
        vm.AddSuffixCommand.Execute(null);

        Assert.Equal(new[] { "结清证明" }, vm.QuickSuffixes.ToArray());
        Assert.Equal(0, vm.SelectedSuffixIndex);
        Assert.Equal(string.Empty, vm.NewSuffixText);
        Assert.Empty(dialogs.Warnings);
    }

    [Fact]
    public void AddSuffixRejectsDuplicateIgnoringCase()
    {
        var (vm, dialogs, _) = NewVm();
        vm.QuickSuffixes.Add("身份证");

        vm.NewSuffixText = "身份证";
        vm.AddSuffixCommand.Execute(null);

        Assert.Single(vm.QuickSuffixes);
        Assert.Contains("已存在", dialogs.Warnings.Single());
    }

    [Fact]
    public void AddSuffixWarnsOnEmptyInput()
    {
        var (vm, dialogs, _) = NewVm();

        vm.NewSuffixText = "   ";
        vm.AddSuffixCommand.Execute(null);

        Assert.Empty(vm.QuickSuffixes);
        Assert.Contains("请输入后缀内容", dialogs.Warnings.Single());
    }

    [Fact]
    public void UpdateSuffixReplacesSelectedToken()
    {
        var (vm, _, _) = NewVm(s => s.QuickSuffixes = new List<string> { "旧后缀" });
        vm.SelectedSuffixIndex = 0;

        vm.NewSuffixText = "新后缀";
        vm.UpdateSuffixCommand.Execute(null);

        Assert.Equal(new[] { "新后缀" }, vm.QuickSuffixes.ToArray());
    }

    [Fact]
    public void DeleteSuffixRemovesSelectionAndKeepsIndexInRange()
    {
        var (vm, _, _) = NewVm(s => s.QuickSuffixes = new List<string> { "a", "b", "c" });
        vm.SelectedSuffixIndex = 2;

        vm.DeleteSuffixCommand.Execute(null);

        Assert.Equal(new[] { "a", "b" }, vm.QuickSuffixes.ToArray());
        Assert.Equal(1, vm.SelectedSuffixIndex);
    }

    [Fact]
    public void MoveUpAndDownSwapAndFollowSelection()
    {
        var (vm, _, _) = NewVm(s => s.QuickSuffixes = new List<string> { "a", "b" });
        vm.SelectedSuffixIndex = 1;

        vm.MoveUpCommand.Execute(null);
        Assert.Equal(new[] { "b", "a" }, vm.QuickSuffixes.ToArray());
        Assert.Equal(0, vm.SelectedSuffixIndex);
        Assert.False(vm.MoveUpCommand.CanExecute(null));

        vm.MoveDownCommand.Execute(null);
        Assert.Equal(new[] { "a", "b" }, vm.QuickSuffixes.ToArray());
        Assert.Equal(1, vm.SelectedSuffixIndex);
        Assert.False(vm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void CommitCopiesEditsAndSuffixesBackToTarget()
    {
        var (vm, _, target) = NewVm(s => s.QuickSuffixes = new List<string> { "旧" });

        vm.Settings.OutputSuffix = "_clean";
        vm.Settings.OutputMode = OutputMode.CustomDirectory;
        vm.Settings.OutputDirectory = @"D:\out";
        vm.Settings.AutoNameByContent = false;
        vm.Settings.RemoveLinkedAnnotations = false;
        vm.Settings.MaxConcurrency = 4;
        vm.Settings.CheckUpdatesOnStartup = false;
        vm.Settings.ScanColor = 1;
        vm.Settings.ContinuousScan = true;
        vm.Settings.ScanOutputDirectory = @"D:\scans";
        vm.QuickSuffixes.Clear();
        vm.QuickSuffixes.Add("新");
        vm.Commit();

        Assert.Equal("_clean", target.OutputSuffix);
        Assert.Equal(OutputMode.CustomDirectory, target.OutputMode);
        Assert.Equal(@"D:\out", target.OutputDirectory);
        Assert.False(target.AutoNameByContent);
        Assert.False(target.RemoveLinkedAnnotations);
        Assert.Equal(4, target.MaxConcurrency);
        Assert.False(target.CheckUpdatesOnStartup);
        Assert.Equal(1, target.ScanColor);
        Assert.True(target.ContinuousScan); // moved onto the settings page; committed with the rest
        Assert.Equal(@"D:\scans", target.ScanOutputDirectory);
        Assert.Equal(new[] { "新" }, target.QuickSuffixes.ToArray());
    }

    [Fact]
    public void ScanOutputDirectoryCommitTrimsAndEmptyMeansPicturesFallback()
    {
        var (vm, _, target) = NewVm();

        vm.Settings.ScanOutputDirectory = "  D:\\scans  ";
        vm.Commit();
        Assert.Equal(@"D:\scans", target.ScanOutputDirectory);

        vm.Settings.ScanOutputDirectory = "   ";
        vm.Commit();
        Assert.Equal(string.Empty, target.ScanOutputDirectory);
    }

    [Fact]
    public void ScanSectionIsRegisteredAndItsEditsStayInvisibleWithoutCommit()
    {
        var (vm, _, target) = NewVm();

        // The nav row drives the sidebar entry, the page title and the content trigger.
        SettingsNavItemViewModel nav = Assert.Single(vm.NavItems, n => n.Id == "scan");
        int generalIndex = vm.NavItems.ToList().FindIndex(n => n.Id == "general");
        Assert.True(vm.NavItems.ToList().IndexOf(nav) > generalIndex);

        vm.Settings.ScanColor = 0;
        Assert.Equal(new AppSettings().ScanColor, target.ScanColor);

        vm.Commit();
        Assert.Equal(0, target.ScanColor);
    }

    [Fact]
    public void EditsWithoutCommitLeaveTargetUntouched()
    {
        var (vm, _, target) = NewVm();

        vm.Settings.OutputSuffix = "_dirty";
        vm.NewSuffixText = "x";
        vm.AddSuffixCommand.Execute(null);

        Assert.Equal(new AppSettings().OutputSuffix, target.OutputSuffix);
        Assert.Empty(target.QuickSuffixes.Where(s => s == "x"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 24)]
    [InlineData(2, 24 * 7)]
    public void LogRangeIndexSelectsExpectedSpan(int index, int hours)
    {
        var (vm, _, _) = NewVm();

        vm.LogRangeIndex = index;

        Assert.Equal(TimeSpan.FromHours(hours), vm.SelectedLogRange());
    }

    [Fact]
    public void OcrSectionRegistersAutoPlusEveryEngineAndSelectionPersists()
    {
        var (vm, _, target) = NewVm();

        vm.StartOcrProbe();

        // Auto row + one row per engine registered in the app chain (declarative registration).
        Assert.Equal(1 + AppOcrEngineChain.Instance.Factories.Count, vm.OcrOptions.Count);
        Assert.Equal("自动选择（推荐）", vm.OcrOptions[0].Title);
        Assert.Equal(string.Empty, vm.OcrOptions[0].Name);
        Assert.True(vm.OcrOptions[0].IsSelected);

        // Pinning an engine updates the working copy and deselects the auto row (exclusivity
        // lives in the view model, not only in the RadioButton group).
        OcrEngineOptionViewModel pinned = vm.OcrOptions[1];
        pinned.IsSelected = true;
        Assert.False(vm.OcrOptions[0].IsSelected);
        Assert.Equal(pinned.Name, vm.Settings.PreferredOcrEngine);

        vm.Commit();
        Assert.Equal(pinned.Name, target.PreferredOcrEngine);

        // Re-entering the section must not rebuild / re-probe the list.
        var options = vm.OcrOptions;
        vm.StartOcrProbe();
        Assert.Same(options, vm.OcrOptions);
    }

    [Fact]
    public void OcrSelectionIsDiscardedWithoutCommit()
    {
        var (vm, _, target) = NewVm();

        vm.StartOcrProbe();
        vm.OcrOptions[1].IsSelected = true;

        Assert.Equal(string.Empty, target.PreferredOcrEngine);
    }

    [Fact]
    public void LayoutSectionRegistersEveryBlockAndSelectionPersists()
    {
        var (vm, _, target) = NewVm();

        vm.StartOcrProbe();

        // One row per layout block registered in the app chain (declarative registration);
        // the full-image passthrough is pinned first, so an empty preference lands there.
        Assert.Equal(OcrLayoutStageChain.Instance.Factories.Count, vm.LayoutOptions.Count);
        Assert.Equal("整图直通", vm.LayoutOptions[0].Name);
        Assert.True(vm.LayoutOptions[0].IsSelected);

        // Pinning a block updates the working copy, deselects the passthrough row and leaves
        // the engine group untouched — the two radio groups are independent.
        OcrEngineOptionViewModel pinned = vm.LayoutOptions[1];
        pinned.IsSelected = true;
        Assert.False(vm.LayoutOptions[0].IsSelected);
        Assert.Equal(pinned.Name, vm.Settings.PreferredLayoutStage);
        Assert.Equal(string.Empty, vm.Settings.PreferredOcrEngine);
        Assert.True(vm.OcrOptions[0].IsSelected);

        vm.Commit();
        Assert.Equal(pinned.Name, target.PreferredLayoutStage);

        // Re-entering the section must not rebuild / re-probe the list.
        var options = vm.LayoutOptions;
        vm.StartOcrProbe();
        Assert.Same(options, vm.LayoutOptions);
    }

    [Fact]
    public void LayoutSelectionIsDiscardedWithoutCommit()
    {
        var (vm, _, target) = NewVm();

        vm.StartOcrProbe();
        vm.LayoutOptions[1].IsSelected = true;

        Assert.Equal(string.Empty, target.PreferredLayoutStage);
    }

    [Fact]
    public void LayoutAndEnginePreferencesCommitTogetherAndRebindIndependently()
    {
        // The whole point of the brick pipeline: both halves are chosen in one session and both
        // must survive the round trip — neither preference may overwrite the other.
        var (vm, _, target) = NewVm();

        vm.StartOcrProbe();
        vm.LayoutOptions[1].IsSelected = true; // first real layout block after the passthrough
        vm.OcrOptions[2].IsSelected = true;    // a pinned text engine
        vm.Commit();

        Assert.Equal(vm.LayoutOptions[1].Name, target.PreferredLayoutStage);
        Assert.Equal(vm.OcrOptions[2].Name, target.PreferredOcrEngine);

        // A fresh session restores exactly one selected row per group from the saved values.
        var (reopened, _, _) = NewVm(s =>
        {
            s.PreferredLayoutStage = target.PreferredLayoutStage;
            s.PreferredOcrEngine = target.PreferredOcrEngine;
        });
        reopened.StartOcrProbe();

        Assert.True(reopened.LayoutOptions.Single(o => o.Name == target.PreferredLayoutStage).IsSelected);
        Assert.True(reopened.OcrOptions.Single(o => o.Name == target.PreferredOcrEngine).IsSelected);
        Assert.Equal(1, reopened.LayoutOptions.Count(o => o.IsSelected));
        Assert.Equal(1, reopened.OcrOptions.Count(o => o.IsSelected));
    }

    /// <summary>In-memory report store for the settings preview.</summary>
    private sealed class FakeReportStore : IRecognitionReportStore
    {
        public List<RecognitionReport> Reports { get; } = new();

        public string Append(RecognitionReport report) => string.Empty;

        public IReadOnlyList<RecognitionReport> ReadRecent(int maxLines) =>
            Reports.Take(maxLines).ToList();
    }

    private static SettingsViewModel NewVmWithStore(IRecognitionReportStore store)
    {
        var target = new AppSettings { QuickSuffixes = new List<string>() };
        return new SettingsViewModel(target, new FakeDialogs(), new FakeShell(), store);
    }

    [Fact]
    public void ReportsSectionRegistersAndPreviewReadsFromStore()
    {
        var store = new FakeReportStore();
        store.Reports.Add(new RecognitionReport
        {
            Timestamp = new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(8)),
            InputPath = @"D:\docs\扫描件.pdf",
            Classification = new ClassificationReport("business-license", "执照", 12, 2),
            PersonName = "林孝明",
            Fields = new[] { new FieldObservation("经营者", null, "林孝明", null) },
            Issues = new[] { new ReviewIssue("anchor_split", ReviewSeverity.Warning, "分割", "证据") },
        });
        SettingsViewModel vm = NewVmWithStore(store);

        // Declarative registration, same as every other settings section.
        SettingsNavItemViewModel nav = Assert.Single(vm.NavItems, n => n.Id == "reports");
        Assert.Equal("识别报告", nav.Title);

        vm.SelectedNav = nav; // entering the section reads the store back

        Assert.Contains("扫描件.pdf", vm.ReportsPreviewText);
        Assert.Contains("执照（12）", vm.ReportsPreviewText);
        Assert.Contains("姓名=林孝明", vm.ReportsPreviewText);
        Assert.Contains("字段 1", vm.ReportsPreviewText);
        Assert.Contains("问题 1", vm.ReportsPreviewText);
        Assert.Contains("最近 1 条", vm.ReportsStatusText);
        Assert.Contains("90 天", vm.ReportsStatusText);
    }

    [Fact]
    public void ReportsPreviewShowsEmptyHintAndRefreshIsRepeatable()
    {
        SettingsViewModel vm = NewVmWithStore(new FakeReportStore());
        SettingsNavItemViewModel nav = vm.NavItems.Single(n => n.Id == "reports");

        vm.SelectedNav = nav;
        Assert.Contains("暂无识别报告", vm.ReportsPreviewText);
        Assert.Contains("最近 0 条", vm.ReportsStatusText);

        vm.RefreshReportsCommand.Execute(null);
        Assert.Contains("暂无识别报告", vm.ReportsPreviewText);
        Assert.Contains("最近 0 条", vm.ReportsStatusText);
    }
}
