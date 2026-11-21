using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>WD 작은 이동 — 센서 노이즈에 묻힌 응답으로 오판하지 않는다.</summary>
public class LaserSmallMoveTests
{
    [Theory]
    [InlineData(null, true, 3)]
    [InlineData(null, false, 0)]       // 비전(용접검사기)은 비우면 끔 — 종전 판정 그대로
    [InlineData(0.0, true, 0)]
    [InlineData(2.0, false, 2)]
    [InlineData(9.0, true, 3)]         // 범위 밖 → 기본값
    public void 한계를_해석한다(double? raw, bool gas, double expected)
        => Assert.Equal(expected, LaserWorkingDistanceStep.ResolveSmallMoveMm(raw, gas));

    [Fact]
    public void 이동_1_18mm_변화_0_이면_한번_더_보정한다()   // M2 19:29 — 종전엔 "측정 불변" 실패
    {
        Assert.False(LaserWorkingDistanceStep.ShouldCorrect(move: 1.18, change: 0, residual: 1.18));
        Assert.True(LaserWorkingDistanceStep.ShouldCorrectSmall(move: 1.18, residual: 1.18, smallMm: 3));
    }

    [Fact]
    public void 이동_1_33mm_반대_변화도_한번_더_보정한다()   // M2 21:21 — 종전엔 "반대로 변함" 실패
    {
        Assert.False(LaserWorkingDistanceStep.ShouldCorrect(move: 1.33, change: 1.13, residual: 2.46));
        Assert.True(LaserWorkingDistanceStep.ShouldCorrectSmall(move: 1.33, residual: 2.46, smallMm: 3));
    }

    [Fact]
    public void 큰_이동은_종전_판정을_따른다()
    {
        Assert.False(LaserWorkingDistanceStep.ShouldCorrectSmall(move: 16, residual: 2, smallMm: 3));
        Assert.False(LaserWorkingDistanceStep.AcceptSmallResidual(move: 16, residual: 2, smallMm: 3));
    }

    [Fact]
    public void 끄면_작은_이동_예외_없음()
    {
        Assert.False(LaserWorkingDistanceStep.ShouldCorrectSmall(move: 1.18, residual: 1.18, smallMm: 0));
        Assert.False(LaserWorkingDistanceStep.AcceptSmallResidual(move: 1.18, residual: 1.18, smallMm: 0));
    }

    [Theory]
    [InlineData(1.18, 1.18, true)]
    [InlineData(1.33, 2.46, true)]
    [InlineData(1.33, 3.5, false)]     // 노이즈로 보기엔 큰 잔차 — 실패
    public void 작은_이동_뒤_잔차가_한계_안이면_진행(double move, double residual, bool expected)
        => Assert.Equal(expected, LaserWorkingDistanceStep.AcceptSmallResidual(move, residual, 3));
}
