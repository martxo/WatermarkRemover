using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WatermarkRemover.App.Controls;

/// <summary>
/// 文字标注浮层（截图遮罩 / 标注窗共用）。Enter 提交、Shift+Enter 换行、Esc 取消、失焦提交；
/// <see cref="Completed"/> 的文本为 null 表示取消。宿主把控件放在自己的画布上，
/// 调用 <see cref="Start"/> 定位——控件同时写 Margin 与 Canvas 附加属性，两种容器都能定位。
/// </summary>
public partial class AnnotationTextEditor : UserControl
{
    private bool _active;

    public AnnotationTextEditor()
    {
        InitializeComponent();
    }

    /// <summary>是否正在输入中。</summary>
    public bool IsActive => _active;

    /// <summary>输入结束：文本 = 提交内容；null = 取消。每次输入只触发一次。</summary>
    public event EventHandler<string?>? Completed;

    /// <summary>在指定位置打开浮层并聚焦。</summary>
    public void Start(Point topLeft, double fontSizeDip, Color color, double width, double height)
    {
        Width = width;
        Height = height;
        Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
        Canvas.SetLeft(this, topLeft.X);
        Canvas.SetTop(this, topLeft.Y);
        Editor.FontSize = fontSizeDip;
        Editor.Foreground = new SolidColorBrush(color);
        Editor.Text = string.Empty;
        Visibility = Visibility.Visible;
        _active = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Keyboard.Focus(Editor);
            Editor.SelectAll();
        }), DispatcherPriority.Input);
    }

    /// <summary>收起浮层（不触发 <see cref="Completed"/>）。</summary>
    public void Hide()
    {
        _active = false;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>按当前内容提交（宿主在画布上点击时调用，等价于失焦提交）。</summary>
    public void Submit() => Finish(Editor.Text);

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Shift+Enter 交给文本框本身插入换行；合成器按行绘制。
            Finish(Editor.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Finish(null);
            e.Handled = true;
        }
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (_active) Finish(Editor.Text);
    }

    private void Finish(string? text)
    {
        if (!_active) return;
        _active = false;
        Visibility = Visibility.Collapsed;
        Completed?.Invoke(this, text);
    }
}
