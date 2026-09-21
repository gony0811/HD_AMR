using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HD.AMR.App.Models;

namespace HD.AMR.Desktop.Controls;

/// <summary>맵, 라이다 점과 AMR 포즈를 한 번의 render pass로 그린다.</summary>
public sealed class AmrMapCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> MapImageProperty =
        AvaloniaProperty.Register<AmrMapCanvas, Bitmap?>(nameof(MapImage));
    public static readonly StyledProperty<IReadOnlyList<AmrLidarPoint>?> LidarPointsProperty =
        AvaloniaProperty.Register<AmrMapCanvas, IReadOnlyList<AmrLidarPoint>?>(nameof(LidarPoints));
    public static readonly StyledProperty<double> ResolutionProperty =
        AvaloniaProperty.Register<AmrMapCanvas, double>(nameof(Resolution), 0.05);
    public static readonly StyledProperty<double> OriginXProperty =
        AvaloniaProperty.Register<AmrMapCanvas, double>(nameof(OriginX));
    public static readonly StyledProperty<double> OriginYProperty =
        AvaloniaProperty.Register<AmrMapCanvas, double>(nameof(OriginY));
    public static readonly StyledProperty<double> RobotXProperty =
        AvaloniaProperty.Register<AmrMapCanvas, double>(nameof(RobotX));
    public static readonly StyledProperty<double> RobotYProperty =
        AvaloniaProperty.Register<AmrMapCanvas, double>(nameof(RobotY));
    public static readonly StyledProperty<double> RobotHeadingProperty =
        AvaloniaProperty.Register<AmrMapCanvas, double>(nameof(RobotHeading));
    public static readonly StyledProperty<bool> ShowRobotProperty =
        AvaloniaProperty.Register<AmrMapCanvas, bool>(nameof(ShowRobot));

    private static readonly IBrush LidarBrush = new SolidColorBrush(Color.FromArgb(220, 255, 74, 74));
    private static readonly IBrush RobotBrush = new SolidColorBrush(Color.FromRgb(22, 139, 255));
    private static readonly Pen RobotOutline = new(Brushes.White, 1.5);
    private static readonly Pen HeadingPen = new(Brushes.White, 2.5);
    private static readonly IBrush OriginBrush = new SolidColorBrush(Color.FromRgb(255, 196, 64));
    private static readonly Pen OriginPen = new(OriginBrush, 2);
    private static readonly IBrush AxisXBrush = new SolidColorBrush(Color.FromRgb(255, 92, 92));
    private static readonly IBrush AxisYBrush = new SolidColorBrush(Color.FromRgb(86, 220, 132));
    private static readonly Pen AxisXPen = new(AxisXBrush, 2);
    private static readonly Pen AxisYPen = new(AxisYBrush, 2);
    private static readonly Pen OriginGuidePen = new(OriginBrush, 1);
    private static readonly Typeface LabelTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    static AmrMapCanvas()
    {
        AffectsRender<AmrMapCanvas>(MapImageProperty, LidarPointsProperty, ResolutionProperty,
            OriginXProperty, OriginYProperty, RobotXProperty, RobotYProperty,
            RobotHeadingProperty, ShowRobotProperty);
    }

    public Bitmap? MapImage { get => GetValue(MapImageProperty); set => SetValue(MapImageProperty, value); }
    public IReadOnlyList<AmrLidarPoint>? LidarPoints { get => GetValue(LidarPointsProperty); set => SetValue(LidarPointsProperty, value); }
    public double Resolution { get => GetValue(ResolutionProperty); set => SetValue(ResolutionProperty, value); }
    public double OriginX { get => GetValue(OriginXProperty); set => SetValue(OriginXProperty, value); }
    public double OriginY { get => GetValue(OriginYProperty); set => SetValue(OriginYProperty, value); }
    public double RobotX { get => GetValue(RobotXProperty); set => SetValue(RobotXProperty, value); }
    public double RobotY { get => GetValue(RobotYProperty); set => SetValue(RobotYProperty, value); }
    public double RobotHeading { get => GetValue(RobotHeadingProperty); set => SetValue(RobotHeadingProperty, value); }
    public bool ShowRobot { get => GetValue(ShowRobotProperty); set => SetValue(ShowRobotProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var image = MapImage;
        if (image is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        // Render 좌표는 이미 이 컨트롤의 좌상단이 (0, 0)이다. 부모 기준 Bounds.X/Y를
        // 목적지에 다시 사용하면 배경 맵만 그만큼 밀리고, 로컬 좌표인 라이다/로봇과 분리된다.
        var localBounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var sx = Bounds.Width / image.PixelSize.Width;
        var sy = Bounds.Height / image.PixelSize.Height;
        // 점유 격자 맵은 1px 벽이 많다. 확대 시 보간하면 벽이 번지고, 축소 시 저품질 보간은 벽이 끊겨 보인다.
        var interpolation = Math.Min(sx, sy) >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation }))
            context.DrawImage(image, new Rect(image.Size), localBounds);
        if (Resolution <= 0) return;

        DrawWorldOrigin(context, image.PixelSize.Width, image.PixelSize.Height, sx, sy);

        if (LidarPoints is { } points)
        {
            var radius = Math.Clamp(Math.Min(sx, sy) * 1.15, 1.0, 3.0);
            foreach (var point in points)
            {
                if (MapProjection.TryProjectPoint(point.X, point.Y, Resolution, OriginX, OriginY,
                        image.PixelSize.Width, image.PixelSize.Height, out var px, out var py))
                    context.DrawEllipse(LidarBrush, null, new Point(px * sx, py * sy), radius, radius);
            }
        }

        if (!ShowRobot) return;
        if (!MapProjection.TryProjectPoint(RobotX, RobotY, Resolution, OriginX, OriginY,
                image.PixelSize.Width, image.PixelSize.Height, out var robotPx, out var robotPy)) return;
        var robotX = robotPx * sx;
        var robotY = robotPy * sy;
        var robotRadius = Math.Clamp(Math.Min(sx, sy) * 7, 7, 14);
        context.DrawEllipse(RobotBrush, RobotOutline, new Point(robotX, robotY), robotRadius, robotRadius);
        var arrowLength = robotRadius * 1.5;
        var end = new Point(robotX + Math.Cos(RobotHeading) * arrowLength,
            robotY - Math.Sin(RobotHeading) * arrowLength);
        context.DrawLine(HeadingPen, new Point(robotX, robotY), end);
    }

    /// <summary>
    /// 월드 좌표계의 (0,0)과 양의 축을 표시한다. 이미지 Y축은 아래로 증가하므로
    /// 월드 +Y 화살표는 화면 위쪽을 향한다.
    /// </summary>
    private void DrawWorldOrigin(DrawingContext context, int imageWidth, int imageHeight, double sx, double sy)
    {
        if (!MapProjection.TryProjectPoint(0, 0, Resolution, OriginX, OriginY,
                imageWidth, imageHeight, out var originPx, out var originPy)) return;

        var projectedOrigin = new Point(originPx * sx, originPy * sy);
        // 원점이 이미지 경계에 있으면 링과 글자가 컨트롤 밖에서 잘린다. 실제 위치에는 연결선을
        // 남기고, 축 마커만 안쪽으로 옮겨 좌하단 원점도 온전히 보이게 한다.
        const double inset = 13;
        var origin = new Point(
            Math.Clamp(projectedOrigin.X, inset, Math.Max(inset, Bounds.Width - inset)),
            Math.Clamp(projectedOrigin.Y, inset, Math.Max(inset, Bounds.Height - inset)));
        const double axisLength = 46;
        const double arrow = 6;
        var xEnd = new Point(Math.Min(Bounds.Width - 3, origin.X + axisLength), origin.Y);
        var yEnd = new Point(origin.X, Math.Max(3, origin.Y - axisLength));

        if (origin != projectedOrigin)
            context.DrawLine(OriginGuidePen, projectedOrigin, origin);

        // 원점 십자와 링은 배경색과 무관하게 좌표 (0,0)을 찾기 쉽게 한다.
        context.DrawEllipse(null, OriginPen, origin, 7, 7);
        context.DrawLine(OriginPen, new Point(origin.X - 10, origin.Y), new Point(origin.X + 10, origin.Y));
        context.DrawLine(OriginPen, new Point(origin.X, origin.Y - 10), new Point(origin.X, origin.Y + 10));

        context.DrawLine(AxisXPen, origin, xEnd);
        context.DrawLine(AxisXPen, xEnd, new Point(xEnd.X - arrow, xEnd.Y - arrow / 2));
        context.DrawLine(AxisXPen, xEnd, new Point(xEnd.X - arrow, xEnd.Y + arrow / 2));
        context.DrawLine(AxisYPen, origin, yEnd);
        context.DrawLine(AxisYPen, yEnd, new Point(yEnd.X - arrow / 2, yEnd.Y + arrow));
        context.DrawLine(AxisYPen, yEnd, new Point(yEnd.X + arrow / 2, yEnd.Y + arrow));

        DrawLabel(context, "(0,0)", OriginBrush, new Point(origin.X + 8, origin.Y + 6));
        DrawLabel(context, "+X · 0°", AxisXBrush, new Point(xEnd.X + 3, xEnd.Y - 17));
        DrawLabel(context, "+Y · +90°", AxisYBrush, new Point(yEnd.X + 5, yEnd.Y - 7));
    }

    private void DrawLabel(DrawingContext context, string text, IBrush brush, Point point)
    {
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, 11, brush);
        // 우측/상하 경계에서도 텍스트 전체가 캔버스 안에 남도록 최종 위치를 제한한다.
        var x = Math.Clamp(point.X, 2, Math.Max(2, Bounds.Width - label.Width - 2));
        var y = Math.Clamp(point.Y, 2, Math.Max(2, Bounds.Height - label.Height - 2));
        context.DrawText(label, new Point(x, y));
    }
}
