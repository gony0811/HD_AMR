using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

// 실기 2026-11-22 M2: 피치 이동 들어올림 측정이 0.3mm/8s 안정 판정에 거의 매번 걸려 피치마다 16s 를 기다렸다.
// 피치 측정은 짧게 기다리고, 안정되지 않아도 최근 유효 샘플이 고르면 중앙값을 쓴다.
public class PitchLaserSampleTests
{
    [Fact]
    public void 피치_측정은_WD_정밀_측정보다_짧고_넓다()
    {
        Assert.True(LaserWorkingDistanceStep.PitchStabilizeTimeoutMs <= 2000);
        Assert.True(LaserWorkingDistanceStep.PitchStableBandMm >= 1.0);
    }

    [Fact]
    public void 흔들려도_고른_최근_샘플은_중앙값을_쓴다()
    {
        // 앞쪽 큰 값은 최근 5개 밖이라 빠진다.
        var z = LaserWorkingDistanceStep.FallbackMedian(new[] { 480.0, 431.2, 430.4, 431.9, 430.8, 431.5 });
        Assert.Equal(431.2, z!.Value, 6);
    }

    [Fact]
    public void 샘플이_적거나_폭이_크면_중앙값을_쓰지_않는다()
    {
        Assert.Null(LaserWorkingDistanceStep.FallbackMedian(new[] { 431.0, 431.5 }));
        Assert.Null(LaserWorkingDistanceStep.FallbackMedian(new[] { 430.0, 433.0, 441.0, 436.0 }));
    }
}
