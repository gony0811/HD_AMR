using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// Peak 센터링 판정 — 이미 허용 범위 안이면 이동하지 않고, 축 부호 검사는 이동이 충분히 클 때만 한다.
/// 실기 2026-11-12 T L4-R10C09: Peak2 −4.8mm 측정 → 4.8mm 이동 → 재측정 −4.8mm 를 "반대로 움직였다"로 오판해 검사 실패.
/// </summary>
public class PeakCenteringJudgeTests
{
    [Theory]
    [InlineData(-4.8, 5.0, true)]    // 실기 Peak2 — 허용 ±5mm 안
    [InlineData(5.0, 5.0, true)]     // 경계 포함
    [InlineData(-5.1, 5.0, false)]
    [InlineData(-36.3, 5.0, false)]  // 실기 Peak1 — 이동 필요
    [InlineData(-1.0, 0.0, false)]   // 허용치 0 = 보정 안 함 설정 — 종전대로 이동
    public void 측정_이격이_허용_잔차_안이면_이동하지_않는다(double offsetMm, double residualMaxMm, bool expected)
        => Assert.Equal(expected, PeakCenteringStep.AlreadyCentered(offsetMm, residualMaxMm));

    [Fact]
    public void 작은_이동에서는_재측정이_그대로여도_부호_오설정으로_보지_않는다()
    {
        // 실기 값: 측정 −4.8, 이동 −4.8, 재측정 −4.8 (제대로면 0, 반대면 −9.6 — 정확히 한가운데).
        Assert.False(PeakCenteringStep.LooksLikeWrongSign(-4.8, -4.8, -4.8));
        Assert.False(PeakCenteringStep.LooksLikeWrongSign(-4.8, -4.8, -9.6));   // 작은 이동은 어떤 값이 나와도 판정 안 함
        Assert.False(PeakCenteringStep.LooksLikeWrongSign(10.0, 10.0, 20.0));
    }

    [Fact]
    public void 큰_이동에서는_종전대로_부호를_판정한다()
    {
        // 실기 Peak1: 측정 −36.3, 이동 −36.3, 재측정 −2.1 → 정상.
        Assert.False(PeakCenteringStep.LooksLikeWrongSign(-36.3, -36.3, -2.1));
        // 같은 이동인데 Peak 가 반대로 가 −70 근처가 됐다면 부호 오설정.
        Assert.True(PeakCenteringStep.LooksLikeWrongSign(-36.3, -36.3, -70.0));
        // 클램프된 큰 이동(측정 −158.6, 이동 −100): 제대로면 −58.6, 반대면 −258.6.
        Assert.False(PeakCenteringStep.LooksLikeWrongSign(-158.6, -100, -60.0));
        Assert.True(PeakCenteringStep.LooksLikeWrongSign(-158.6, -100, -250.0));
    }

    [Fact]
    public void 부호_검사_경계는_최소_이동량이다()
    {
        var min = PeakCenteringStep.MinSignCheckMoveMm;
        Assert.False(PeakCenteringStep.LooksLikeWrongSign(min - 0.1, min - 0.1, 2 * (min - 0.1)));
        Assert.True(PeakCenteringStep.LooksLikeWrongSign(min, min, 2 * min));
    }
}
