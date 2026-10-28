using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class LaserTiltDampingTests
{
    // 10-28 10:57 평탄면 — 1° 안팎 보정은 절반만.
    [Fact]
    public void SmallCorrection_IsHalved()
    {
        var (rx, ry, damped) = LaserTiltCorrector.DampSmall(1.069, -0.358);
        Assert.True(damped);
        Assert.Equal(0.5345, rx, 6);
        Assert.Equal(-0.179, ry, 6);
    }

    // 한 축이라도 2° 이상이면 큰 보정 그대로.
    [Theory]
    [InlineData(-2.952, -3.87)]
    [InlineData(2.0, 0.1)]
    public void LargeCorrection_IsUnchanged(double uRx, double uRy)
    {
        var (rx, ry, damped) = LaserTiltCorrector.DampSmall(uRx, uRy);
        Assert.False(damped);
        Assert.Equal(uRx, rx);
        Assert.Equal(uRy, ry);
    }

    // 실제 응답이 공칭의 2배인 진동 조건에서 감쇠하면 기준 1° 안으로 들어온다.
    [Fact]
    public void DoubledResponse_ConvergesWithDamping()
    {
        double tilt = 1.069;
        for (var i = 0; i < 3; i++)
        {
            var (u, _, _) = LaserTiltCorrector.DampSmall(tilt, 0);
            tilt -= 2.0 * u;   // 실제 응답 = 공칭의 2배
        }
        Assert.True(Math.Abs(tilt) < 1.0);
    }
}
