using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// 검사 포인트 X-Y 캔버스(auto-fit). 기존 InspectionPoints.razor 의 SVG 뷰어를 네이티브 렌더로 이식.
/// 플롯 좌표는 py = -Y(화면 아래 = 세계 Y 위). <see cref="Update"/> 로 데이터를 넣으면 다시 그린다.
/// </summary>
public sealed class XyPlot : Control
{
    public readonly record struct PlotPoint(double X, double Y, double Z, bool Selected);

    private IReadOnlyList<PlotPoint> _pts = Array.Empty<PlotPoint>();
    private double _fov = 30, _minX = -50, _minY = -50, _w = 100, _h = 100;

    // 다크 캔버스(ACS UI 통일) 위에서 시인성이 나오도록 조정한 팔레트.
    private static readonly IBrush CanvasBg = new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x18));
    private static readonly IBrush AxisX = new SolidColorBrush(Color.FromRgb(0xB0, 0x6A, 0x6A));
    private static readonly IBrush AxisY = new SolidColorBrush(Color.FromRgb(0x5A, 0x7A, 0x9A));
    private static readonly IBrush SelStroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x7F, 0xFF));
    private static readonly IBrush SelFill = new SolidColorBrush(Color.FromArgb(45, 0x1E, 0x7F, 0xFF));
    private static readonly IBrush DotSel = new SolidColorBrush(Color.FromRgb(0xE4, 0x50, 0x60));
    private static readonly IBrush Dot = new SolidColorBrush(Color.FromRgb(0x4C, 0x99, 0xFF));
    private static readonly IBrush BoxStroke = new SolidColorBrush(Color.FromRgb(0x66, 0x6C, 0x74));
    private static readonly IBrush BoxFill = new SolidColorBrush(Color.FromArgb(20, 0xA0, 0xA6, 0xAE));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8));
    private static readonly Typeface Face = new(FontFamily.Default);

    public void Update(IReadOnlyList<PlotPoint> pts, double fov, double minX, double minY, double w, double h)
    {
        _pts = pts; _fov = fov; _minX = minX; _minY = minY; _w = w; _h = h;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds.Size;
        // 다크 배경(ACS UI 통일) — 축/점/라벨은 위 다크 대비 팔레트로 그린다.
        ctx.FillRectangle(CanvasBg, new Rect(b));
        if (b.Width <= 0 || b.Height <= 0 || _w <= 0 || _h <= 0) return;

        var scale = Math.Min(b.Width / _w, b.Height / _h);
        var offX = (b.Width - _w * scale) / 2;
        var offY = (b.Height - _h * scale) / 2;

        Point S(double px, double py) => new(offX + (px - _minX) * scale, offY + (py - _minY) * scale);

        // 축선(플롯 y=0 = X축, 플롯 x=0 = Y축).
        ctx.DrawLine(new Pen(AxisX, 1), S(_minX, 0), S(_minX + _w, 0));
        ctx.DrawLine(new Pen(AxisY, 1), S(0, _minY), S(0, _minY + _h));

        var half = _fov / 2.0;
        for (var i = 0; i < _pts.Count; i++)
        {
            var p = _pts[i];
            double px = p.X, py = -p.Y;
            var tl = S(px - half, py - half);
            var br = S(px + half, py + half);
            var rect = new Rect(tl, br);
            ctx.DrawRectangle(p.Selected ? SelFill : BoxFill, new Pen(p.Selected ? SelStroke : BoxStroke, 1.5), rect);

            var c = S(px, py);
            ctx.DrawEllipse(p.Selected ? DotSel : Dot, null, c, 3.5, 3.5);

            var ft = new FormattedText($"#{i + 1} (Z{p.Z.ToString("0.###", CultureInfo.InvariantCulture)})",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, LabelBrush);
            ctx.DrawText(ft, new Point(br.X + 4, c.Y - ft.Height / 2));
        }
    }
}
