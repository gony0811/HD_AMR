using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// 가스 측정 결과 그래프 — 가로축은 측정 순서(왼쪽이 오래된 것), 세로축은 측정값(구간 최대값).
/// 임계값을 빨간 점선으로 긋고 점은 OK 초록·NG 빨강·실패 회색(아래쪽 ×)으로 칠한다. 재측정은 속이 빈 원.
/// 값 범위가 50배 넘게 벌어지면 세로축을 로그로 바꾼다. 마우스를 올리면 가장 가까운 점의 내용을 보여준다.
/// </summary>
public sealed class GasResultChart : Control
{
    public enum Judge { Ok, Ng, Error }

    public sealed record ChartPoint(int Seq, string Label, string Time, int Retry, double? Value, double Threshold,
        string Unit, Judge Judgement, string? Error);

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<GasResultChart, IReadOnlyList<ChartPoint>?>(nameof(Points));

    public IReadOnlyList<ChartPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    static GasResultChart() => AffectsRender<GasResultChart>(PointsProperty);

    private static readonly IBrush Bg = new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x18));
    private static readonly IBrush Grid = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x36));
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x7A, 0x9A));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly IBrush NgBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly IBrush ErrBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly IBrush TipBg = new SolidColorBrush(Color.FromArgb(235, 0x1F, 0x23, 0x2B));
    private static readonly IBrush TipText = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
    private static readonly Typeface Face = new(FontFamily.Default);

    private const double PadL = 64, PadR = 16, PadT = 14, PadB = 30;

    private Point? _hover;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    /// <summary>세로축 범위와 로그 여부 — 값·임계값을 모두 담는다.</summary>
    internal static (double Lo, double Hi, bool Log) YRange(IReadOnlyList<ChartPoint> pts)
    {
        var vals = pts.Where(p => p.Value is { } v && double.IsFinite(v)).Select(p => p.Value!.Value).ToList();
        vals.AddRange(pts.Select(p => p.Threshold).Where(t => double.IsFinite(t) && t > 0));
        if (vals.Count == 0) return (0, 1, false);
        var max = vals.Max();
        var minPos = vals.Where(v => v > 0).DefaultIfEmpty(0).Min();
        if (minPos > 0 && max / minPos > 50 && vals.All(v => v > 0))
        {
            var lo = Math.Pow(10, Math.Floor(Math.Log10(minPos)));
            var hi = Math.Pow(10, Math.Ceiling(Math.Log10(max)));
            if (hi <= lo) hi = lo * 10;
            return (lo, hi, true);
        }
        var top = max <= 0 ? 1 : max * 1.15;
        return (0, top, false);
    }

    private static string Fmt(double v) => v == 0 ? "0"
        : Math.Abs(v) >= 0.01 && Math.Abs(v) < 10000 ? v.ToString("0.###", CultureInfo.InvariantCulture)
        : v.ToString("0.0E+0", CultureInfo.InvariantCulture);

    private static FormattedText Text(string s, IBrush b, double size = 11)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, b);

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Bg, new Rect(size));
        var pts = Points ?? Array.Empty<ChartPoint>();
        if (size.Width < PadL + PadR + 20 || size.Height < PadT + PadB + 20) return;
        if (pts.Count == 0)
        {
            var t = Text("측정 결과 없음", Muted, 13);
            ctx.DrawText(t, new Point((size.Width - t.Width) / 2, (size.Height - t.Height) / 2));
            return;
        }

        var plot = new Rect(PadL, PadT, size.Width - PadL - PadR, size.Height - PadT - PadB);
        var (lo, hi, log) = YRange(pts);
        double Ny(double v)
        {
            double f = log ? (Math.Log10(Math.Max(v, lo)) - Math.Log10(lo)) / (Math.Log10(hi) - Math.Log10(lo))
                           : (v - lo) / (hi - lo);
            return plot.Bottom - Math.Clamp(f, 0, 1) * plot.Height;
        }
        double Nx(int i) => pts.Count == 1 ? plot.Center.X : plot.Left + plot.Width * i / (pts.Count - 1);

        // 세로축 눈금
        var ticks = new List<double>();
        if (log) for (var v = lo; v <= hi * 1.0001; v *= 10) ticks.Add(v);
        else for (var k = 0; k <= 4; k++) ticks.Add(lo + (hi - lo) * k / 4);
        var gridPen = new Pen(Grid, 1);
        foreach (var v in ticks)
        {
            var y = Ny(v);
            ctx.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(Fmt(v), Muted);
            ctx.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
        }
        var unit = pts[^1].Unit;
        ctx.DrawText(Text(log ? $"{unit} (로그)" : unit, Muted, 10), new Point(4, 0));

        // 가로축 — 측정 순번(약 8개만)
        var step = Math.Max(1, (int)Math.Ceiling(pts.Count / 8.0));
        for (var i = 0; i < pts.Count; i += step)
        {
            var t = Text($"#{pts[i].Seq}", Muted, 10);
            ctx.DrawText(t, new Point(Nx(i) - t.Width / 2, plot.Bottom + 6));
        }

        // 임계값(구간마다 바뀔 수 있으니 계단으로)
        var thPen = new Pen(NgBrush, 1.5, dashStyle: new DashStyle(new double[] { 4, 3 }, 0));
        for (var i = 0; i < pts.Count; i++)
        {
            var x0 = i == 0 ? plot.Left : (Nx(i - 1) + Nx(i)) / 2;
            var x1 = i == pts.Count - 1 ? plot.Right : (Nx(i) + Nx(i + 1)) / 2;
            var y = Ny(pts[i].Threshold);
            ctx.DrawLine(thPen, new Point(x0, y), new Point(x1, y));
        }
        var thLabel = Text($"임계 {Fmt(pts[^1].Threshold)}", NgBrush, 10);
        ctx.DrawText(thLabel, new Point(plot.Right - thLabel.Width, Ny(pts[^1].Threshold) - thLabel.Height - 2));

        // 값 선
        var linePen = new Pen(LineBrush, 1.2);
        Point? prev = null;
        for (var i = 0; i < pts.Count; i++)
        {
            if (pts[i].Value is not { } v) { prev = null; continue; }
            var p = new Point(Nx(i), Ny(v));
            if (prev is { } q) ctx.DrawLine(linePen, q, p);
            prev = p;
        }

        // 점
        var r = pts.Count > 120 ? 2.5 : 4.0;
        for (var i = 0; i < pts.Count; i++)
        {
            var pt = pts[i];
            if (pt.Value is not { } v || pt.Judgement == Judge.Error)
            {
                var x = Nx(i); var y = plot.Bottom - 6;
                var pen = new Pen(ErrBrush, 1.5);
                ctx.DrawLine(pen, new Point(x - r, y - r), new Point(x + r, y + r));
                ctx.DrawLine(pen, new Point(x - r, y + r), new Point(x + r, y - r));
                continue;
            }
            var brush = pt.Judgement == Judge.Ng ? NgBrush : OkBrush;
            var c = new Point(Nx(i), Ny(v));
            if (pt.Retry > 0) ctx.DrawEllipse(null, new Pen(brush, 1.5), c, r, r);
            else ctx.DrawEllipse(brush, null, c, r, r);
        }

        DrawTooltip(ctx, pts, plot, Nx, Ny);
    }

    private void DrawTooltip(DrawingContext ctx, IReadOnlyList<ChartPoint> pts, Rect plot,
        Func<int, double> nx, Func<double, double> ny)
    {
        if (_hover is not { } h || !plot.Inflate(8).Contains(h)) return;
        var best = 0;
        for (var i = 1; i < pts.Count; i++)
            if (Math.Abs(nx(i) - h.X) < Math.Abs(nx(best) - h.X)) best = i;
        var p = pts[best];
        var x = nx(best);
        ctx.DrawLine(new Pen(Muted, 1, dashStyle: new DashStyle(new double[] { 2, 3 }, 0)),
            new Point(x, plot.Top), new Point(x, plot.Bottom));

        var judge = p.Judgement switch { Judge.Ok => "OK", Judge.Ng => "NG", _ => "실패" };
        var lines = new List<string>
        {
            $"#{p.Seq}  {p.Time}  {judge}{(p.Retry > 0 ? $" (재측정 {p.Retry})" : "")}",
            p.Label,
            p.Value is { } v ? $"측정 {Fmt(v)} {p.Unit} / 임계 {Fmt(p.Threshold)}" : $"측정 실패: {p.Error ?? "값 없음"}",
        };
        var texts = lines.Select(s => Text(s, TipText)).ToList();
        var w = texts.Max(t => t.Width) + 16;
        var hgt = texts.Sum(t => t.Height) + 12;
        var bx = x + 12 + w > Bounds.Width ? x - 12 - w : x + 12;
        var by = Math.Clamp(h.Y - hgt / 2, 2, Bounds.Height - hgt - 2);
        ctx.DrawRectangle(TipBg, new Pen(Grid, 1), new Rect(bx, by, w, hgt), 4, 4);
        var ty = by + 6;
        foreach (var t in texts)
        {
            ctx.DrawText(t, new Point(bx + 8, ty));
            ty += t.Height;
        }
    }
}
