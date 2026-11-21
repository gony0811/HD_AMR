using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>무정지 통과 실측 TCP 속도 — 파라미터 해석과 명령별 속도 계산.</summary>
public class TcpSpeedProbeTests
{
    [Theory]
    [InlineData(null, true, true)]
    [InlineData("", false, false)]     // 비전(용접검사기)은 비우면 끔
    [InlineData("off", true, false)]
    [InlineData("0", true, false)]
    [InlineData("on", false, true)]
    public void 켜기_파라미터를_해석한다(string? raw, bool gas, bool expected)
        => Assert.Equal(expected, TcpSpeedProbe.ResolveEnabled(raw, gas));

    /// <summary>X 축으로 곡선 10mm(5mm/s) → 직선 100mm(50mm/s) → 곡선 10mm(5mm/s), 앞뒤로 정지 샘플.</summary>
    private static List<TcpSpeedProbe.Sample> Track()
    {
        var s = new List<TcpSpeedProbe.Sample>();
        var t = 0.0; var x = 0.0;
        void Run(double mm, double v)
        {
            var n = (int)Math.Round(mm / v / 0.008);
            for (var i = 0; i < n; i++) { t += 0.008; x += v * 0.008; s.Add(new(t, x, 0, 0)); }
        }
        for (var i = 0; i < 20; i++) { t += 0.008; s.Add(new(t, 0, 0, 0)); }
        Run(10, 5); Run(100, 50); Run(10, 5);
        for (var i = 0; i < 20; i++) { t += 0.008; s.Add(new(t, x, 0, 0)); }
        return s;
    }

    [Fact]
    public void 명령별_속도를_나눈다()
    {
        var r = TcpSpeedProbe.Analyze(Track(), new[] { 10.0, 100.0, 10.0 });
        Assert.NotNull(r);
        Assert.InRange(r!.TotalMm, 119.5, 120.5);
        Assert.InRange(r.Sec, 5.9, 6.1);                       // 2 + 2 + 2 초
        Assert.InRange(r.Commands[1].AvgMmPerSec, 48, 52);
        Assert.InRange(r.Commands[0].AvgMmPerSec, 4.5, 5.5);
        Assert.InRange(r.Commands[2].AvgMmPerSec, 4.5, 5.5);
        Assert.InRange(r.PeakMmPerSec, 48, 52);
    }

    [Fact]
    public void 안_움직이면_null()
    {
        var s = Enumerable.Range(0, 50).Select(i => new TcpSpeedProbe.Sample(i * 0.008, 1, 2, 3)).ToList();
        Assert.Null(TcpSpeedProbe.Analyze(s, new[] { 10.0 }));
    }
}
