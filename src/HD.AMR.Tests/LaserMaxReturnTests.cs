using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>④⁺ 횡복귀 한계 파라미터(실기 2026-11-21 T L4-R09C26 H1: 306.7mm 가 한계 300mm 에 걸려 실패).</summary>
public class LaserMaxReturnTests
{
    [Theory]
    [InlineData(null, 400)]      // 비면 기본 400
    [InlineData(300.0, 300)]
    [InlineData(500.0, 500)]
    [InlineData(10.0, 400)]      // 범위 밖 → 기본
    [InlineData(5000.0, 400)]
    [InlineData(double.NaN, 400)]
    public void 한계_파라미터를_해석한다(double? value, double expected)
        => Assert.Equal(expected, LaserWorkingDistanceStep.ResolveMaxReturnMm(value));

    [Fact]
    public void 기본값이면_이번_H1_복귀량은_통과한다()
        => Assert.True(306.7 <= LaserWorkingDistanceStep.ResolveMaxReturnMm(null));
}
