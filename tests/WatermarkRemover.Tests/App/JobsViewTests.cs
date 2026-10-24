using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Sections keep their own history: the job store is shared, but the view (and the status
/// counts, and 清除) only ever see records that belong to the section you are on.
/// </summary>
public class JobsViewTests
{
    private sealed class FakeDialogs : IDialogService
    {
        /// <summary>What every confirmation answers; a test can flip it to deny.</summary>
        public bool ConfirmResult { get; set; } = true;

        public bool Confirm(string message, string title) => ConfirmResult;

        public bool ConfirmYesNo(string message, string title) => ConfirmResult;

        public void Info(string message, string title)
        {
        }

        public void Warn(string message, string title)
        {
        }

        public void Error(string message, string title)
        {
        }
    }

    private sealed class FakeShell : IShellOpenService
    {
        public bool TryOpenDirectory(string directory, out string? error)
        {
            error = null;
            return true;
        }
    }

    private static MainViewModel NewVm(FakeDialogs? dialogs = null) =>
        new(dialogs ?? new FakeDialogs(), new FakeShell());

    private static FileJobViewModel Job(string path, string owner) =>
        new(path, Array.Empty<string>()) { OwnerFeatureId = owner };

    [Fact]
    public void Each_section_lists_only_its_own_records()
    {
        var vm = NewVm();
        vm.Board.Jobs.Add(Job(@"C:\t\scan1.pdf", "scan.page"));
        vm.Board.Jobs.Add(Job(@"C:\t\wm1.pdf", "watermark.badge"));
        vm.Board.Jobs.Add(Job(@"C:\t\ocr1.pdf", "ocr.text"));

        // The app opens on the scan page: exactly one record there, none of the others leak in.
        Assert.Equal(new[] { "scan1.pdf" }, vm.Board.JobsView.Cast<FileJobViewModel>().Select(j => j.FileName));
        Assert.Equal(1, vm.Board.VisibleJobCount);

        vm.SelectedNavItem = vm.NavItems.First(n => n.Feature.Id == "watermark.badge");
        Assert.Equal(new[] { "wm1.pdf" }, vm.Board.JobsView.Cast<FileJobViewModel>().Select(j => j.FileName));
        Assert.Equal(1, vm.Board.VisibleJobCount);

        vm.SelectedNavItem = vm.NavItems.First(n => n.Feature.Id == "ocr.text");
        Assert.Equal(new[] { "ocr1.pdf" }, vm.Board.JobsView.Cast<FileJobViewModel>().Select(j => j.FileName));
    }

    [Fact]
    public void Clear_removes_only_the_current_sections_records()
    {
        var vm = NewVm();
        vm.Board.Jobs.Add(Job(@"C:\t\scan1.pdf", "scan.page"));
        vm.Board.Jobs.Add(Job(@"C:\t\wm1.pdf", "watermark.badge"));

        vm.Board.ClearCommand.Execute(null);

        Assert.Equal(1, vm.Board.Jobs.Count);
        Assert.Equal("watermark.badge", vm.Board.Jobs[0].OwnerFeatureId);
    }

    [Fact]
    public void Clear_keeps_the_records_when_the_confirmation_is_declined()
    {
        var vm = NewVm(new FakeDialogs { ConfirmResult = false });
        vm.Board.Jobs.Add(Job(@"C:\t\scan1.pdf", "scan.page"));

        vm.Board.ClearCommand.Execute(null);

        Assert.Equal(1, vm.Board.Jobs.Count);
    }

    [Fact]
    public void Clear_is_disabled_when_the_current_section_has_no_records()
    {
        var vm = NewVm();
        vm.Board.Jobs.Add(Job(@"C:\t\wm1.pdf", "watermark.badge"));

        // Opened on the scan page: the scan section is empty, so its 清除 stays off.
        Assert.False(vm.Board.ClearCommand.CanExecute(null));

        vm.SelectedNavItem = vm.NavItems.First(n => n.Feature.Id == "watermark.badge");
        Assert.True(vm.Board.ClearCommand.CanExecute(null));
    }
}
