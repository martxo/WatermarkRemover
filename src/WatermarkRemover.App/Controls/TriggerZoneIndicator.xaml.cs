using System;
using System.Windows;
using System.Windows.Media.Animation;
using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.App.Controls;

/// <summary>
/// 触发区域「云朵」指示器：鼠标进入热区时在热区居中浮出一朵半透明云，让用户直观看见
/// 「这里就是触发区」。窗口点击穿透（IsHitTestVisible=False → WS_EX_TRANSPARENT）、
/// 不抢焦点、不进任务栏与 Alt+Tab，只在自己可见的一瞬存在。晾衣绳弹出、截图会话或
/// 配置变更时立即淡出。与设置页示意图的热区块共用 Themes/TriggerZone.xaml 的云朵资源。
/// </summary>
public partial class TriggerZoneIndicator : Window
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(160);

    /// <summary>云朵比热区大，窗口各边外扩一圈，保证云朵不被窗口边界裁掉。</summary>
    private const double Padding = 40;

    private bool _visible;

    public TriggerZoneIndicator() => InitializeComponent();

    /// <summary>把云朵挪到指定热区矩形（屏幕坐标）并淡入；已显示时只跟手不重启动画。</summary>
    public void ShowAt(ScreenRect zone)
    {
        Left = zone.X - Padding;
        Top = zone.Y - Padding;
        Width = Math.Max(1, zone.Width + Padding * 2);
        Height = Math.Max(1, zone.Height + Padding * 2);

        if (_visible) return;
        _visible = true;
        Visibility = Visibility.Visible;
        BeginFade(0.92, FadeIn, onDone: null);
    }

    /// <summary>淡出云朵；已在隐藏状态时是空操作。</summary>
    public void HideCloud()
    {
        if (!_visible) return;
        _visible = false;
        BeginFade(0, FadeOut, () => Visibility = Visibility.Collapsed);
    }

    private void BeginFade(double to, TimeSpan duration, Action? onDone)
    {
        var animation = new DoubleAnimation(to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (onDone is not null) animation.Completed += (_, _) => onDone();
        BeginAnimation(OpacityProperty, animation);
    }
}
