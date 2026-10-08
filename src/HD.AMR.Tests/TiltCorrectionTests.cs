using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class TiltCorrectionTests
{
    // 현장 로그(2026-10-07/08) 응답: 툴 Rx +a → 측정 rx +a (종전 [+Rx, −Ry] 규약이 발산한 이유).
    // 응답 행렬로 푼 보정은 부호·축 결합과 무관하게 측정 틸트를 0 으로 만든다.
    [Theory]
    [InlineData(1.0, 0.0, 0.0, -1.0)]     // 축 정렬, ry 부호 반전
    [InlineData(1.0, 0.23, -0.79, 0.29)]  // 현장 추정치(축 결합 포함)
    [InlineData(-1.0, 0.0, 0.0, 1.0)]     // 종전 규약이 맞는 장착
    public void Correction_ZeroesTilt_ForAnyResponse(double a, double b, double c, double d)
    {
        double rx0 = -16.5, ry0 = -29.5, probe = 3.0;
        // 프로브: A = 툴 Rx +δ, B = 툴 Ry +δ
        var j = LaserTiltCorrector.BuildJacobian(
            rx0, ry0, rx0 + a * probe, ry0 + c * probe, rx0 + b * probe, ry0 + d * probe, probe);

        var (uRx, uRy) = LaserTiltCorrector.Solve(j, rx0, ry0);

        Assert.Equal(0, rx0 + a * uRx + b * uRy, 6);
        Assert.Equal(0, ry0 + c * uRx + d * uRy, 6);
    }
}

public class NominalTiltResponseTests
{
    // 캘리브레이션된 헤드 기하에서는 공칭 J 로 푼 보정이 종전 규약 [+rx, −ry] 와 같다.
    [Fact]
    public void NominalJacobian_GivesLegacyConvention()
    {
        var (uRx, uRy) = HD.AMR.App.Service.LaserTiltCorrector.Solve(
            HD.AMR.App.Service.LaserTiltCorrector.NominalJacobian(), 6.73, -3.01);
        Assert.Equal(6.73, uRx, 9);
        Assert.Equal(3.01, uRy, 9);
    }

    // 2026-10-08 ACS 실행: 보정 Rx+1.26/Ry−3.03(|u|≈3.3) 후 예측 대비 수 도 어긋남 → 비평탄.
    [Theory]
    [InlineData(0.0, 0.0, 1.9, -1.9, 3.3, true)]    // 2° 이내
    [InlineData(0.0, 0.0, 2.1, 0.0, 3.3, false)]    // 2° 초과
    [InlineData(0.0, 0.0, 4.9, 0.0, 10.0, true)]    // 큰 회전은 0.5×|u| 허용
    [InlineData(0.0, 0.0, -5.35, -0.65, 3.3, false)]
    public void ResponseConsistency(double pRx, double pRy, double rx, double ry, double mag, bool expected)
        => Assert.Equal(expected, HD.AMR.App.Service.LaserTiltCorrector.IsResponseConsistent(pRx, pRy, rx, ry, mag));
}
