using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Imaging.Screenshot;
using WatermarkRemover.Pdf.Annotation;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// PDF 预览页的标注编排（阶段 2）：进入/退出编辑态、按页会话、三档保存。标注逻辑全在
/// <see cref="AnnotationEditSession"/> 与 <see cref="PdfAnnotationSession"/> 里，这里只做
/// 「会话 ↔ 视图模型 ↔ 写盘服务」的编排，不出现按工具的分支。
/// 保存策略（市调结论 2026-11-02）：
/// 档① 保留原文 + 叠加批注（默认，文本可检索）；档② 烧录到页面（定稿/防改）；
/// 档③ 导出图片型 PDF（脱敏安全出口——视觉遮盖不可靠，彻底脱敏走这一档）。
/// </summary>
public sealed partial class PdfPreviewViewModel
{
    /// <summary>写盘光栅的 DPI：比预览（150）高，烧录/叠加的图更实；标注坐标按两张光栅的
    /// 实际尺寸比例换算，所以渲染器的 2400px 长边上限不会让标注错位。</summary>
    private const int AnnotationDpi = 300;

    private readonly PdfAnnotationSession _annotation = new();
    private bool _isAnnotating;

    /// <summary>按页标注会话（当前页会话、脏页集、样式继承）。</summary>
    public PdfAnnotationSession Annotation => _annotation;

