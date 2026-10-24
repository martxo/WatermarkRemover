using System.IO;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The job list is memory-only, so 「今天扫了哪些、传没传」 used to die with the process. The
/// journal keeps the newest records on disk and the board reads them back at startup; these
/// tests cover the round trip, the ceiling, a corrupt file, and the row upload chip.
/// </summary>
public class TaskJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wm-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool Confirm(string message, string title) => true;

        public bool ConfirmYesNo(string message, string title) => true;

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

    private TaskJournal NewJournal() => new(Path.Combine(_dir, "journal"));

    private string NewPdf(string name)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    private static TaskJournalEntry Entry(string outputPath, DateTime processedAt, string upload = "None") =>
        new("scan.page", outputPath, outputPath, "扫描 1 页", 1, processedAt, upload, null);

    [Fact]
    public void Save_then_load_round_trips_newest_first()
    {
        var journal = NewJournal();
        string older = NewPdf("older.pdf");
        string newer = NewPdf("newer.pdf");
        DateTime now = DateTime.Now;

        Assert.True(journal.Save(new[]
        {
            Entry(older, now.AddMinutes(-10)),
            Entry(newer, now),
        }));

        var loaded = journal.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(newer, loaded[0].OutputPath);
        Assert.Equal(older, loaded[1].OutputPath);
    }

    [Fact]
    public void Save_keeps_only_the_newest_entries()
    {
        var journal = NewJournal();
        DateTime start = DateTime.Now.AddHours(-1);
        var entries = Enumerable.Range(0, TaskJournal.MaxEntries + 5)
            .Select(i => Entry(NewPdf($"scan-{i}.pdf"), start.AddSeconds(i)))
            .ToList();

        journal.Save(entries);
        var loaded = journal.Load();

        Assert.Equal(TaskJournal.MaxEntries, loaded.Count);
        // Newest first: the tail of the input, never the oldest rows.
        Assert.Equal(entries[^1].OutputPath, loaded[0].OutputPath);
    }

    [Fact]
    public void A_corrupt_journal_is_ignored_and_the_next_save_recovers_it()
    {
        var journal = NewJournal();
        Directory.CreateDirectory(journal.DirectoryPath);
        File.WriteAllText(journal.FilePath, "{ not json");

        Assert.Empty(journal.Load());

        string pdf = NewPdf("scan.pdf");
        Assert.True(journal.Save(new[] { Entry(pdf, DateTime.Now) }));
        Assert.Single(journal.Load());
    }

    [Fact]
    public void Upload_state_drives_the_row_chip_and_button()
    {
        var job = new FileJobViewModel(@"C:\t\a.pdf", Array.Empty<string>());
        Assert.False(job.ShowUploadButton);

        job.Apply(FeatureResult.Success("in", "out", "已处理", 1));
        Assert.True(job.ShowUploadButton);
        Assert.False(job.ShowUploadChip);

        job.SetUploadState(UploadState.Uploading);
        Assert.False(job.ShowUploadButton); // no second parallel transfer
        Assert.True(job.ShowUploadChip);
        Assert.Equal("上传中…", job.UploadChipText);

        job.SetUploadState(UploadState.Uploaded, "已上传到「账号」");
        Assert.False(job.ShowUploadButton);
        Assert.Equal("已上传", job.UploadChipText);
        Assert.Equal("已上传到「账号」", job.UploadTip);

        job.SetUploadState(UploadState.Failed, "网络错误");
        Assert.True(job.ShowUploadButton); // retry stays available
        Assert.Equal("上传失败", job.UploadChipText);
        Assert.Equal("网络错误", job.UploadTip);
    }

    [Fact]
    public void A_restart_restores_finished_records_with_their_upload_state()
    {
        string pdf = NewPdf("scan-2026.pdf");
        var journal = NewJournal();
        journal.Save(new[]
        {
            new TaskJournalEntry("scan.page", pdf, pdf, "扫描 2 页", 2,
                DateTime.Now.AddMinutes(-5), "Uploaded", "已上传到「账号」"),
        });

        var vm = new MainViewModel(new FakeDialogs(), new FakeShell(), journal);

        var job = Assert.Single(vm.Board.Jobs);
        Assert.Equal(pdf, job.OutputPath);
        Assert.StartsWith("扫描 2 页", job.Detail);
        Assert.Equal(UploadState.Uploaded, job.Upload);
        Assert.True(job.ShowUploadChip);
        // The footer must not claim zero finished files over a full list.
        Assert.Equal(1, vm.Board.CompletedCount);
    }

    [Fact]
    public void Restored_records_whose_file_is_gone_are_dropped()
    {
        var journal = NewJournal();
        journal.Save(new[]
        {
            Entry(Path.Combine(_dir, "deleted.pdf"), DateTime.Now),
        });

        var vm = new MainViewModel(new FakeDialogs(), new FakeShell(), journal);

        Assert.Empty(vm.Board.Jobs);
    }
}
