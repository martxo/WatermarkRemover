using System.Windows;
using WatermarkRemover.App.Controls;

namespace WatermarkRemover.Tests.Controls;

/// <summary>
/// 图片平面 ↔ 位图像素换算：看图器与 PDF 页宿主的标注坐标全靠它，旋转反算错一度
/// 标注就整体偏移，所以四个角度 + 往返一致性都钉死。
/// </summary>
public class PlaneMappingTests
{
    private const int Width = 400;
    private const int Height = 300;
    private const double Scale = 0.5;

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Plane_and_pixel_round_trip_at_every_quarter(double angle)
    {
        foreach ((double x, double y) in new[]
        {
            (0d, 0d), (399d, 299d), (123d, 45d), (399d, 0d), (0d, 299d),
        })
        {
            Point pixel = new(x, y);
            Point plane = PlaneMapping.ToPlane(angle, Width, Height, Scale, pixel);
            Point back = PlaneMapping.ToPixel(angle, Width, Height, Scale, plane);

            Assert.Equal(x, back.X, 3);
            Assert.Equal(y, back.Y, 3);
        }
    }

    [Fact]
    public void No_rotation_is_a_plain_scale()
    {
        Assert.Equal(new Point(50, 30), PlaneMapping.ToPlane(0, Width, Height, Scale, new Point(100, 60)));
        Assert.Equal(new Point(100, 60), PlaneMapping.ToPixel(0, Width, Height, Scale, new Point(50, 30)));
    }

    /// <summary>90° 顺时针：原图 (x,y) 落在旋转后位图的 (H-1-y, x)。</summary>
    [Fact]
    public void Quarter_turn_maps_top_right_to_bottom_right()
    {
        // 原图右上角 (399,0) → 旋转后右下角 (299,399)。
        Point plane = PlaneMapping.ToPlane(90, Width, Height, Scale, new Point(399, 0));
        Assert.Equal(299 * Scale, plane.X, 3);
        Assert.Equal(399 * Scale, plane.Y, 3);

        // 反向：平面右下角 → 原图右上角。
        Point pixel = PlaneMapping.ToPixel(90, Width, Height, Scale, new Point(299 * Scale, 399 * Scale));
        Assert.Equal(399, pixel.X, 3);
        Assert.Equal(0, pixel.Y, 3);
    }

    /// <summary>180°：两轴都翻转。</summary>
    [Fact]
    public void Half_turn_flips_both_axes()
    {
        Point plane = PlaneMapping.ToPlane(180, Width, Height, Scale, new Point(0, 0));
        Assert.Equal(399 * Scale, plane.X, 3);
        Assert.Equal(299 * Scale, plane.Y, 3);
    }

    /// <summary>270°（逆时针 90°）：原图 (x,y) 落在 (y, W-1-x)。</summary>
    [Fact]
    public void Three_quarter_turn_maps_top_left_to_bottom_left()
    {
        // 原图左上角 (0,0) → 旋转后左下角 (0, 399)。
        Point plane = PlaneMapping.ToPlane(270, Width, Height, Scale, new Point(0, 0));
        Assert.Equal(0, plane.X, 3);
        Assert.Equal(399 * Scale, plane.Y, 3);

        Point pixel = PlaneMapping.ToPixel(270, Width, Height, Scale, new Point(0, 399 * Scale));
        Assert.Equal(0, pixel.X, 3);
        Assert.Equal(0, pixel.Y, 3);
    }

    /// <summary>负角/超过 360 的角度按模归一，宿主旋转累加不会算错。</summary>
    [Fact]
    public void Angles_are_normalised()
    {
        Point a = PlaneMapping.ToPlane(-90, Width, Height, Scale, new Point(10, 20));
        Point b = PlaneMapping.ToPlane(270, Width, Height, Scale, new Point(10, 20));
        Assert.Equal(a, b);

        Point c = PlaneMapping.ToPlane(450, Width, Height, Scale, new Point(10, 20));
        Point d = PlaneMapping.ToPlane(90, Width, Height, Scale, new Point(10, 20));
        Assert.Equal(c, d);
    }

    /// <summary>缩放非正（无图）时返回原点，不让宿主拿到 NaN。</summary>
    [Fact]
    public void Non_positive_scale_returns_origin()
    {
        Assert.Equal(default, PlaneMapping.ToPixel(0, Width, Height, 0, new Point(10, 10)));
    }
}
