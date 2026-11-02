using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Interop;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.App;

/// <summary>
/// A close reconstruction of Tendedero's clothesline (see its Line class):
/// the rope is a fixed quadratic sag, photos are evenly spread from the centre outwards,
/// each peg swings with a damped spring, new shots drop in from 46px above and kick the line,
/// hover grows the glass card by 3.5%, and a copy shows the checkmark capsule and a 3° nudge.
/// The line hides itself above the screen until the pointer rests in a configured trigger zone
/// (截图设置 → 触发区域: four screen edges, each independently enabled).
/// </summary>
public partial class ScreenshotShelfWindow : Window
{
    private const double RopeBaseY = 42;
    private const double CardWidth = 150;
    private const double CardHalf = CardWidth / 2;
    private const double Spacing = CardWidth + 24;
    private const int MaxCapacity = 12;
    private const double HiddenHeight = 220;

    private const double HoldMilliseconds = 450;
    private const double RevealDelayMilliseconds = 250;
    private const double RetractDelayMilliseconds = 500;
    private const double PeekMilliseconds = 2500;

    private readonly DataTemplate _photoTemplate;
    private readonly List<ShelfItem> _items = new();
    private readonly List<ShelfItem> _falling = new();
    private readonly List<FileJobViewModel> _initialJobs;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _pointerTimer;
    private readonly DispatcherTimer _longPressTimer;
    private readonly DispatcherTimer _gustTimer;
    private readonly Random _random = new();

    /// <summary>触发区域配置（截图设置，四边独立）；设置保存后由宿主热更新。</summary>
    private ScreenshotTriggerSettings _trigger;

    /// <summary>上次轮询时刻（探针：实测轮询间隔，验证定时器是否被饿死）。</summary>
    private long _lastPointerTickMs = Environment.TickCount64;

    /// <summary>窗口句柄钩子（探针：激活/失焦/鼠标离开等系统消息）。</summary>
    private HwndSource? _hwndSource;

    private Rect _workArea;
    private long _lastFrameMs;
    private double _windowTop;
    private double _windowTopVelocity;
    private double _sag;
    private double _sagVelocity;
    private double _sagTarget;
    private double _lastDrawnSag = -1;
    private bool _revealed;
    private bool _manual;
    private bool _initialised;

    private DateTime _topHotSince = DateTime.MinValue;
    private DateTime _awaySince = DateTime.MinValue;
    private DateTime _peekUntil = DateTime.MinValue;

    private FileJobViewModel? _dragItem;
    private FileJobViewModel? _longPressJob;
    private UIElement? _longPressSource;
    private Point _dragStart;
    private bool _dragStarted;
    private bool _longPressFired;

