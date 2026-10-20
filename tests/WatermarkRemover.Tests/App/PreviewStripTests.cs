using System.ComponentModel;
using System.Windows;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Preview;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Two-step PDF preview: the preview button unfolds a thumbnail grid under the job row, and a
/// tile click opens the screen-level image viewer (dark veil, page in the middle, film rail on
/// the left). The row and the viewer share one view model instance, so these tests pin down the
/// sharing (one grid per row), the toggle wording, and the viewer request leaving the row
/// untouched.
/// </summary>
public class PreviewStripTests
{
    private sealed class FakeDialogs : IDialogService
    {
        public List<string> Infos { get; } = new();

        public bool Confirm(string message, string title) => true;

        public bool ConfirmYesNo(string message, string title) => true;

        public void Info(string message, string title) => Infos.Add(message);

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

    /// <summary>Never renders: tests here only need a view model to exist and be attachable.</summary>
    private sealed class NullRenderer : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(0);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            Task.FromResult<IReadOnlyList<byte[]>>(Array.Empty<byte[]>());

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            Task.FromResult(Array.Empty<byte>());
    }

    /// <summary>Builds a finished row whose output is a real PDF on disk (the strip needs one).</summary>
    private static (MainViewModel Vm, FileJobViewModel Job, string PdfPath) NewRow(FakeDialogs dialogs)
    {
        string pdf = Path.Combine(Path.GetTempPath(), "wm-strip-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(pdf, PdfFixture.CamScannerLike().Build());

        var vm = new MainViewModel(dialogs, new FakeShell());
        var job = new FileJobViewModel(pdf + ".png", Array.Empty<string>());
        job.Apply(FeatureResult.Success(pdf + ".png", pdf, "已处理", 1));
        return (vm, job, pdf);
    }

    /// <summary>LoadAsync runs fire-and-forget behind OpenPreviewStrip; drain it so the shared
    /// PDFium renderer releases the temp file before the finally deletes it. The first few
    /// rounds wait unconditionally — IsLoading is still false during LoadAsync's startup beat,
    /// which is exactly the race that made this flaky before. IsRotating covers the scan row's
    /// blank-page cleanup, which rewrites the file after the tiles are already filled — a
    /// delete during it used to be masked by how long the old slow tile pipeline took.</summary>
    private static async Task DrainStripAsync(PdfPreviewViewModel strip)
    {
        for (int i = 0; i < 300 && (i < 5 || strip.IsLoading || strip.IsRotating || strip.Thumbnails.Any(t => t.Image is null)); i++)
            await Task.Delay(10);

        // Scan rows also auto-orient after the tiles settle — fire-and-forget. The pass got
        // fast enough (90dpi probes, early vertical verdict) to finish BEFORE this drain
        // looks, so IsOrienting can be false again while the renderer's file handle is still
        // winding down. DeleteWhenReleased absorbs that transient window.
        if (strip.AutoOrientCommand.CanExecute(null))
        {
            for (int i = 0; i < 300 && !strip.IsOrienting; i++)
                await Task.Delay(10);
            for (int i = 0; i < 300 && strip.IsOrienting; i++)
                await Task.Delay(10);
        }
    }

    /// <summary>Deletes a temp strip file, riding out the brief window where the strip's
    /// background passes (orient probe, blank clean, PDFium mapping teardown) still hold it.</summary>
    private static void DeleteWhenReleased(string pdf)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                File.Delete(pdf);
                return;
            }
            catch (IOException) when (i < 300)
            {
                Thread.Sleep(10);
            }
        }
    }

    [Fact]
    public async Task Preview_button_unfolds_the_row_strip_and_folds_it_again()
    {
        var dialogs = new FakeDialogs();
        var (vm, job, pdf) = NewRow(dialogs);
        try
        {
            vm.PreviewCommand.Execute(job);

            Assert.NotNull(job.Strip);
            Assert.True(job.StripOpen);
            Assert.Equal("收起", job.PreviewToggleText);
            Assert.Empty(dialogs.Infos);

            vm.PreviewCommand.Execute(job);

            Assert.False(job.StripOpen);
            Assert.Equal("预览", job.PreviewToggleText);
            Assert.NotNull(job.Strip); // folded, not destroyed
        }
        finally
        {
            if (job.Strip is { } strip) await DrainStripAsync(strip);
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task Scan_rows_unfold_without_a_click_and_hide_the_preview_button()
    {
        var (vm, job, pdf) = NewRow(new FakeDialogs());
        try
        {
            job.OwnerFeatureId = "scan.page";

            vm.OpenPreviewStrip(job);

            Assert.NotNull(job.Strip);
            Assert.True(job.StripOpen);
            Assert.Equal(Visibility.Collapsed, job.PreviewButtonVisibility);
        }
        finally
        {
            if (job.Strip is { } strip) await DrainStripAsync(strip);
            DeleteWhenReleased(pdf);
        }
    }

    [Fact]
    public void Attaching_a_strip_flips_the_toggle_wording_and_notifies_it()
    {
        var dialogs = new FakeDialogs();
        var (_, job, pdf) = NewRow(dialogs);
        try
        {
            string? notified = null;
            ((INotifyPropertyChanged)job).PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FileJobViewModel.PreviewToggleText))
                    notified = e.PropertyName;
            };

            job.AttachStrip(new PdfPreviewViewModel(job.OutputPath!, Path.GetFileName(job.OutputPath!), new NullRenderer()));

            Assert.Equal(nameof(FileJobViewModel.PreviewToggleText), notified);
            Assert.Equal("收起", job.PreviewToggleText);
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    [Fact]
    public async Task Clicking_a_tile_opens_the_screen_level_viewer_on_the_shared_strip()
    {
        var (vm, job, pdf) = NewRow(new FakeDialogs());
        var opened = new List<PdfPreviewViewModel>();
        vm.PreviewViewerOpener = strip => opened.Add(strip);
        try
        {
            vm.PreviewCommand.Execute(job);
            var strip = job.Strip!;

            // The button's LoadAsync runs fire-and-forget; wait for its tiles instead of
            // re-entering LoadAsync a second time.
            for (int i = 0; i < 300 && strip.Thumbnails.Count == 0; i++)
                await Task.Delay(10);
            Assert.Single(strip.Thumbnails);

            // Every render (grid tile) must finish before the temp file is deleted — the shared
            // PDFium renderer keeps the file open while a render is in flight.
            await DrainStripAsync(strip);

            // A tile click asks the host for the screen-level viewer, on this very strip (the
            // image-browser interaction: dark veil, page in the middle, rail on the left).
            strip.Thumbnails[0].SelectCommand.Execute(null);
            Assert.Single(opened);
            Assert.Same(strip, opened[0]);

            // The viewer window closing merely unbinds — the row keeps its strip and stays
            // expanded, exactly like a folded-then-reopened browser grid.
            Assert.Same(strip, job.Strip);
            Assert.True(job.StripOpen);
        }
        finally
        {
            if (job.Strip is { } strip) await DrainStripAsync(strip);
            File.Delete(pdf);
        }
    }

    [Fact]
    public void A_row_without_an_output_pdf_stays_folded_and_prompts()
    {
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(dialogs, new FakeShell());
        var job = new FileJobViewModel(@"C:\missing\input.png", Array.Empty<string>());

        vm.PreviewCommand.Execute(job);

        Assert.Null(job.Strip);
        Assert.False(job.StripOpen);
        Assert.Single(dialogs.Infos);
    }
}
