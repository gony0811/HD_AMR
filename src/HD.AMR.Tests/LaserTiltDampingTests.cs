using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class LaserTiltDampingTests
{
    // 10-28 10:57 평탄면 — 1° 안팎 보정은 절반만.
    [Fact]
    public void SmallCorrection_IsHalved()
    {
        var (rx, ry, damped) = LaserTiltCorrector.Damp(1.069, -0.358);
        Assert.True(damped);
        Assert.Equal(0.5345, rx, 6);
        Assert.Equal(-0.179, ry, 6);
        Assert.Equal(0.5, LaserTiltCorrector.DampFactor(1.069, -0.358));
    }

    // 한 축이라도 2° 이상이면 ×0.6 (2026-11-25 — 종전엔 그대로 돌려 반대쪽으로 넘어갔다).
    [Theory]
    [InlineData(-2.952, -3.87)]
    [InlineData(2.0, 0.1)]
    public void LargeCorrection_IsDampedBy06(double uRx, double uRy)
    {
        var (rx, ry, _) = LaserTiltCorrector.Damp(uRx, uRy);
        Assert.Equal(uRx * 0.6, rx, 9);
        Assert.Equal(uRy * 0.6, ry, 9);
        Assert.Equal(0.6, LaserTiltCorrector.DampFactor(uRx, uRy));
    }

    // 실제 응답이 공칭의 2배인 진동 조건에서 감쇠하면 기준 1° 안으로 들어온다.
    [Fact]
    public void DoubledResponse_ConvergesWithDamping()
    {
        double tilt = 1.069;
        for (var i = 0; i < 3; i++)
        {
            var (u, _, _) = LaserTiltCorrector.Damp(tilt, 0);
            tilt -= 2.0 * u;   // 실제 응답 = 공칭의 2배
        }
        Assert.True(Math.Abs(tilt) < 1.0);
    }

    /// <summary>
    /// 11-20~25 현장 로그의 큰 틸트(종전엔 한 번에 넘어가 '비평탄'으로 버린 자리)를 실제 응답 1.0~2.0배로 흉내 낸다 —
    /// 감쇠 보정이면 보정 4회(FlatSurfaceAlignStep.MaxTiltCorrections) 안에 |rx|,|ry| &lt; 1° 로 들어오고,
    /// 매 회차 실측이 예측과의 응답 허용치 안이며, 발산 가드(직전 ×1.2)에 걸리지 않는다.
    /// </summary>
    [Theory]
    [InlineData(-1.817, 4.266, 1.0)]
    [InlineData(-1.817, 4.266, 1.4)]
    [InlineData(-1.817, 4.266, 2.0)]
    [InlineData(-3.392, 3.348, 1.4)]
    [InlineData(-3.392, 3.348, 2.0)]
    [InlineData(1.174, 7.036, 1.4)]
    [InlineData(1.174, 7.036, 2.0)]
    [InlineData(-6.021, 0.58, 2.0)]
    public void FieldLargeTilt_ConvergesWithin4(double rx0, double ry0, double response)
    {
        var jac = LaserTiltCorrector.NominalJacobian();
        double rx = rx0, ry = ry0, prev = double.PositiveInfinity;
        var corrections = 0;
        while (!(Math.Abs(rx) < 1.0 && Math.Abs(ry) < 1.0))
        {
            var tilt = Math.Max(Math.Abs(rx), Math.Abs(ry));
            Assert.True(corrections == 0 || tilt <= prev * 1.2, $"발산 {prev:0.##} → {tilt:0.##}");
            prev = tilt;
            Assert.True(corrections < 4, $"4회 안에 수렴 못 함 (rx={rx:0.##}, ry={ry:0.##})");

            var (uRx, uRy) = LaserTiltCorrector.Solve(jac, rx, ry);
            var (aRx, aRy, _) = LaserTiltCorrector.Damp(uRx, uRy);
            var predRx = rx + jac[0, 0] * aRx + jac[0, 1] * aRy;
            var predRy = ry + jac[1, 0] * aRx + jac[1, 1] * aRy;
            rx += response * (jac[0, 0] * aRx + jac[0, 1] * aRy);
            ry += response * (jac[1, 0] * aRx + jac[1, 1] * aRy);
            corrections++;
            Assert.True(LaserTiltCorrector.IsResponseConsistent(predRx, predRy, rx, ry, Math.Sqrt(aRx * aRx + aRy * aRy)),
                $"응답 허용치 밖 — 예측 ({predRx:0.##}, {predRy:0.##}) vs 실측 ({rx:0.##}, {ry:0.##})");
        }
    }
}
