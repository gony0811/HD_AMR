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

/// <summary>WD 이동 도착 판정 — 실기 2026-11-18 11:37 묶음 점2 +1.03mm 보정이 움직이기 전 값으로 확정된 문제.</summary>
public class LaserWorkingDistanceArriveTests
{
    [Theory]
    [InlineData(1.03, 1.03, 0.01, true)]    // 지령만큼 옮겨졌고 멈춤
    [InlineData(0.05, 1.03, 0.01, false)]   // 아직 출발 전(멈춰 있지만 거리 부족) — 종전엔 이 값으로 확정됐다
    [InlineData(0.80, 1.03, 0.20, false)]   // 이동 중
    [InlineData(20.9, 20.98, 0.02, true)]
    public void 도착은_거리와_정지를_함께_본다(double moved, double expected, double step, bool arrived)
        => Assert.Equal(arrived, HD.AMR.App.Service.Sequence.Steps.LaserWorkingDistanceStep.IsArrived(moved, expected, step));
}
