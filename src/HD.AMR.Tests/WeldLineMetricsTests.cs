using HD.AMR.App.Models;
using HD.AMR.App.Service.Vision;

namespace HD.AMR.Tests;

public class WeldLineMetricsTests
{
    private static WeldLineLabel Gt(double y0 = 200, double y1 = 200) => new()
    {
        Width = 848, Height = 480, Thickness = 9,
        Points = new() { new[] { 0.0, y0 }, new[] { 847.0, y1 } },
    };

    private static List<PixelPoint> Line(double y0, double y1, int x0 = 0, int x1 = 847)
        => Enumerable.Range(x0, x1 - x0 + 1).Select(x => new PixelPoint(x, y0 + (y1 - y0) * (x - x0) / (x1 - x0))).ToList();

    [Fact]
    public void 정답과_같으면_오차_0_합격()
    {
        var e = WeldLineMetrics.Compare(Gt(), Line(200, 200), horizontal: true);
        Assert.True(e.Detected);
        Assert.True(e.Pass);
        Assert.Equal(0, e.AngleDeg, 3);
        Assert.Equal(0, e.CenterOffsetPx, 3);
        Assert.Equal(1.0, e.Coverage, 2);
    }

    [Fact]
    public void 평행이동은_중심오차로만_나타난다()
    {
        var e = WeldLineMetrics.Compare(Gt(), Line(215, 215), horizontal: true);
        Assert.Equal(0, e.AngleDeg, 3);
        Assert.Equal(15, e.CenterOffsetPx, 3);
        Assert.False(e.Pass);   // 기본 기준 10px 초과
    }

    [Fact]
    public void 기울기_차이는_각도오차로()
    {
        // 정답 수평, 검출은 중심(424,200)을 지나며 5° 기울어짐.
        double t = Math.Tan(5 * Math.PI / 180);
        var det = Enumerable.Range(0, 848).Select(x => new PixelPoint(x, 200 + (x - 423.5) * t)).ToList();
        var e = WeldLineMetrics.Compare(Gt(), det, horizontal: true);
        Assert.Equal(5, e.AngleDeg, 1);
        Assert.True(e.CenterOffsetPx < 1);
        Assert.False(e.Pass);
    }

    [Fact]
    public void 부분_검출은_커버리지가_낮다()
    {
        var e = WeldLineMetrics.Compare(Gt(), Line(200, 200, 0, 423), horizontal: true);
        Assert.InRange(e.Coverage, 0.45, 0.55);
        Assert.True(e.Pass);   // 직선이 맞으면 FOV 중심 오차는 작다
    }

    [Fact]
    public void 세로_진행축도_계산한다()
    {
        var gt = new WeldLineLabel { Width = 480, Height = 848, Thickness = 9, Points = new() { new[] { 100.0, 0.0 }, new[] { 100.0, 847.0 } } };
        var det = Enumerable.Range(0, 848).Select(y => new PixelPoint(104, y)).ToList();
        var e = WeldLineMetrics.Compare(gt, det, horizontal: false);
        Assert.Equal(4, e.CenterOffsetPx, 3);
        Assert.Equal(0, e.AngleDeg, 3);
    }

    [Fact]
    public void 점이_부족하면_미검출()
    {
        Assert.False(WeldLineMetrics.Compare(Gt(), new List<PixelPoint> { new(1, 1) }, true).Detected);
    }

    [Fact]
    public void 분위수()
    {
        Assert.Equal(2.5, WeldLineMetrics.Quantile(new[] { 1.0, 2, 3, 4 }, 0.5), 6);
        Assert.Equal(4, WeldLineMetrics.Quantile(new[] { 4.0, 1, double.NaN }, 1.0), 6);
        Assert.True(double.IsNaN(WeldLineMetrics.Quantile(Array.Empty<double>(), 0.5)));
    }

    [Fact]
    public void 요약은_미검출을_합격률에_반영하고_오차는_검출분만()
    {
        var items = new List<BenchmarkItem>
        {
            new("a", WeldDetectionMethod.Param, true, null, new LineErrors(true, 1, 4, 2, 1, true), 10, null),
            new("b", WeldDetectionMethod.Param, true, null, new LineErrors(true, 3, 20, 5, 1, false), 20, null),
            new("c", WeldDetectionMethod.Param, false, "x", LineErrors.Missed, 30, null),
        };
        var s = WeldDetectionBenchmarkService.Summarize(WeldDetectionMethod.Param, items);
        Assert.Equal(3, s.Count);
        Assert.Equal(2, s.Detected);
        Assert.Equal(1, s.Passed);
        Assert.Equal(2, s.AngleMedian, 6);
        Assert.Equal(12, s.OffsetMedian, 6);
        Assert.Equal(20, s.MillisMean, 6);
    }
}
