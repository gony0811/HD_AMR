using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 레이저 WD 보정 이동 판정(<see cref="LaserWorkingDistanceStep.ShouldCorrect"/>).
/// 정상 반응·작은 잔차일 때만 잔차만큼 다시 움직이고, 이상 응답은 보정하지 않는다.
/// </summary>
public class LaserWorkingDistanceCorrectionTests
{
    [Fact]
    public void 현장_오실패_사례는_보정한다()
        // 2026-11-16 15:55 L3-R02C15 H04: 이동 +26.38mm, 측정변화 -25.06mm, 잔차 +1.31mm.
        => Assert.True(LaserWorkingDistanceStep.ShouldCorrect(move: 26.38, change: -25.06, residual: 1.31));

    [Fact]
    public void 허용_이내면_보정하지_않는다()
        => Assert.False(LaserWorkingDistanceStep.ShouldCorrect(move: 29.15, change: -29.64, residual: -0.49));

    [Fact]
    public void 잔차가_보정_상한보다_크면_보정하지_않는다()
        => Assert.False(LaserWorkingDistanceStep.ShouldCorrect(
            move: 26.0, change: -18.0, residual: LaserWorkingDistanceStep.MaxCorrectionMm + 0.1));

    [Theory]
    [InlineData(26.0, 0.0, 3.0)]     // 측정 불변 — 광축 매핑 수직·센서 미갱신
    [InlineData(26.0, +2.0, 3.0)]    // 반대 방향 — 축 부호 반대
    [InlineData(26.0, -5.0, 3.0)]    // 이동의 19%만 반응 — 스케일·센서 이상
    [InlineData(10.0, -20.0, -2.0)]  // 이동의 2배 반응 — 스케일 이상
    public void 이상_응답이면_보정하지_않는다(double move, double change, double residual)
        => Assert.False(LaserWorkingDistanceStep.ShouldCorrect(move, change, residual));

    [Fact]
    public void 뒤로_물러나는_이동도_정상_반응이면_보정한다()
        // 목표보다 가까워 후퇴(이동 −), 거리는 늘어남(측정변화 +).
        => Assert.True(LaserWorkingDistanceStep.ShouldCorrect(move: -20.0, change: +18.5, residual: -1.5));
}
