using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>④ 틸트 보정 — 작은 틸트는 같은 자리 재측정, 첫 측정이 크게 기울면 자리 버림
/// (실기 2026-11-21 14:00 S203 H1: 자리1 4.25→2.95→1.27→2.37° 발산, 자리3 첫 rx −32°).</summary>
public class TiltRecenterGateTests
{
    [Theory]
    [InlineData(4.25, true)]    // 크면 종전처럼 재탐색
    [InlineData(3.0, true)]
    [InlineData(2.95, false)]   // 3° 미만 — 같은 자리
    [InlineData(1.27, false)]
    public void 작은_틸트는_재탐색하지_않는다(double tilt, bool expected)
        => Assert.Equal(expected, LaserTiltCorrector.ShouldRecenter(tilt, FlatSurfaceAlignStep.RecenterMinTiltDeg));

    [Theory]
    [InlineData(32.0, true)]    // 코루게이션에 걸린 첫 측정 — 버림
    [InlineData(10.5, true)]
    [InlineData(10.0, false)]   // 클램프 이내 — 보정
    [InlineData(4.25, false)]
    public void 첫_측정이_크게_기울면_자리를_버린다(double tilt, bool expected)
        => Assert.Equal(expected, LaserTiltCorrector.IsFirstTiltTooLarge(tilt, FlatSurfaceAlignStep.MaxFirstTiltDeg));

    [Fact]
    public void 기본_옵션은_종전_동작이다()
    {
        var o = new TiltLevelOptions();
        Assert.True(LaserTiltCorrector.ShouldRecenter(0.5, o.RecenterMinTiltDeg));
        Assert.False(LaserTiltCorrector.IsFirstTiltTooLarge(90, o.MaxFirstTiltDeg));
    }
}
