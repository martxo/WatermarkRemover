using WatermarkRemover.App.Services;
using WatermarkRemover.App.Updates;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The background download announces itself through one shared notifier; the shell's update icon
/// binds to it and its command only appears (and only fires) once a verified package is waiting.
/// </summary>
public class UpdateReadyNotifierTests
{
    private sealed class RecordingDialogs : IDialogService
    {
        public int ConfirmCalls { get; private set; }

        public bool ConfirmResult { get; set; } = true;

        public bool Confirm(string message, string title) => ConfirmResult;

        public bool ConfirmYesNo(string message, string title)
        {
            ConfirmCalls++;
            return ConfirmResult;
        }

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

    [Fact]
    public void Publishing_an_update_fills_the_icon_state_and_clearing_empties_it()
    {
        var notifier = UpdateReadyNotifier.Current;
        var changes = new List<string?>();
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            changes.Add(e.PropertyName);
        notifier.PropertyChanged += OnChanged;
        try
        {
            notifier.Publish("9.9.9", () => { });

            Assert.True(notifier.HasUpdate);
            Assert.Equal("9.9.9", notifier.Version);
            Assert.Contains("9.9.9", notifier.Hint);
            Assert.NotNull(notifier.Apply);
            Assert.Contains(nameof(UpdateReadyNotifier.HasUpdate), changes);

            notifier.Clear();

            Assert.False(notifier.HasUpdate);
            Assert.Null(notifier.Apply);
            Assert.Equal(string.Empty, notifier.Hint);
        }
        finally
        {
            notifier.PropertyChanged -= OnChanged;
            notifier.Clear();
        }
    }

    [Fact]
    public void The_icon_command_tracks_the_state_and_applies_the_downloaded_update()
    {
        var notifier = UpdateReadyNotifier.Current;
        var dialogs = new RecordingDialogs();
        var vm = new MainViewModel(dialogs, new FakeShell());
        int applied = 0;
        try
        {
            Assert.False(vm.InstallUpdateCommand.CanExecute(null));

            notifier.Publish("9.9.9", () => applied++);

            Assert.True(vm.InstallUpdateCommand.CanExecute(null));
            vm.InstallUpdateCommand.Execute(null);

            Assert.Equal(1, dialogs.ConfirmCalls);
            Assert.Equal(1, applied);
        }
        finally
        {
            notifier.Clear();
        }
    }

    [Fact]
    public void Declining_the_restart_confirmation_keeps_the_current_version_running()
    {
        var notifier = UpdateReadyNotifier.Current;
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var vm = new MainViewModel(dialogs, new FakeShell());
        int applied = 0;
        try
        {
            notifier.Publish("9.9.9", () => applied++);

            vm.InstallUpdateCommand.Execute(null);

            Assert.Equal(1, dialogs.ConfirmCalls);
            Assert.Equal(0, applied);
            Assert.True(vm.InstallUpdateCommand.CanExecute(null)); // still waiting
        }
        finally
        {
            notifier.Clear();
        }
    }
}
