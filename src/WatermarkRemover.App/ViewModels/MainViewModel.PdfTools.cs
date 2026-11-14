using System.Diagnostics;
using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// 「PDF 工具」记录行上的就地「识别文字」：直接运行 OCR 管线并把结果应用回这一行，
    /// 不新建记录。行显示处理状态；OCR 单通道引擎与批量处理互斥，忙时礼貌拒绝。
    /// </summary>
    private async Task RunRowOcrAsync(FileJobViewModel job)
    {
        if (job.OutputPath is not { } path || !File.Exists(path))
        {
            job.ShowTransientNameNote("文件已不存在，无法识别");
            return;
        }

        if (IsBusy)
        {
            job.ShowTransientNameNote("正在处理其他文件，完成后再识别");
            return;
        }

        using var op = AppLog.BeginOperation(LogCategory.Ocr, "行内识别文字", Path.GetFileName(path));
        IFeature processor = _composition.PdfToolbox.Ocr;
        job.MarkProcessing("正在识别文字…");
        IsBusy = true;
        try
        {
            var context = FeatureContext.Capture(Settings, (_, stage) => job.ReportStage(stage));
            FeatureResult result = await Task.Run(() => processor.Run(path, context)).ConfigureAwait(true);
            job.Apply(result);
            Board.MarkCompleted(job);
            op.Complete(result.Summary);
        }
        catch (Exception ex)
        {
            AppLog.Error("行内识别文字失败", ex);
            job.Apply(FeatureResult.Fail(path, $"识别失败：{ex.Message}"));
            op.Fail("行内识别文字失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
