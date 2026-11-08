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

    [Fact]
    public void 끊긴_구간은_커버리지_분모에서_빠진다()
    {
        var gt = new WeldLineLabel
        {
            Width = 848, Height = 480, Thickness = 9,
            Points = new() { new[] { 0.0, 200.0 }, new[] { 300.0, 200.0 }, new[] { 500.0, 200.0 }, new[] { 847.0, 200.0 } },
            Breaks = new() { 2 },
        };
        // 검출은 정답 두 조각 위만(빈 구간 300~500 은 없음) → 커버리지 ~1.
        var det = Line(200, 200, 0, 300).Concat(Line(200, 200, 500, 847)).ToList();
        var e = WeldLineMetrics.Compare(gt, det, horizontal: true);
        Assert.True(e.Coverage > 0.98, $"coverage={e.Coverage}");
        Assert.True(e.Pass);
        // 빈 구간 위의 검출 점은 정답에서 멀다(가까운 끝점까지 거리).
        Assert.True(WeldLineMetrics.DistToLabel(400, 200, gt) >= 99);
        Assert.False(e.BranchMatched);   // 같은 직선 위 조각은 한 가지
    }

    // X 자 교차: 가로선(y=240)과 약 47° 대각선(200,0)-(647,479) 이 가운데서 만난다. 교차부는 짧은 연결 조각으로 이어 그림.
    private static WeldLineLabel Cross() => new()
    {
        Width = 848, Height = 480, Thickness = 9,
        Points = new()
        {
            new[] { 0.0, 240.0 }, new[] { 410.0, 240.0 }, new[] { 418.0, 236.0 }, new[] { 847.0, 236.0 },
            new[] { 200.0, 0.0 }, new[] { 647.0, 479.0 },
        },
        Breaks = new() { 4 },
    };

    [Fact]
    public void 여러_가지_정답은_가장_가까운_가지와_비교()
    {
        // 가로선을 정확히 찾은 검출 — 전체 평균 직선과 비교하면 불합격이던 경우.
        var e = WeldLineMetrics.Compare(Cross(), Line(238, 238), horizontal: true);
        Assert.True(e.BranchMatched);
        Assert.True(e.Pass, $"angle={e.AngleDeg} offset={e.CenterOffsetPx}");
        Assert.Equal(2, WeldLineMetrics.Branches(Cross()).Count);
    }

    [Fact]
    public void 여러_가지_정답이라도_어느_가지와도_안맞으면_불합격()
    {
        var e = WeldLineMetrics.Compare(Cross(), Line(100, 100), horizontal: true);
        Assert.True(e.BranchMatched);
        Assert.False(e.Pass);
    }

    [Fact]
    public void 구불구불하게_그린_일자_비드는_한_가지()
    {
        // 선창 147 처럼 비드 윤곽을 따라 15~30° 씩 꺾어 그린 가로 비드.
        var gt = new WeldLineLabel
        {
            Width = 1280, Height = 720, Thickness = 9,
            Points = new() { new[] { 5.5, 51.0 }, new[] { 200.5, 42.0 }, new[] { 232.0, 18.0 }, new[] { 287.5, 16.5 }, new[] { 395.5, 42.0 },
                             new[] { 904.0, 67.5 }, new[] { 976.0, 34.5 }, new[] { 1049.5, 33.0 }, new[] { 1129.0, 52.5 }, new[] { 1271.5, 60.0 } },
        };
        Assert.Single(WeldLineMetrics.Branches(gt));
    }

    [Fact]
    public void 한_가지_정답은_기존과_결과가_같다()
    {
        Assert.Single(WeldLineMetrics.Branches(Gt(200, 210)));
        var e = WeldLineMetrics.Compare(Gt(200, 210), Line(203, 213), horizontal: true);
        Assert.False(e.BranchMatched);
        Assert.True(e.Pass);
    }

    // 선창 073 — 긴 대각선(약 48°) + 오른쪽 위 짧은 가지, 교차부에 짧은 꼬리.
    private static WeldLineLabel Gt073() => new()
    {
        Width = 1280, Height = 720, Thickness = 15,
        Points = new() { new[] { 380.5, 712.5 }, new[] { 623.5, 445.5 }, new[] { 707.5, 345.0 }, new[] { 763.0, 289.5 }, new[] { 973.0, 40.5 },
                         new[] { 976.0, 7.5 }, new[] { 1011.5, 8.0 }, new[] { 1013.3, 38.2 }, new[] { 1271.1, 242.6 } },
        Breaks = new() { 6 },
    };

    [Fact]
    public void 진행축은_가장_긴_가지_방향으로_정한다()
    {
        // 첫·끝 점(380,712)→(1271,242) 은 가로지만, 주 비드(긴 대각선)는 세로에 더 가깝다.
        var f = WeldLineMetrics.MainBranchFit(Gt073())!.Value;
        Assert.True(Math.Abs(f.Dy) > Math.Abs(f.Dx));
    }

    [Fact]
    public void 짧은_가지는_FOV_중심까지_외삽하지_않고_가지_구간_안에서_중심_오차를_잰다()
    {
        // 오른쪽 위 가지에 평행하게 2px 떨어진 검출 선 — 가지 구간(x 1013~1271)은 화면 중심(640) 밖.
        double Y(double x) => 38.2 + (x - 1013.3) * (242.6 - 38.2) / (1271.1 - 1013.3) + 2;
        var det = new List<PixelPoint> { new(1020, Y(1020)), new(1260, Y(1260)) };
        var e = WeldLineMetrics.Compare(Gt073(), det, horizontal: true);
        Assert.True(e.BranchMatched);
        Assert.True(e.CenterOffsetPx < 3, $"offset {e.CenterOffsetPx}");
        Assert.True(e.Pass);
    }
}
