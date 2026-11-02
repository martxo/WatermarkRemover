namespace WatermarkRemover.Core.Features.Screenshot;

/// <summary>屏幕的一条边。</summary>
public enum ScreenEdge
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>
/// 一条边上的触发区域（热区）：是否启用、伸入屏幕的厚度、沿边长度、中心沿边的位置
/// （0..1，0.5 = 居中）。单位是屏幕像素，位置用比例表示，换分辨率/换显示器都不会错位。
/// </summary>
public sealed class TriggerZoneEdge
{
    /// <summary>热区伸入屏幕的深度（上/下边为高度，左/右边为宽度）。</summary>
    public int Thickness { get; set; } = ScreenshotTriggerSettings.DefaultThickness;

    /// <summary>热区沿边的长度（上/下边为宽度，左/右边为高度）。</summary>
    public int Length { get; set; } = ScreenshotTriggerSettings.DefaultLength;

    /// <summary>热区中心沿边的位置，0 = 靠边起点（左/上），1 = 靠边终点（右/下）。</summary>
    public double Position { get; set; } = 0.5;

    public bool Enabled { get; set; }

    public TriggerZoneEdge Clone() => new()
    {
        Thickness = Thickness,
        Length = Length,
        Position = Position,
        Enabled = Enabled,
    };
}

/// <summary>
/// 晾衣绳（最近截图）触发区域配置：上/下/左/右四条边各自独立启用，每条边一个矩形热区，
/// 鼠标移入热区停留片刻即滑出晾衣绳。默认只启用上边、居中——与旧版「顶部 32px 热区」
/// 的触发方式一致，只是位置和大小可配。
/// 坐标一律相对工作区（不含任务栏），由调用方按当前工作区尺寸解释。
/// </summary>
public sealed class ScreenshotTriggerSettings
{
    /// <summary>默认厚度：与旧版硬编码的顶部菜单栏热区高度一致。</summary>
    public const int DefaultThickness = 32;

    /// <summary>默认沿边长度：顶部居中热区的宽度。</summary>
    public const int DefaultLength = 560;

    public const int MinThickness = 4;

    public const int MaxThickness = 240;

    public const int MinLength = 80;

    /// <summary>上边热区；默认启用并居中。</summary>
    public TriggerZoneEdge Top { get; set; } = new() { Enabled = true };

    public TriggerZoneEdge Bottom { get; set; } = new();

    public TriggerZoneEdge Left { get; set; } = new();

    public TriggerZoneEdge Right { get; set; } = new();

    public TriggerZoneEdge Of(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Top => Top,
        ScreenEdge.Bottom => Bottom,
        ScreenEdge.Left => Left,
        ScreenEdge.Right => Right,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    public ScreenshotTriggerSettings Clone() => new()
    {
        Top = Top.Clone(),
        Bottom = Bottom.Clone(),
        Left = Left.Clone(),
        Right = Right.Clone(),
    };

    /// <summary>
    /// 把配置收敛到合法范围：厚度/长度不超对应边的尺寸，位置保证热区完整落在边上
    /// （长度占满整条边时位置归中）。设置界面与运行时都会调用，手改配置文件也不会越界。
    /// </summary>
    public void Clamp(double workWidth, double workHeight)
    {
        if (double.IsNaN(workWidth) || workWidth < 1) workWidth = 1;
        if (double.IsNaN(workHeight) || workHeight < 1) workHeight = 1;

        ClampEdge(Top, workWidth, workHeight, crossByHeight: true);
        ClampEdge(Bottom, workWidth, workHeight, crossByHeight: true);
        ClampEdge(Left, workWidth, workHeight, crossByHeight: false);
        ClampEdge(Right, workWidth, workHeight, crossByHeight: false);
    }

    /// <summary>
    /// 工作区坐标系（左上角为原点）下某条边热区的矩形；该边未启用返回 null。
    /// 调用前应先 <see cref="Clamp"/>，否则越界配置会产生伸出屏幕外的矩形。
    /// </summary>
    public ScreenRect? ZoneRect(ScreenEdge edge, double workWidth, double workHeight)
    {
        TriggerZoneEdge zone = Of(edge);
        if (!zone.Enabled) return null;

        double span = edge is ScreenEdge.Top or ScreenEdge.Bottom ? workWidth : workHeight;
        double centre = zone.Position * span;
        double half = zone.Length / 2.0;
        double start = centre - half;
        // 收敛后 start 本就在 [0, span-length]；双保险，防止漏调 Clamp。
        start = Math.Clamp(start, 0, Math.Max(0, span - zone.Length));

        return edge switch
        {
            ScreenEdge.Top => new ScreenRect(start, 0, zone.Length, zone.Thickness),
            ScreenEdge.Bottom => new ScreenRect(start, workHeight - zone.Thickness, zone.Length, zone.Thickness),
            ScreenEdge.Left => new ScreenRect(0, start, zone.Thickness, zone.Length),
            ScreenEdge.Right => new ScreenRect(workWidth - zone.Thickness, start, zone.Thickness, zone.Length),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }

    /// <summary>
    /// 光标（屏幕坐标）是否落在任一启用边的热区内；返回命中的边，无命中返回 null。
    /// 多条边同时启用时按上→下→左→右取第一个命中（热区相邻时以先配的先占）。
    /// </summary>
    public ScreenEdge? HitTest(
        double cursorX, double cursorY, double workLeft, double workTop, double workWidth, double workHeight)
    {
        double x = cursorX - workLeft;
        double y = cursorY - workTop;
        if (x < 0 || y < 0 || x >= workWidth || y >= workHeight) return null;

        foreach (ScreenEdge edge in new[] { ScreenEdge.Top, ScreenEdge.Bottom, ScreenEdge.Left, ScreenEdge.Right })
        {
            if (Of(edge).Enabled && ZoneRect(edge, workWidth, workHeight) is { } rect && rect.Contains(x, y))
                return edge;
        }

        return null;
    }

    private static void ClampEdge(TriggerZoneEdge zone, double workWidth, double workHeight, bool crossByHeight)
    {
        double span = crossByHeight ? workWidth : workHeight;
        double cross = crossByHeight ? workHeight : workWidth;

        zone.Thickness = (int)Math.Clamp(zone.Thickness, MinThickness, Math.Min(MaxThickness, cross));
        zone.Length = (int)Math.Clamp(zone.Length, MinLength, span);

        double half = zone.Length / 2.0;
        double minPosition = span > 0 ? half / span : 0;
        double maxPosition = 1 - minPosition;
        zone.Position = minPosition >= maxPosition
            ? 0.5
            : Math.Clamp(zone.Position, minPosition, maxPosition);
    }
}

/// <summary>工作区坐标系下的轴对齐矩形（不依赖 WPF，Core 与测试可直接使用）。</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) =>
        x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}
