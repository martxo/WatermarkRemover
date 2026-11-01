using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App;

/// <summary>Read-only OCR result for one screenshot, with a one-click copy for pasting elsewhere.</summary>
public partial class ScreenshotOcrWindow : Window
{
    private readonly DispatcherTimer _statusTimer;

    public ScreenshotOcrWindow(string text, string engineName)
    {
        InitializeComponent();

        EngineText.Text = $"识别引擎：{engineName}";
        ResultBox.Text = text;

        string normalized = text.Replace("\r\n", "\n");
        int characters = normalized.Replace("\n", string.Empty).Length;
        int lines = string.IsNullOrEmpty(normalized) ? 0 : normalized.Split('\n').Length;
        SummaryText.Text = characters == 0
            ? "未识别到文字"
            : $"共 {characters} 个字符 · {lines} 行";

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            SummaryText.Text = characters == 0
                ? "未识别到文字"
                : $"共 {characters} 个字符 · {lines} 行";
        };
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string text = ResultBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                SummaryText.Text = "已复制到剪贴板";
                _statusTimer.Stop();
                _statusTimer.Start();
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                {
                    AppLog.Warn($"复制 OCR 文字失败：{ex.Message}");
                    SummaryText.Text = "复制失败，请重试";
                    return;
                }

                Thread.Sleep(40);
            }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
