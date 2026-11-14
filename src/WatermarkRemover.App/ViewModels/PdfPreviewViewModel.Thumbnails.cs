﻿using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.App.ViewModels;

public sealed partial class PdfPreviewViewModel
{
    /// <summary>Builds one tile per page and marks the first page as the one being shown.
    /// Seed bitmaps come from the scan pipeline's pre-decision rasters, so they never carry the
    /// rotation the PDF was built with: a page baked as /Rotate 90/180/270 would show its raw
    /// orientation here while the canvas shows the corrected view — the exact "缩略图还是竖的、
    /// 打开是横的" confusion. Each seed is turned by its page's baked quarter turns so tile and
    /// canvas agree; if a later edit rotates the file, the rebuild renders tiles from the PDF.</summary>
    private void BuildThumbnails()
    {
        Thumbnails.Clear();
        IReadOnlyList<ImageSource>? seeds = _seedThumbnails;
        _seedThumbnails = null;
        for (int i = 0; i < _pageCount; i++)
        {
            var thumb = new PdfPreviewThumbnailViewModel(this, i);
            if (seeds is not null && i < seeds.Count)
            {
                thumb.SetImage(ApplyBakedTurn(seeds[i], i));
            }
            Thumbnails.Add(thumb);
        }
        SyncThumbnailSelection();
        RebuildStripItems();
    }

    /// <summary>Rotates a seed by the quarter turns the scan already baked into that page.
    /// 90°/270° swaps the bitmap's width and height, which the strip's layout follows.</summary>
    private ImageSource ApplyBakedTurn(ImageSource seed, int pageIndex)
    {
        int turns = pageIndex < _pageTurns.Count ? ((_pageTurns[pageIndex] % 4) + 4) % 4 : 0;
        if (turns == 0 || seed is not BitmapSource bitmap) return seed;

        var rotated = new TransformedBitmap(bitmap, new RotateTransform(turns * 90));
        if (rotated.CanFreeze) rotated.Freeze(); // 跨线程可读（填充/解码线程都碰得到瓦片）
        return rotated;
    }

    /// <summary>Rebuilds the display sequence: one atomic unit per page — [gap | tile]. Unit i
    /// opens with the gap after page i-1 (-1 = in front of page one), so every slot between
    /// pages still takes a scan while the wrap panel can only break between units — a wrapped
    /// row never starts with a bare tile, and the columns line up across rows. Every page unit
    /// keeps the same shape (gap + tile) on purpose: a wider last unit is what pushed the last
    /// page to its own row and left a hole; see <see cref="PdfPreviewStripUnitViewModel"/>.
    /// The trailing "append after the last page" slot closes the strip as its own item: adding
    /// it to the last unit would widen that unit by a gap and reintroduce the same hole.</summary>
    private void RebuildStripItems()
    {
        StripItems.Clear();
        for (int i = 0; i < Thumbnails.Count; i++)
        {
            string gapToolTip = i == 0
                ? "点击扫描，把新页插到第一页前面"
                : "点击扫描，把新页插到这两页之间";
            StripItems.Add(new PdfPreviewStripUnitViewModel(
                new PdfPreviewGapViewModel(i - 1, gapToolTip), Thumbnails[i]));
        }

        if (Thumbnails.Count == 0) return;
        StripItems.Add(new PdfPreviewGapViewModel(
            Thumbnails.Count - 1, "点击扫描，把新页追加到最后一页之后"));
    }

    /// <summary>Exactly one tile carries the accent ring: the page the canvas is showing.</summary>
    private void SyncThumbnailSelection()
    {
        foreach (var thumb in Thumbnails)
            thumb.IsSelected = thumb.Index == _pageIndex;
    }

    /// <summary>Fills the strip tile by tile, off the UI path, without ever touching StatusText.</summary>
    private void StartThumbnailLoad() =>
        _ = LoadThumbnailsAsync(Thumbnails.ToList(), ++_thumbnailLoadGeneration);

    /// <summary>Fills the strip while marking the file as "being read by the preview": every tile
    /// render opens the PDF for a moment, and a rename (File.Move) landing inside that window is
    /// rejected by Windows — the 2026-11-02 scan read the name correctly and still kept the
    /// timestamp name because the move hit a thumbnail render. The counter covers overlapping
    /// fills (initial load + insert/delete rebuilds).</summary>
    private async Task LoadThumbnailsAsync(
        IReadOnlyList<PdfPreviewThumbnailViewModel> thumbs,
        int generation)
    {
        _thumbnailFillCount++;
        try
        {
            await FillThumbnailsAsync(thumbs, generation).ConfigureAwait(false);
        }
        finally
        {
            _thumbnailFillCount--;
        }
    }

    private async Task FillThumbnailsAsync(
        IReadOnlyList<PdfPreviewThumbnailViewModel> thumbs,
        int generation)
    {
        using var op = AppLog.BeginOperation(
            LogCategory.Preview, "缩略图填充", $"{thumbs.Count} 页", slowMs: 0);

        int filled = 0;
        foreach (var thumb in thumbs)
        {
            // A tile that already has pixels is current by construction (page edits either shift
            // its index or rebuild the tile); decoding it again would only flicker the strip.
            if (thumb.Image is not null) continue;

            // Re-supplied tiles after a reload belong to a superseded session: drop out.
            if (IsCancelled || generation != _thumbnailLoadGeneration || !Thumbnails.Contains(thumb))
            {
                op.Complete($"已终止，成功填充 {filled}/{thumbs.Count} 张");
                return;
            }

            try
            {
                byte[] bytes = await _cache.GetPageBytesAsync(thumb.Index, ThumbnailDpi);
                if (IsCancelled || generation != _thumbnailLoadGeneration || !Thumbnails.Contains(thumb))
                {
                    op.Complete($"已终止，成功填充 {filled}/{thumbs.Count} 张");
                    return;
                }
                ImageSource image = await Task.Run(() => MakeThumbSource(bytes));
                // Re-check after the decode: a fold/accordion switch during it must win.
                if (IsCancelled || generation != _thumbnailLoadGeneration || !Thumbnails.Contains(thumb))
                {
                    op.Complete($"已终止，成功填充 {filled}/{thumbs.Count} 张");
                    return;
                }
                thumb.SetImage(image);
                filled++;

                // Viewer open: the rail's clicks are the interactive path, so the fill yields
                // between tiles instead of holding the pdfium channel back-to-back.
                if (_viewerOpen) await Task.Delay(200, _cancellationToken);
            }
            catch (OperationCanceledException)
            {
                op.Complete($"已取消，成功填充 {filled}/{thumbs.Count} 张");
                return;
            }
            catch (Exception ex)
            {
                // The main canvas reports the real reason; an empty tile is enough here, but the
                // strip failure still leaves a trace.
                AppLog.Debug($"缩略图填充失败：第 {thumb.Index + 1} 页（{ex.Message}）");
            }
        }
        op.Complete($"成功填充 {filled}/{thumbs.Count} 张");
    }
}
