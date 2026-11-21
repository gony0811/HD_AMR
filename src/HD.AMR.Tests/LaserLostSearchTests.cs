using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>레이저 분실 시 벽 쪽 재탐색 — 파라미터 해석과 다가가는 단계.</summary>
public class LaserLostSearchTests
{
    [Theory]
    [InlineData(null, true, 60)]
    [InlineData(null, false, 0)]       // 비전(용접검사기)은 비우면 끔 — 동작 그대로
    [InlineData(0.0, true, 0)]
    [InlineData(40.0, false, 40)]
    [InlineData(500.0, true, 60)]      // 범위 밖 → 기본값
    [InlineData(-5.0, false, 0)]
    public void 최대_거리를_해석한다(double? raw, bool gas, double expected)
        => Assert.Equal(expected, LaserWorkingDistanceStep.ResolveLostSearchMm(raw, gas));

    [Fact]
    public void 기본_60mm_는_10mm_씩_6단계()
        => Assert.Equal(new[] { 10.0, 20, 30, 40, 50, 60 }, LaserWorkingDistanceStep.LostSearchSteps(60));

    [Fact]
    public void 나누어떨어지지_않으면_마지막은_최대_거리()
        => Assert.Equal(new[] { 10.0, 20, 25 }, LaserWorkingDistanceStep.LostSearchSteps(25));

    [Fact]
    public void 끄면_단계_없음()
        => Assert.Empty(LaserWorkingDistanceStep.LostSearchSteps(0));
}