    /// <summary>是否处于标注编辑态（工具条/覆盖层/保存入口都看它）。</summary>
    public bool IsAnnotating
    {
        get => _isAnnotating;
        private set
        {
            if (SetProperty(ref _isAnnotating, value))
            {
                OnPropertyChanged(nameof(CanEditPages));
                OnPropertyChanged(nameof(AnnotationButtonText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>有未保存的标注（内存里有脏页）。</summary>
    public bool HasUnsavedAnnotations => _annotation.HasAnnotations;

    /// <summary>标注态下禁止调整页面：索引一变，按页标注就对不上旧页了。</summary>
    public bool CanEditPages => !IsAnnotating;

    /// <summary>工具条按钮文字：进入 / 退出。</summary>
    public string AnnotationButtonText => IsAnnotating ? "退出标注" : "标注";

    /// <summary>标注态常驻状态行：脏页数 + 保存口径。</summary>
    public string AnnotationStatusText
    {
        get
        {
            if (!IsAnnotating) return string.Empty;
            int dirty = _annotation.DirtyPages.Count;
            return dirty == 0 ? "选工具后在页面上绘制" : $"{dirty} 页已标注 · 未保存";
        }
    }

    public ICommand ToggleAnnotationCommand { get; }

    public ICommand SaveOverlayAnnotationsCommand { get; }

    public ICommand BurnAnnotationsCommand { get; }

    /// <summary>任何一页的标注集合变化：宿主（视图）刷新合成预览，状态行刷新。</summary>
    public event EventHandler? AnnotationsChanged;

    /// <summary>任何一页的样式变化：宿主刷工具条与指针抢占。</summary>
    public event EventHandler? AnnotationStylesChanged;

    /// <summary>进入/退出标注（按钮与 M 键）。</summary>
    internal void ToggleAnnotation()
    {
        if (IsAnnotating) ExitAnnotation();
        else EnterAnnotation();
    }

    internal void EnterAnnotation()
    {
        if (IsAnnotating || PageCount <= 0) return;

        IsAnnotating = true;
        AppLog.Info($"PDF 进入标注：第 {PageIndex + 1} 页 / {Path.GetFileName(_file.Path)}");
        OnAnnotationStateChanged();
    }

    /// <summary>退出标注：内存标注全部作废（磁盘文件自始至终没被碰过）。</summary>
    internal void ExitAnnotation()
    {
        if (!IsAnnotating) return;

        _annotation.Reset();
        IsAnnotating = false;
        AppLog.Info("PDF 退出标注（未保存的标注已放弃）");
        OnAnnotationStateChanged();
    }

    private void OnAnnotationStateChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedAnnotations));
        OnPropertyChanged(nameof(AnnotationStatusText));
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    // --- 三档保存 ------------------------------------------------------------------

    /// <summary>
    /// 档①（默认）：标注层叠加回 PDF——原文与文本层保留、可检索。只碰脏页；原文件被占用时
    /// 自动另存副本并跟随（与旋转/删除同一模式）。保存后清掉这些页的内存标注并重渲染：
    /// 屏幕上看到的就是落盘结果，继续标注在新底图上叠加。
    /// </summary>
    internal async Task SaveOverlayAnnotationsAsync()
    {
        if (!IsAnnotating) return;

        IReadOnlyList<int> dirty = _annotation.DirtyPages;
        if (dirty.Count == 0)
        {
            ShowTransientStatus("还没有标注可保存");
            return;
        }

        IsRotating = true; // 写门单飞：与页面旋转/删除共用同一把锁
        StatusText = "正在保存标注…";
        try
        {
            List<PdfAnnotationPage> payloads = await BuildAnnotationPayloadsAsync(dirty, overlay: true)
                .ConfigureAwait(true);
            bool savedAsCopy = await _edits.OverlayAnnotationsAsync(payloads).ConfigureAwait(true);
            await AfterAnnotationsSavedAsync(dirty, savedAsCopy,
                "已叠加批注并保存（原文保留，文本仍可检索）").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("保存标注失败", ex);
            StatusText = $"保存标注失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>
    /// 档②：把脏页烧录为图像页——定稿/防改，这些页从此不可检索、不可再改（其余页不动）。
    /// </summary>
    internal async Task BurnAnnotationsAsync()
    {
        if (!IsAnnotating) return;

        IReadOnlyList<int> dirty = _annotation.DirtyPages;
        if (dirty.Count == 0)
        {
            ShowTransientStatus("还没有标注可烧录");
            return;
        }

        IsRotating = true;
        StatusText = "正在烧录页面…";
        try
        {
            List<PdfAnnotationPage> payloads = await BuildAnnotationPayloadsAsync(dirty, overlay: false)
                .ConfigureAwait(true);
            bool savedAsCopy = await _edits.BurnAnnotationsAsync(payloads).ConfigureAwait(true);
            await AfterAnnotationsSavedAsync(dirty, savedAsCopy,
                "已烧录定稿（这些页已变为图像，文本不可检索）").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("烧录页面失败", ex);
            StatusText = $"烧录失败：{ex.Message}";
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>档③：整文档导出图片型 PDF（脱敏安全出口）。新文件，原 PDF 不动。</summary>
    internal async Task<string?> ExportImagePdfAsync(string targetPath, IProgress<int>? progress = null)
    {
        if (PageCount <= 0) return null;

        IsRotating = true;
        StatusText = "正在导出图片型 PDF…";
        try
        {
            var pages = new List<ImagePdfPage>(PageCount);
            for (int page = 0; page < PageCount; page++)
            {
                byte[] render = await _cache.GetPageBytesAsync(page, AnnotationDpi, priority: true)
                    .ConfigureAwait(true);
                using SKBitmap? baseBitmap = SKBitmap.Decode(render);
                if (baseBitmap is null) throw new InvalidOperationException($"第 {page + 1} 页渲染结果无法解码");

                // 这一档连没标注的页也整体光栅化：底层文字随页面一起变成像素，搜不出来。
                using SKBitmap composed = ScreenshotComposer.Compose(
                    baseBitmap,
                    new SKRectI(0, 0, baseBitmap.Width, baseBitmap.Height),
                    _annotation.AnnotationsOf(page));
                pages.Add(new ImagePdfPage(
                    EncodePng(composed), composed.Width, composed.Height, AnnotationDpi));
                progress?.Report(page + 1);
            }

            byte[] pdf = PdfAnnotationExporter.BuildImagePdf(Title, pages);
            await File.WriteAllBytesAsync(targetPath, pdf).ConfigureAwait(true);
            ShowTransientStatus($"已导出图片型 PDF：{Path.GetFileName(targetPath)}");
            AppLog.Info($"PDF 标注导出图片型 PDF：{targetPath}（{PageCount} 页）");
            return targetPath;
        }
        catch (Exception ex)
        {
            AppLog.Warn("导出图片型 PDF 失败", ex);
            StatusText = $"导出失败：{ex.Message}";
            return null;
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>导出当前页（含标注）为 PNG——屏幕上看到的就是导出的图。</summary>
    internal async Task<string?> ExportPagePngAsync(string targetPath, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) return null;

        try
        {
            byte[] render = await _cache.GetPageBytesAsync(pageIndex, PreviewDpi, priority: true)
                .ConfigureAwait(true);
            using SKBitmap? baseBitmap = SKBitmap.Decode(render);
            if (baseBitmap is null) throw new InvalidOperationException("页面渲染结果无法解码");

            using SKBitmap composed = ScreenshotComposer.Compose(
                baseBitmap,
                new SKRectI(0, 0, baseBitmap.Width, baseBitmap.Height),
                _annotation.AnnotationsOf(pageIndex));
            await File.WriteAllBytesAsync(targetPath, EncodePng(composed)).ConfigureAwait(true);
            ShowTransientStatus($"已导出 PNG：{Path.GetFileName(targetPath)}");
            return targetPath;
        }
        catch (Exception ex)
        {
            AppLog.Warn("导出 PNG 失败", ex);
            StatusText = $"导出失败：{ex.Message}";
            return null;
        }
    }

    // --- 内部 -----------------------------------------------------------------------

    /// <summary>当前页的预览光栅字节（150dpi）——视图据此合成「页面 + 标注」的预览。</summary>
    internal Task<byte[]> GetPreviewBytesAsync(int pageIndex) =>
        _cache.GetPageBytesAsync(pageIndex, PreviewDpi, priority: true);

    /// <summary>
    /// 把脏页渲染 + 合成为写盘载荷。<paramref name="overlay"/> = 只画标注层（透明底，档①）；
    /// 否则整页烧录图（档②）。标注坐标在预览光栅（150dpi）空间，写盘光栅按
    /// <see cref="AnnotationDpi"/> 重新渲染，换算比例用两张光栅的实际尺寸量出来。
    /// </summary>
    private async Task<List<PdfAnnotationPage>> BuildAnnotationPayloadsAsync(
        IReadOnlyList<int> pages, bool overlay)
    {
        var payloads = new List<PdfAnnotationPage>(pages.Count);
        foreach (int page in pages)
        {
            byte[] previewBytes = await _cache.GetPageBytesAsync(page, PreviewDpi, priority: true)
                .ConfigureAwait(true);
            byte[] writeBytes = await _cache.GetPageBytesAsync(page, AnnotationDpi, priority: true)
                .ConfigureAwait(true);

            using SKBitmap? previewBitmap = SKBitmap.Decode(previewBytes);
            using SKBitmap? writeBitmap = SKBitmap.Decode(writeBytes);
            if (previewBitmap is null || writeBitmap is null) continue;

            double factor = writeBitmap.Width / (double)previewBitmap.Width;
            IReadOnlyList<ScreenshotAnnotation> annotations =
                ScaleAnnotations(_annotation.AnnotationsOf(page), factor);

            using SKBitmap image = overlay
                ? ScreenshotComposer.ComposeAnnotationLayer(
                    writeBitmap.Width, writeBitmap.Height, annotations, writeBitmap)
                : ScreenshotComposer.Compose(
                    writeBitmap, new SKRectI(0, 0, writeBitmap.Width, writeBitmap.Height), annotations);
            payloads.Add(new PdfAnnotationPage(page, EncodePng(image)));
        }

        return payloads;
    }

    /// <summary>保存落库后的统一收尾：清内存标注、刷新缩略图与画布、汇报结果。</summary>
    private async Task AfterAnnotationsSavedAsync(
        IReadOnlyList<int> pages, bool savedAsCopy, string message)
    {
        foreach (int page in pages)
        {
            _annotation.ClearPage(page);

            byte[] thumbBytes = await _cache.GetPageBytesAsync(page, ThumbnailDpi, priority: true)
                .ConfigureAwait(true);
            if (page < Thumbnails.Count)
                Thumbnails[page].SetImage(await Task.Run(() => MakeThumbSource(thumbBytes)).ConfigureAwait(true));
        }

        OnAnnotationStateChanged();
        // 写门已失效这些页的渲染缓存：重渲染后画布上的就是落盘结果。
        await RenderAsync(PageIndex, force: true).ConfigureAwait(true);

        string copyNote = savedAsCopy
            ? $"，原文件被占用，已另存为 {Path.GetFileName(_file.Path)}"
            : string.Empty;
        ShowTransientStatus(message + copyNote);
        AppLog.Info($"PDF 标注已保存：{message}{copyNote}");
    }

    /// <summary>标注坐标按光栅比例缩放（写盘光栅通常比预览光栅大）。</summary>
    private static IReadOnlyList<ScreenshotAnnotation> ScaleAnnotations(
        IReadOnlyList<ScreenshotAnnotation> source, double factor)
    {
        if (source.Count == 0 || Math.Abs(factor - 1) < 0.0001) return source;

        var scaled = new ScreenshotAnnotation[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            ScreenshotAnnotation annotation = source[i];
            var points = new ScreenshotPointF[annotation.Points.Count];
            for (int j = 0; j < points.Length; j++)
            {
                points[j] = new ScreenshotPointF(
                    (float)(annotation.Points[j].X * factor),
                    (float)(annotation.Points[j].Y * factor));
            }

            scaled[i] = new ScreenshotAnnotation
            {
                Kind = annotation.Kind,
                Points = points,
                ColorArgb = annotation.ColorArgb,
                Thickness = (float)(annotation.Thickness * factor),
                Text = annotation.Text,
                FontSize = (float)(annotation.FontSize * factor),
            };
        }

        return scaled;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
