using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
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
        Assert.Equal(new[] { "新" }, target.QuickSuffixes.ToArray());
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
}