    public ScreenshotShelfWindow(
        IReadOnlyList<FileJobViewModel> items, Action openFolder, ScreenshotTriggerSettings trigger)
    {
        ArgumentNullException.ThrowIfNull(openFolder);
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _initialJobs = items.Where(i => i.HasThumbnail).ToList();

        InitializeComponent();
        _photoTemplate = (DataTemplate)FindResource("PhotoTemplate");

        _workArea = SystemParameters.WorkArea;
        Width = _workArea.Width;
        Left = _workArea.Left;
        Height = HiddenHeight;
        _windowTop = _workArea.Top - HiddenHeight;
        Top = _windowTop;
        IsHitTestVisible = false;

        RopeShadow.RenderTransform = new TranslateTransform(0, 1.2);
        RopeShadow.Effect = new BlurEffect { Radius = 1.2 };
        RopeHighlight.RenderTransform = new TranslateTransform(0, -0.35);

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frameTimer.Tick += OnFrame;

        _pointerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pointerTimer.Tick += OnPointerTick;

        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMilliseconds) };
        _longPressTimer.Tick += OnLongPressTick;

        _gustTimer = new DispatcherTimer();
        _gustTimer.Tick += OnGustTick;

        Loaded += OnLoaded;
        SizeChanged += (_, _) => OnSizeChanged();
        MouseEnter += OnWindowMouseEnter;
        MouseLeave += OnWindowMouseLeave;
        Closing += (_, _) =>
        {
            _hwndSource?.RemoveHook(WndProc);
            _frameTimer.Stop();
            _pointerTimer.Stop();
            _longPressTimer.Stop();
            _gustTimer.Stop();
            foreach (ShelfItem item in _items)
                item.Root.ClearValue(RenderTransformProperty);
        };

        _pointerTimer.Start();
        _lastFrameMs = Environment.TickCount64;
        ScheduleGust();
    }

    /// <summary>Tendedero's "hold to mark up": long-pressing a photo opens the markup editor.</summary>
    public event EventHandler<FileJobViewModel>? MarkupRequested;

    private int Capacity => Clamp((int)Math.Floor((LineWidth - 200) / Spacing), 3, MaxCapacity);

    /// <summary>
    /// The strip is shown a frame before its first layout pass, so ActualWidth can still be 0
    /// when a screenshot is hung. Geometry must use the work area until the window is measured,
    /// otherwise the first photo lands at x=0 (the far left) instead of on the middle.
    /// </summary>
    private double LineWidth => ActualWidth > 1 ? ActualWidth : Math.Max(1, _workArea.Width);

    private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    // --- Initial layout --------------------------------------------------------

    private void OnLoaded(object sender, EventArgs e)
    {
        if (_initialised) return;
        _initialised = true;

        _workArea = SystemParameters.WorkArea;
        Width = _workArea.Width;
        Left = _workArea.Left;
        _windowTop = _revealed ? _workArea.Top : _workArea.Top - HiddenHeight;
        Top = _windowTop;
        ItemsLayer.Width = Math.Max(1, RootCanvas.ActualWidth > 1 ? RootCanvas.ActualWidth : LineWidth);
        ItemsLayer.Height = Math.Max(1, RootCanvas.ActualHeight > 1 ? RootCanvas.ActualHeight : HiddenHeight);

        int capacity = Capacity;
        // The view model lists newest first; the line reads oldest -> newest, left -> right.
        foreach (FileJobViewModel job in _initialJobs.Take(capacity).Reverse())
            CreateItem(job, animateLanding: false);

        _sag = BaseSagForCount(_items.Count);
        _sagTarget = _sag;
        LayoutTargets(instant: true);
        UpdateRope(force: true);
        UpdateEmptyHint();

        ClampTrigger();
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        ShelfProbe.Log($"窗口就绪 Topmost={Topmost} ShowActivated={ShowActivated} " +
                       $"工作区=({_workArea.Left:F0},{_workArea.Top:F0}) {_workArea.Width:F0}×{_workArea.Height:F0} " +
                       $"触发区=[{DescribeZones()}] 弹出阈值={RevealDelayMilliseconds:F0}ms 收回阈值={RetractDelayMilliseconds:F0}ms");
    }

    private void OnSizeChanged()
    {
        if (!_initialised) return;
        _workArea = SystemParameters.WorkArea;
        Width = _workArea.Width;
        Left = _workArea.Left;
        ClampTrigger();
        ItemsLayer.Width = Math.Max(1, RootCanvas.ActualWidth > 1 ? RootCanvas.ActualWidth : LineWidth);
        ItemsLayer.Height = Math.Max(1, RootCanvas.ActualHeight > 1 ? RootCanvas.ActualHeight : HiddenHeight);

        // Keep the newest photos when the screen gets narrower.
        int capacity = Capacity;
        while (_items.Count > capacity)
        {
            ShelfItem oldest = _items[0];
            _items.RemoveAt(0);
            ItemsLayer.Children.Remove(oldest.Root);
            StopSwing(oldest);
        }

        LayoutTargets(instant: true);
        UpdateRope(force: true);
        UpdateEmptyHint();
    }

    // --- Public surface --------------------------------------------------------

    public void Toggle()
    {
        if (_revealed) HideLine();
        else ShowLine(manual: true);
    }

    public void AddScreenshot(FileJobViewModel job)
    {
        if (job.OutputPath is null || !job.HasThumbnail || _items.Any(i => i.Job == job)) return;
        if (!_initialised)
        {
            _initialJobs.Add(job);
            return;
        }

        if (!_revealed) ShowLine(manual: false);
        _peekUntil = DateTime.Now.AddMilliseconds(PeekMilliseconds);

        CreateItem(job, animateLanding: true);
        _sagTarget = BaseSagForCount(_items.Count);
        _sagVelocity += 9; // the new weight kicks the rope
        LayoutTargets(instant: false);

        int capacity = Capacity;
        while (_items.Count > capacity)
        {
            ShelfItem oldest = _items[0];
            _items.RemoveAt(0);
            StartFall(oldest);
        }
    }

    /// <summary>Hide immediately before capture so the line cannot be part of the PNG.</summary>
    public void SuppressForCapture()
    {
        _revealed = false;
        _manual = false;
        _peekUntil = DateTime.MinValue;
        _windowTop = _workArea.Top - HiddenHeight;
        _windowTopVelocity = 0;
        _sagVelocity = 0;
        _frameTimer.Stop();
        BeginAnimation(TopProperty, null);
        Top = _windowTop;
        IsHitTestVisible = false;
    }

    /// <summary>设置保存后热更新触发区域（改配置不用重启）。</summary>
    public void UpdateTrigger(ScreenshotTriggerSettings trigger)
    {
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        ClampTrigger();
        ShelfProbe.Log($"触发区域已更新：[{DescribeZones()}]");
    }

    // --- Show / hide -----------------------------------------------------------

    private void ShowLine(bool manual)
    {
        _peekUntil = DateTime.MinValue;
        _revealed = true;
        _manual |= manual;
        IsHitTestVisible = true;
        _topHotSince = DateTime.MinValue;

        if (!_frameTimer.IsEnabled)
        {
            _lastFrameMs = Environment.TickCount64;
            _frameTimer.Start();
            ShelfProbe.Log("帧定时器启动（弹簧动画）");
        }

        ShelfProbe.Log($"弹出 manual={manual} Topmost={Topmost} 可点击={IsHitTestVisible}");
    }

    private void HideLine()
    {
        _revealed = false;
        _manual = false;
        _peekUntil = DateTime.MinValue;
        IsHitTestVisible = false;
        foreach (ShelfItem item in _items)
            HideHover(item);

        ShelfProbe.Log("收回");
    }

    private void OnPointerTick(object? sender, EventArgs e)
    {
        if (!GetCursorPos(out CursorPoint cursor)) return;

        // 探针：实测轮询间隔。DispatcherTimer 在 UI 线程繁忙时会拖后腿，间隔本身就是证据。
        long tickNow = Environment.TickCount64;
        long interval = tickNow - _lastPointerTickMs;
        _lastPointerTickMs = tickNow;
        DateTime now = DateTime.Now;

        if (!_revealed)
        {
            ScreenEdge? hot = _trigger.HitTest(
                cursor.X, cursor.Y, _workArea.Left, _workArea.Top, _workArea.Width, _workArea.Height);
            bool gated = _manual || _dragStarted;
            if (hot is not null && !gated)
            {
                if (_topHotSince == DateTime.MinValue) _topHotSince = now;
                double held = (now - _topHotSince).TotalMilliseconds;
                if (held >= RevealDelayMilliseconds)
                {
                    _topHotSince = DateTime.MinValue;
                    ShelfProbe.Log($"轮询间隔{interval}ms 光标({cursor.X},{cursor.Y}) 命中{hot}边并持续{held:F0}ms，达到弹出阈值");
                    ShowLine(manual: false);
                }
                else
                {
                    ShelfProbe.Log($"轮询间隔{interval}ms 光标({cursor.X},{cursor.Y}) 命中{hot}边，持续{held:F0}ms / {RevealDelayMilliseconds:F0}ms");
                }
            }
            else
            {
                if (_topHotSince != DateTime.MinValue)
                    ShelfProbe.Log($"轮询间隔{interval}ms 光标({cursor.X},{cursor.Y}) 热区中断（manual={_manual} drag={_dragStarted}），持续计时清零");
                _topHotSince = DateTime.MinValue;
            }

            return;
        }

        bool inside = cursor.Y >= _workArea.Top - 2 &&
                      cursor.Y <= _workArea.Top + Height + 8 &&
                      cursor.X >= _workArea.Left && cursor.X <= _workArea.Right;
        double peekLeft = Math.Max(0, (_peekUntil - now).TotalMilliseconds);

        if (inside || _manual || _dragStarted || now < _peekUntil)
        {
            if (_awaySince != DateTime.MinValue)
                ShelfProbe.Log($"轮询间隔{interval}ms 光标({cursor.X},{cursor.Y}) 恢复保持条件（在内={inside} manual={_manual} drag={_dragStarted} peek余={peekLeft:F0}ms），离开计时清零");
            _awaySince = DateTime.MinValue;
            return;
        }

        if (_awaySince == DateTime.MinValue)
        {
            _awaySince = now;
            ShelfProbe.Log($"轮询间隔{interval}ms 光标({cursor.X},{cursor.Y}) 离开保持区，开始计时（需{RetractDelayMilliseconds:F0}ms）");
        }
        else if ((now - _awaySince).TotalMilliseconds >= RetractDelayMilliseconds)
        {
            double away = (now - _awaySince).TotalMilliseconds;
            _awaySince = DateTime.MinValue;
            ShelfProbe.Log($"轮询间隔{interval}ms 光标({cursor.X},{cursor.Y}) 已离开{away:F0}ms，达到收回阈值");
            HideLine();
        }
        else
        {
            ShelfProbe.Log($"轮询间隔{interval}ms 光标({cursor.X},{cursor.Y}) 保持离开，已计{(now - _awaySince).TotalMilliseconds:F0}ms / {RetractDelayMilliseconds:F0}ms");
        }
    }

    // --- 触发区域 / 探针 ---------------------------------------------------------

    private void ClampTrigger()
    {
        _trigger.Clamp(_workArea.Width, _workArea.Height);
    }

    private string DescribeZones()
    {
        var parts = new List<string>();
        foreach (ScreenEdge edge in Enum.GetValues<ScreenEdge>())
        {
            TriggerZoneEdge zone = _trigger.Of(edge);
            if (!zone.Enabled) continue;
            parts.Add($"{edge} 厚{zone.Thickness} 长{zone.Length} 位{zone.Position:F2}");
        }

        return parts.Count == 0 ? "无启用边" : string.Join("；", parts);
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e) =>
        ShelfProbe.Log("WPF MouseEnter：指针进入晾衣绳窗口");

    private void OnWindowMouseLeave(object sender, MouseEventArgs e) =>
        ShelfProbe.Log("WPF MouseLeave：指针离开晾衣绳窗口");

    /// <summary>
    /// 系统消息探针：激活/失焦/鼠标离开窗口。「切到其他应用不收回」必须先确认这些消息
    /// 到底有没有到窗口，再决定根因方向（没收到底层消息 vs 收到了但判定逻辑不放行）。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0006: // WM_ACTIVATE
                ShelfProbe.Log($"WM_ACTIVATE 激活={wParam.ToInt32() != 0}");
                break;
            case 0x001C: // WM_ACTIVATEAPP
                ShelfProbe.Log($"WM_ACTIVATEAPP 激活={wParam != IntPtr.Zero}");
                break;
            case 0x0008: // WM_KILLFOCUS
                ShelfProbe.Log($"WM_KILLFOCUS 新焦点窗口=0x{wParam.ToInt64():X8}");
                break;
            case 0x0021: // WM_MOUSEACTIVATE
                ShelfProbe.Log($"WM_MOUSEACTIVATE 命中测试={wParam.ToInt32()} 鼠标消息={(int)(lParam.ToInt64() & 0xFFFF):X4}");
                break;
            case 0x02A3: // WM_MOUSELEAVE
                ShelfProbe.Log("WM_MOUSELEAVE：系统通知指针离开窗口");
                break;
        }

        return IntPtr.Zero;
    }

    // --- Items -----------------------------------------------------------------

    private void CreateItem(FileJobViewModel job, bool animateLanding)
    {
        FrameworkElement root = (FrameworkElement)_photoTemplate.LoadContent();
        root.DataContext = job;

        Border card = FindNamed<Border>(root, "PhotoCard")!;
        Border remove = FindNamed<Border>(root, "RemoveChip")!;
        Border copied = FindNamed<Border>(root, "CopiedBadge")!;
        Image photo = FindNamed<Image>(root, "PhotoImage")!;
        // The photo itself is rounded to 12px inside the 16px glass, like .card img.
        photo.SizeChanged += (_, _) => ApplyPhotoClip(photo);

        var crossScale = new ScaleTransform(0.6, 0.6);
        remove.RenderTransform = crossScale;
        var copiedShift = new TranslateTransform(0, 4);
        copied.RenderTransform = copiedShift;

        var cardScale = new ScaleTransform(1, 1, CardHalf, 0);
        card.RenderTransform = cardScale;

        // The template's effect is shared and frozen; each card needs a mutable clone to animate.
        DropShadowEffect shadow = ((DropShadowEffect)card.Effect).Clone();
        card.Effect = shadow;

        var drop = new TranslateTransform();
        var rotate = new RotateTransform();
        var group = new TransformGroup();
        group.Children.Add(drop);
        group.Children.Add(rotate);
        root.RenderTransform = group;

        int seed = StableHash(job.OutputPath ?? job.DisplayName);
        var item = new ShelfItem
        {
            Job = job,
            Root = root,
            Card = card,
            RemoveChip = remove,
            CopiedBadge = copied,
            CrossScale = crossScale,
            CopiedShift = copiedShift,
            CardScale = cardScale,
            Shadow = shadow,
            Drop = drop,
            Rotate = rotate,
            Tilt = (seed % 501) / 100.0 - 2.5, // -2.5° .. +2.5°
            Opacity = 1,
        };

        _items.Add(item);
        ItemsLayer.Children.Add(root);
        ItemsLayer.Width = Math.Max(1, RootCanvas.ActualWidth > 1 ? RootCanvas.ActualWidth : LineWidth);
        ItemsLayer.Height = Math.Max(1, RootCanvas.ActualHeight > 1 ? RootCanvas.ActualHeight : HiddenHeight);

        if (animateLanding)
        {
            // Line.hang(): a fresh photo drops from 46px above and lands with a healthy swing.
            item.DropY = -46;
            item.DropVelocity = 0;
            item.Theta = 16;
            item.Omega = 0;
            item.Opacity = 0;
        }

        double targetX = TargetXFor(_items.Count - 1, _items.Count);
        item.X = targetX;
        item.TargetX = targetX;
        item.Y = LineY(targetX) - 9.5;
        item.TargetY = item.Y;
        UpdateItemVisual(item);
    }

    private void LayoutTargets(bool instant)
    {
        int count = _items.Count;
        for (int i = 0; i < count; i++)
        {
            ShelfItem item = _items[i];
            item.TargetX = TargetXFor(i, count);
            if (instant)
            {
                item.X = item.TargetX;
                item.XVelocity = 0;
                item.Y = LineY(item.TargetX) - 9.5;
                item.TargetY = item.Y;
                item.YVelocity = 0;
                UpdateItemVisual(item);
            }
        }

        UpdateEmptyHint();
    }

    private double TargetXFor(int index, int count)
    {
        double gaps = Math.Max(0, count - 1);
        double step = gaps > 0
            ? Math.Min(Spacing, Math.Max(0, LineWidth - 200) / gaps)
            : Spacing;
        return LineWidth / 2 - gaps * step / 2 + index * step;
    }

    private double LineY(double x)
    {
        double width = LineWidth;
        double f = Math.Clamp(x / width, 0, 1);
        return RopeBaseY + 4 * _sag * f * (1 - f);
    }

    /// <summary>
    /// Line.js keeps the rope at min(30, width*1.8%) — always visibly sagged. Hanging clothes
    /// adds weight up to that maximum, and every landing kicks the rope a little further.
    /// </summary>
    private double BaseSagForCount(int count)
    {
        double maximum = Math.Min(30, LineWidth * 0.018);
        double weight = 0.8 + 0.05 * Math.Min(4, count); // 0.8 empty .. 1.0 full
        return maximum * Math.Min(1, weight);
    }

    private void UpdateItemVisual(ShelfItem item)
    {
        Canvas.SetLeft(item.Root, item.X - CardHalf);
        Canvas.SetTop(item.Root, item.Y);
        item.Drop.Y = item.DropY;
        item.Rotate.Angle = item.Tilt + item.Theta;
        item.Rotate.CenterX = CardHalf;
        item.Rotate.CenterY = 0;
        item.Root.Opacity = item.Opacity;
    }

    private void StopSwing(ShelfItem item)
    {
        item.CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        item.CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        item.RemoveChip.BeginAnimation(OpacityProperty, null);
    }

    // --- Frame loop ------------------------------------------------------------

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        double dt = Math.Clamp((now - _lastFrameMs) / 1000.0, 0, 0.05);
        _lastFrameMs = now;
        if (dt <= 0) return;

        // The strip glides down/up on a spring (Tendedero's reveal spring).
        double topTarget = _revealed ? _workArea.Top : _workArea.Top - HiddenHeight;
        StepSpring(ref _windowTop, ref _windowTopVelocity, topTarget, 224, 24.5, dt);
        if (!_revealed && Math.Abs(_windowTop - topTarget) < 0.4)
        {
            _windowTop = topTarget;
            _windowTopVelocity = 0;
        }
        Top = _windowTop;

        // Rope weight + landing bounce.
        StepSpring(ref _sag, ref _sagVelocity, _sagTarget, 90, 8, dt);
        UpdateRope(force: false);

        foreach (ShelfItem item in _items)
        {
            // Horizontal/vertical layout uses Tendedero's 0.55s spring feel.
            StepSpring(ref item.X, ref item.XVelocity, item.TargetX, 55, 11, dt);
            item.TargetY = LineY(item.TargetX) - 9.5;
            StepSpring(ref item.Y, ref item.YVelocity, item.TargetY, 70, 12, dt);

            // Swing: exactly the app's -46/2.6 damped spring.
            if (item.NudgeTarget is { } nudge)
            {
                double k = Math.Clamp((now - item.NudgeStartMs!.Value) / 300.0, 0, 1);
                item.Theta = item.NudgeFrom + (nudge - item.NudgeFrom) * (1 - Math.Pow(1 - k, 3));
                item.Omega = 0;
                if (k >= 1) item.NudgeTarget = null;
            }
            else
            {
                double remaining = dt;
                while (remaining > 0)
                {
                    double h = Math.Min(remaining, 1.0 / 240);
                    double a = -46 * item.Theta - 2.6 * item.Omega;
                    item.Omega += a * h;
                    item.Theta += item.Omega * h;
                    remaining -= h;
                }
            }

            StepSpring(ref item.DropY, ref item.DropVelocity, 0, 224, 21.5, dt);
            item.Opacity = Math.Min(1, item.Opacity + dt * 6);

            UpdateItemVisual(item);
        }

        for (int i = _falling.Count - 1; i >= 0; i--)
        {
            if (UpdateFall(_falling[i], now))
            {
                ItemsLayer.Children.Remove(_falling[i].Root);
                StopSwing(_falling[i]);
                _falling.RemoveAt(i);
            }
        }

        UpdateEmptyHint();

        // Hidden and settled: no reason to keep a 60fps clock running while in the tray.
        if (!_revealed && _falling.Count == 0 &&
            Math.Abs(_windowTop - (int)(_workArea.Top - HiddenHeight)) < 0.5)
        {
            _frameTimer.Stop();
            ShelfProbe.Log("帧定时器停止（已收起且无掉落动画）");
        }
    }

    private static void StepSpring(
        ref double value, ref double velocity, double target, double stiffness, double damping, double dt)
    {
        double acceleration = -stiffness * (value - target) - damping * velocity;
        velocity += acceleration * dt;
        value += velocity * dt;
    }

    private bool UpdateFall(ShelfItem item, long now)
    {
        double raw = Math.Clamp((now - item.FallStartMs!.Value) / 550.0, 0, 1);
        double e = raw * raw * raw;
        Canvas.SetLeft(item.Root, item.X - CardHalf);
        Canvas.SetTop(item.Root, item.Y + 520 * e);
        item.Rotate.Angle = item.Tilt + item.Theta + (item.Tilt * 7 + 20) * e;
        item.Root.Opacity = 1 - e;
        return raw >= 1;
    }

    private void StartFall(ShelfItem item)
    {
        if (item.FallStartMs is not null) return;
        item.FallStartMs = Environment.TickCount64;
        item.CardScale.ScaleX = 1;
        item.CardScale.ScaleY = 1;
        item.RemoveChip.Opacity = 0;
        _falling.Add(item);
        if (!_frameTimer.IsEnabled)
        {
            _lastFrameMs = Environment.TickCount64;
            _frameTimer.Start();
        }
    }

    private void StartNudge(ShelfItem item, double degrees)
    {
        item.NudgeFrom = item.Theta;
        item.NudgeTarget = degrees;
        item.NudgeStartMs = Environment.TickCount64;
    }

    private void ScheduleGust()
    {
        _gustTimer.Interval = TimeSpan.FromMilliseconds(_random.Next(7000, 16000));
        _gustTimer.Start();
    }

    private void OnGustTick(object? sender, EventArgs e)
    {
        _gustTimer.Stop();
        if (_items.Count > 0 && _revealed && !_dragStarted)
        {
            foreach (ShelfItem item in _items)
            {
                double degrees = (1.6 + _random.NextDouble() * 1.8) * (_random.Next(2) == 0 ? -1 : 1);
                StartNudge(item, degrees);
            }
        }

        ScheduleGust();
    }

    // --- Rope ------------------------------------------------------------------

    private void UpdateRope(bool force)
    {
        double width = LineWidth;
        if (width < 10) return;
        if (!force && Math.Abs(_sag - _lastDrawnSag) < 0.03) return;
        _lastDrawnSag = _sag;

        var figure = new PathFigure { StartPoint = new Point(-20, RopeBaseY) };
        figure.Segments.Add(new QuadraticBezierSegment(
            new Point(width / 2, RopeBaseY + 2 * _sag),
            new Point(width + 20, RopeBaseY),
            true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        Rope.Data = geometry;
        RopeShadow.Data = geometry;
        RopeHighlight.Data = geometry;
    }

    private void UpdateEmptyHint()
    {
        bool empty = _items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        EmptyHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(EmptyHint, Math.Max(8, LineWidth / 2 - EmptyHint.DesiredSize.Width / 2));
        Canvas.SetTop(EmptyHint, LineY(LineWidth / 2) + 22);
    }

    // --- Hover / copy -----------------------------------------------------------

    private void OnPhotoMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border { DataContext: FileJobViewModel job }) return;
        ShelfItem? item = FindItem(job);
        if (item is null || item.FallStartMs is not null) return;

        AnimateScale(item.CardScale, 1.035, 300);
        item.RemoveChip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        item.CrossScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        item.CrossScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        AnimateShadow(item, 26, 0.24);
    }

    private void OnPhotoMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border { DataContext: FileJobViewModel job }) return;
        HideHover(FindItem(job));
    }

    private void HideHover(ShelfItem? item)
    {
        if (item is null) return;

        AnimateScale(item.CardScale, 1, 300);
        item.RemoveChip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        item.CrossScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(150)));
        item.CrossScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(150)));
        AnimateShadow(item, 18, 0.16);
    }

    /// <summary>Regular inline .card box-shadow (0 5px 20px .08) versus the lifted hover one.</summary>
    private static void AnimateShadow(ShelfItem item, double blur, double opacity)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        item.Shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(blur, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        item.Shadow.BeginAnimation(DropShadowEffect.OpacityProperty,
            new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    /// <summary>
    /// .card's transition is cubic-bezier(.3, 1.6, .5, 1): the card overshoots by a hair and
    /// settles back. WPF key splines stay inside 0..1, so the overshoot is a separate key frame.
    /// </summary>
    private static void AnimateScale(ScaleTransform scale, double to, int milliseconds)
    {
        double from = scale.ScaleX;
        double overshoot = to + (to - from) * 0.22;

        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            overshoot, KeyTime.FromPercent(0.62), new KeySpline(0.15, 0.8, 0.35, 1)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            to, KeyTime.FromPercent(1), new KeySpline(0.2, 0.9, 0.3, 1)));

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void OnPhotoLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { DataContext: FileJobViewModel job }) return;
        ShelfItem? item = FindItem(job);
        if (item is null) return;

        item.CardScale.ScaleX = 1;
        item.CardScale.ScaleY = 1;
    }

    /// <summary>
    /// Line.copy(): the checkmark capsule pops in under the card for 1.2s and the peg swings 3°.
    /// Driven by its own timeline rather than the frame loop so feedback never depends on the
    /// strip's animation state.
    /// </summary>
    private void ShowCopied(ShelfItem item)
    {
        Border badge = item.CopiedBadge;
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200))));
        fade.Completed += (_, _) => badge.Visibility = Visibility.Collapsed;

        // Base value stays 1: should the strip ever be idle-rendered without a clock tick,
        // the feedback is still solid instead of an invisible zero-opacity capsule.
        badge.BeginAnimation(OpacityProperty, null);
        badge.Opacity = 1;
        badge.Visibility = Visibility.Visible;
        badge.BeginAnimation(OpacityProperty, fade);

        // translate(-50%, 4px) -> translate(-50%, 0), 0.2s ease-out.
        item.CopiedShift.BeginAnimation(TranslateTransform.YProperty, null);
        item.CopiedShift.Y = 4;
        item.CopiedShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    // --- Photo gestures ---------------------------------------------------------

    private void OnPhotoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: FileJobViewModel job }) return;
        ShelfItem? item = FindItem(job);
        if (item is null) return;

        StopLongPress();

        if (e.ClickCount >= 2)
        {
            job.OpenImageCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (item.CardScale.ScaleX > 0)
            AnimateScale(item.CardScale, 0.95, 450);

        _dragItem = job;
        _dragStart = e.GetPosition(this);
        _dragStarted = false;
        _longPressFired = false;
        _longPressJob = job;
        _longPressSource = sender as UIElement;
        _longPressTimer.Start();

        if (sender is UIElement element) element.CaptureMouse();
        e.Handled = true;
    }

    private void OnLongPressTick(object? sender, EventArgs e)
    {
        FileJobViewModel? job = _longPressJob;
        UIElement? source = _longPressSource;
        StopLongPress();
        if (job is null) return;

        _dragItem = null;
        _longPressFired = true;
        source?.ReleaseMouseCapture();
        AcknowledgePress(job);
        MarkupRequested?.Invoke(this, job);
    }

    private void StopLongPress()
    {
        _longPressTimer.Stop();
        _longPressJob = null;
        _longPressSource = null;
    }

    private void AcknowledgePress(FileJobViewModel job)
    {
        ShelfItem? item = FindItem(job);
        if (item is not null)
            AnimateScale(item.CardScale, item.Card.IsMouseOver ? 1.035 : 1, 300);
    }

    private void OnPhotoMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopLongPress();
            _dragItem = null;
            if (sender is UIElement released) released.ReleaseMouseCapture();
            return;
        }

        Point position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        StopLongPress();
        FileJobViewModel job = _dragItem;
        _dragItem = null;
        _dragStarted = true;
        if (sender is UIElement element) element.ReleaseMouseCapture();

        if (job.OutputPath is not { } path || !File.Exists(path))
        {
            _dragStarted = false;
            return;
        }

        ShelfItem? draggedItem = FindItem(job);
        if (draggedItem is not null)
        {
            draggedItem.Card.Opacity = 0.45;
            draggedItem.RemoveChip.BeginAnimation(OpacityProperty, null);
            draggedItem.RemoveChip.Opacity = 0;
        }

        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDropEffects effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            if (effect != DragDropEffects.None)
                RemoveQuietly(job);
        }
        catch (Exception ex)
        {
            Logging.AppLog.Warn($"晾衣绳拖出失败：{ex.Message}");
        }
        finally
        {
            _dragStarted = false;
            if (draggedItem is not null)
            {
                draggedItem.Card.Opacity = 1;
                draggedItem.CardScale.SetCurrentValue(ScaleTransform.ScaleXProperty, 1.0);
            }
        }

        e.Handled = true;
    }

    private void OnPhotoMouseUp(object sender, MouseButtonEventArgs e)
    {
        StopLongPress();
        if (sender is UIElement element) element.ReleaseMouseCapture();

        FileJobViewModel? job = _dragItem ?? (sender as FrameworkElement)?.DataContext as FileJobViewModel;
        _dragItem = null;
        bool dragged = _dragStarted;
        bool longPressed = _longPressFired;
        _dragStarted = false;
        _longPressFired = false;
        if (job is null || dragged || longPressed) return;

        // Feedback first: the capsule and the 3° swing must not depend on the clipboard call.
        ShelfItem? item = FindItem(job);
        if (item is not null)
        {
            AcknowledgePress(job);
            ShowCopied(item);
            StartNudge(item, 3);
        }

        job.CopyImageCommand.Execute(null);
        e.Handled = true;
    }

    private void OnRemovePreviewMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnRemoveClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileJobViewModel job })
        {
            ShelfItem? item = FindItem(job);
            if (item is not null) StartFall(item);
            _items.RemoveAll(i => i.Job == job);
            LayoutTargets(instant: false);
            _sagTarget = BaseSagForCount(_items.Count);
            UpdateEmptyHint();
        }

        e.Handled = true;
    }

    private void RemoveQuietly(FileJobViewModel job)
    {
        ShelfItem? item = FindItem(job);
        if (item is null) return;
        _items.Remove(item);
        ItemsLayer.Children.Remove(item.Root);
        StopSwing(item);
        LayoutTargets(instant: false);
        _sagTarget = BaseSagForCount(_items.Count);
    }

    private ShelfItem? FindItem(FileJobViewModel job) =>
        _items.FirstOrDefault(i => i.Job == job);

    // --- Helpers ---------------------------------------------------------------

    private static void ApplyPhotoClip(FrameworkElement photo)
    {
        double width = photo.ActualWidth;
        double height = photo.ActualHeight;
        if (width <= 0 || height <= 0) return;
        photo.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 12, 12);
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;

        // Template content is built as a logical tree before it is measured; VisualTreeHelper
        // can be empty at this point, so walk the logical children instead.
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependency) continue;
            T? found = FindNamed<T>(dependency, name);
            if (found is not null) return found;
        }

        return null;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in text) hash = hash * 31 + c;
            return hash & int.MaxValue;
        }
    }

    private sealed class ShelfItem
    {
        public required FileJobViewModel Job { get; init; }
        public required FrameworkElement Root { get; init; }
        public required Border Card { get; init; }
        public required Border RemoveChip { get; init; }
        public required Border CopiedBadge { get; init; }
        public required ScaleTransform CrossScale { get; init; }
        public required TranslateTransform CopiedShift { get; init; }
        public required ScaleTransform CardScale { get; init; }
        public required DropShadowEffect Shadow { get; init; }
        public required TranslateTransform Drop { get; init; }
        public required RotateTransform Rotate { get; init; }

        public double X;
        public double TargetX;
        public double XVelocity;
        public double Y;
        public double TargetY;
        public double YVelocity;
        public double Tilt;
        public double Theta;
        public double Omega;
        public double DropY;
        public double DropVelocity;
        public double Opacity;
        public long? FallStartMs;
        public double NudgeFrom;
        public double? NudgeTarget;
        public long? NudgeStartMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);
}
