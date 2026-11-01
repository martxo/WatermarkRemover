using System.IO;
using WatermarkRemover.App.Services;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The unfinished-task file is what makes 「上次没跑完」 survive a restart, so its round trip and
/// its removal-on-empty behaviour are the contract the startup banner depends on.
/// </summary>
public sealed class PendingTaskJournalTests
{
    [Fact]
    public void Round_trips_entries_and_deletes_the_file_when_empty()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "wm-pending-" + Guid.NewGuid().ToString("N"));

        try
        {
            var journal = new PendingTaskJournal(directory);
            Assert.Empty(journal.Load());

            var queued = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Local);
            Assert.True(journal.Save(new[]
            {
                new PendingTaskEntry("watermark.page", @"C:\docs\a.pdf", queued),
                new PendingTaskEntry("ocr.page", @"C:\docs\b.pdf", queued),
            }));

            var loaded = journal.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("watermark.page", loaded[0].SectionId);
            Assert.Equal(@"C:\docs\a.pdf", loaded[0].InputPath);

            Assert.True(journal.Save(Array.Empty<PendingTaskEntry>()));
            Assert.False(File.Exists(journal.FilePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_broken_file_reads_back_as_empty()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "wm-pending-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "pending.json"), "{ not json");
            var journal = new PendingTaskJournal(directory);
            Assert.Empty(journal.Load());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
