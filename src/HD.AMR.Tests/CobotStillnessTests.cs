using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>레이저 측정 전 코봇 정지 확인 — 파라미터 해석과 정지 판정.</summary>
public class CobotStillnessTests
{
    [Theory]
    [InlineData(null, true, 300)]
    [InlineData(null, false, 0)]       // 비전(용접검사기)은 비우면 끔
    [InlineData(0.0, true, 0)]
    [InlineData(500.0, false, 500)]
    [InlineData(9000.0, true, 300)]    // 범위 밖 → 기본값
    public void 창을_해석한다(double? raw, bool gas, int expected)
        => Assert.Equal(expected, CobotStillness.ResolveWindowMs(raw, gas));

    private static List<(double, double[])> Samples(params (double t, double j1)[] s)
        => s.Select(x => (x.t, new[] { x.j1, 10.0, 20.0, 30.0, 40.0, 50.0 })).ToList();

    [Fact]
    public void 창을_다_채우고_움직임이_없으면_정지()
        => Assert.True(CobotStillness.IsStill(Samples((0, 1.000), (100, 1.005), (200, 1.002), (310, 1.004)), 300, 0.02));

    [Fact]
    public void 창_안에서_움직이면_정지_아님()   // 감속 중 — 300ms 동안 0.2° 이동
        => Assert.False(CobotStillness.IsStill(Samples((0, 1.0), (100, 1.08), (200, 1.15), (310, 1.2)), 300, 0.02));

    [Fact]
    public void 창을_못_채우면_정지_아님()
        => Assert.False(CobotStillness.IsStill(Samples((0, 1.0), (100, 1.0), (200, 1.0)), 300, 0.02));

    [Fact]
    public void 움직이다_멈추면_최근_창만_본다()
        => Assert.True(CobotStillness.IsStill(
            Samples((0, 0.0), (100, 0.5), (200, 1.0), (300, 1.0), (400, 1.001), (500, 1.0), (620, 1.0)), 300, 0.02));
}
