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
using WatermarkRemover.App.Controls;
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
/// (截图设置 → 触发区域: four screen edges, each independently enabled). While the pointer is
/// inside a zone but the line has not slid down yet, a translucent cloud (TriggerZoneIndicator,
/// the same asset the settings mock uses) marks the zone so the gesture is discoverable.
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

    /// <summary>
    /// 指针轮询周期。旧实现是 Background 优先级的 DispatcherTimer、100ms：探针实测间隔
    /// 109-110ms，且在切应用瞬间整段饿死 1.18s（22:58:07.901→09.079 无任何轮询记录）——
    /// 「切到别的软件一直不收回，像卡住」的直接证据。现改为专用线程定时器 40ms，
    /// 每个周期以 Input 优先级切回 UI 线程，判定不再被渲染/后台工作拖后腿。
    /// </summary>
    private const int PointerPollMilliseconds = 40;

    /// <summary>热区停留多久弹出（旧值 250ms，叠加轮询量化后实测 314-332ms，「弹出慢」）。</summary>
    private const double RevealDelayMilliseconds = 200;

    /// <summary>鼠标离开多久收回（旧值 500ms，叠加轮询量化后实测 549-578ms，「收回慢」）。</summary>
    private const double RetractDelayMilliseconds = 200;

    private const double PeekMilliseconds = 2500;

    private readonly DataTemplate _photoTemplate;
    private readonly List<ShelfItem> _items = new();
    private readonly List<ShelfItem> _falling = new();
    private readonly List<FileJobViewModel> _initialJobs;
    private readonly DispatcherTimer _frameTimer;

    /// <summary>
    /// 指针轮询：跑在专用线程定时器上（不随 UI 线程忙碌而饿死），每个周期以 Input
    /// 优先级切回 UI 线程执行判定。旧的 DispatcherTimer 方案见
    /// <see cref="PointerPollMilliseconds"/> 的说明与探针日志。
    /// </summary>
    private readonly System.Threading.Timer _pointerTimer;

    private readonly DispatcherTimer _longPressTimer;
    private readonly DispatcherTimer _gustTimer;
    private readonly Random _random = new();

    /// <summary>触发区域配置（截图设置，四边独立）；设置保存后由宿主热更新。</summary>
    private ScreenshotTriggerSettings _trigger;

    /// <summary>上次轮询执行时刻（探针：UI 线程滞后证据——定时器准但执行被拖后）。</summary>
    private long _lastPointerExecMs = Environment.TickCount64;

    /// <summary>上次定时器周期时刻（探针：实测轮询间隔，验证线程定时器是否被饿死）。</summary>
    private long _lastTimerTickMs = Environment.TickCount64;

    /// <summary>最近一个定时器周期的实测间隔（ms），由轮询执行时读取用于探针。</summary>
    private long _lastPollIntervalMs = PointerPollMilliseconds;

    /// <summary>窗口句柄钩子（探针：激活/失焦/鼠标离开等系统消息）。</summary>
    private HwndSource? _hwndSource;

    /// <summary>触发区域「云朵」指示器：命中热区时浮出， lazily 创建后复用。</summary>
    private TriggerZoneIndicator? _zoneIndicator;

    /// <summary>截图会话进行中：热区整体静默——取景时鼠标会划过屏幕边缘，
    /// 晾衣绳和云朵都不该在那一刻弹出来（长截图还会反复截屏，污染拼接图）。</summary>
    private bool _captureSuppressed;

    /// <summary>已进入关闭流程：线程定时器回调直接退场，不再往调度器投递。</summary>
    private volatile bool _closed;

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

        _pointerTimer = new System.Threading.Timer(
            static state => ((ScreenshotShelfWindow)state!).OnPointerTimerTick(),
            this,
            TimeSpan.FromMilliseconds(PointerPollMilliseconds),
            TimeSpan.FromMilliseconds(PointerPollMilliseconds));

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
            _closed = true;
            _hwndSource?.RemoveHook(WndProc);
            _frameTimer.Stop();
            _pointerTimer.Dispose();
            _longPressTimer.Stop();
            _gustTimer.Stop();
            _zoneIndicator?.Close();
            foreach (ShelfItem item in _items)
                item.Root.ClearValue(RenderTransformProperty);
        };

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

        // 会话全程静默热区（配对调用见 ResumeAfterCapture）：云朵也不该浮出来。
        _captureSuppressed = true;
        HideZoneCloud();
        ShelfProbe.Log("截图会话开始：热区响应静默");
    }

    /// <summary>截图会话结束：恢复热区响应（<see cref="SuppressForCapture"/> 的配对调用）。</summary>
    public void ResumeAfterCapture()
    {
        if (!_captureSuppressed) return;
        _captureSuppressed = false;
        ShelfProbe.Log("截图会话结束：恢复热区响应");
    }

    /// <summary>设置保存后热更新触发区域（改配置不用重启）。</summary>
    public void UpdateTrigger(ScreenshotTriggerSettings trigger)
    {
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        ClampTrigger();
        HideZoneCloud(); // 云朵按旧配置定位已无意义，下一轮命中会按新配置重新浮出
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

        // 弹出态由晾衣绳本身占位，云朵退场（不压在绳子/照片上）。
        HideZoneCloud();

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

    /// <summary>
    /// 线程定时器周期：只负责「量间隔 + 切回 UI 线程」，判定全部在 <see cref="OnPointerPoll"/>。
    /// 线程定时器不随 UI 线程忙碌而饿死；投递用 Input 优先级，排在渲染之前。
    /// </summary>
    private void OnPointerTimerTick()
    {
        if (_closed) return;

        long now = Environment.TickCount64;
        _lastPollIntervalMs = now - _lastTimerTickMs;
        _lastTimerTickMs = now;

        try
        {
            Dispatcher.BeginInvoke(new Action(OnPointerPoll), DispatcherPriority.Input);
        }
        catch (Exception)
        {
            // 关闭竞态：调度器已停时没有可投递的队列，忽略即可（_closed 会拦住后续周期）。
        }
    }

    /// <summary>
    /// 指针判定（UI 线程，Input 优先级）。与旧版一致的状态机，三处变化：
    /// ① 截图会话中整体静默；② 命中热区时浮出云朵；③ 探针记录定时器实测周期与执行滞后。
    /// </summary>
    private void OnPointerPoll()
    {
        if (!GetCursorPos(out CursorPoint cursor)) return;

        // 探针：UI 线程滞后证据（定时器周期准、执行间隔被拉长 = 判定延迟来自 UI 线程）。
        long execNow = Environment.TickCount64;
        long execInterval = execNow - _lastPointerExecMs;
        _lastPointerExecMs = execNow;
        if (execInterval > PointerPollMilliseconds + 60)
        {
            ShelfProbe.Log(
                $"⚠ 轮询执行滞后：定时器 {_lastPollIntervalMs}ms，实际执行间隔 {execInterval}ms（UI 线程被占）");
        }

        long interval = _lastPollIntervalMs;
        DateTime now = DateTime.Now;

        if (!_revealed)
        {
            if (_captureSuppressed) return;

            ScreenEdge? hot = _trigger.HitTest(
                cursor.X, cursor.Y, _workArea.Left, _workArea.Top, _workArea.Width, _workArea.Height);
            bool gated = _manual || _dragStarted;

            // 云朵只看「命中哪条边」：命中即浮出（gated 时也浮，只是不弹晾衣绳）。
            if (hot is not null) ShowZoneCloud(hot.Value);
            else HideZoneCloud();

            if (hot is null || gated)
            {
                if (_topHotSince != DateTime.MinValue)
                    ShelfProbe.Log($"轮询间隔{interval}ms 光标({cursor.X},{cursor.Y}) 热区中断（manual={_manual} drag={_dragStarted}），持续计时清零");
                _topHotSince = DateTime.MinValue;
                return;
            }

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

            return;
        }

        // 弹出态由晾衣绳占位，云朵退场。
        HideZoneCloud();

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

    /// <summary>
    /// 系统层面通知「本软件不再是前台」：立即收回。探针证实 WM_ACTIVATEAPP 失活消息
    /// 到达及时（22:58:08.375 那条），旧代码只记日志不动作，收回全押在轮询上——
    /// 轮询被 UI 线程饿死时就表现为「切到别的软件一直不收回，像卡住」。
    /// 拖拽中的 OLE 模态循环会误报失活，排除；截图会话中窗口本就已收起，无需处理。
    /// </summary>
    private void HideFromSystem(string reason)
    {
        if (!_revealed || _dragStarted) return;
        ShelfProbe.Log($"立即收回（{reason}）");
        HideLine();
    }

    /// <summary>
    /// 系统通知指针离开窗口（WM_MOUSELEAVE / WPF MouseLeave）：立刻开始离开计时，
    /// 不等下一个轮询周期才发现——系统消息比轮询更早，且每轮还要再等最多 40ms。
    /// </summary>
    private void StartAwayCountdown(string reason)
    {
        if (!_revealed || _manual || _dragStarted || _awaySince != DateTime.MinValue) return;
        _awaySince = DateTime.Now;
        ShelfProbe.Log($"{reason}：开始离开计时（需{RetractDelayMilliseconds:F0}ms）");
    }

    /// <summary>命中热区：把云朵指示器挪到该边热区（屏幕坐标）并淡入；指示器只创建一次。</summary>
    private void ShowZoneCloud(ScreenEdge edge)
    {
        if (_trigger.ZoneRect(edge, _workArea.Width, _workArea.Height) is not { } rect) return;

        _zoneIndicator ??= new TriggerZoneIndicator();
        _zoneIndicator.ShowAt(new ScreenRect(
            rect.X + _workArea.Left,
            rect.Y + _workArea.Top,
            rect.Width,
            rect.Height));
    }

    private void HideZoneCloud() => _zoneIndicator?.HideCloud();

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

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        ShelfProbe.Log("WPF MouseLeave：指针离开晾衣绳窗口");
        StartAwayCountdown("WPF MouseLeave");
    }

    /// <summary>
    /// 系统消息探针 + 响应：激活/失焦/鼠标离开窗口。「切到其他应用不收回」的根因就是
    /// 失活消息收到了却没动作（探针第一轮已证实消息到达及时），现在收到即收回。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0006: // WM_ACTIVATE
                bool windowActive = wParam.ToInt32() != 0;
                ShelfProbe.Log($"WM_ACTIVATE 激活={windowActive}");
                if (!windowActive) HideFromSystem("WM_ACTIVATE 失活");
                break;
            case 0x001C: // WM_ACTIVATEAPP
                bool appActive = wParam != IntPtr.Zero;
                ShelfProbe.Log($"WM_ACTIVATEAPP 激活={appActive}");
                if (!appActive) HideFromSystem("WM_ACTIVATEAPP 失活");
                break;
            case 0x0008: // WM_KILLFOCUS
                ShelfProbe.Log($"WM_KILLFOCUS 新焦点窗口=0x{wParam.ToInt64():X8}");
                break;
            case 0x0021: // WM_MOUSEACTIVATE
                ShelfProbe.Log($"WM_MOUSEACTIVATE 命中测试={wParam.ToInt32()} 鼠标消息={(int)(lParam.ToInt64() & 0xFFFF):X4}");
                break;
            case 0x02A3: // WM_MOUSELEAVE
                ShelfProbe.Log("WM_MOUSELEAVE：系统通知指针离开窗口");
                StartAwayCountdown("WM_MOUSELEAVE");
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
