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
}
