using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>비드 센터링 이동 뒤 재측정 판정 — 축 부호 반대와 첫 검출 오류를 가른다.</summary>
public class BeadRecheckTests
{
    [Theory]
    [InlineData(91.5, 91.5, 6.5)]       // 정상: 91.5 → 6.5
    [InlineData(-6.8, -6.8, 1.2)]
    [InlineData(123.7, 100.0, 23.9)]    // 이동 한계에 걸린 정상
    public void 이격이_줄면_정상(double initial, double moved, double residual)
        => Assert.Equal(BeadCenteringStep.BeadRecheck.Ok, BeadCenteringStep.ClassifyRecheck(initial, moved, residual));

    [Theory]
    [InlineData(91.5, 91.5, 183.0)]     // 축 반대: 2배
    [InlineData(91.5, 91.5, 170.0)]     // 검출 오차 포함
    [InlineData(-6.8, -6.8, -13.6)]
    [InlineData(-6.8, -6.8, -20.0)]     // 작은 이동은 허용 폭 10mm
    [InlineData(123.7, 100.0, 223.7)]   // 이동 한계에 걸린 축 반대: 처음 + 이동량
    public void 처음_더하기_이동량_근처로_늘면_축_반대(double initial, double moved, double residual)
        => Assert.Equal(BeadCenteringStep.BeadRecheck.AxisReversed, BeadCenteringStep.ClassifyRecheck(initial, moved, residual));

    [Theory]
    [InlineData(-6.8, -6.8, 119.2)]     // 실기 2026-11-22 17:03 M2 — 첫 검출이 가짜 선
    [InlineData(5.0, 5.0, -80.0)]
    [InlineData(40.0, 40.0, 150.0)]     // 축 반대(80)와도 멀다
    public void 줄지도_축_반대와_맞지도_않으면_첫_검출_오류(double initial, double moved, double residual)
        => Assert.Equal(BeadCenteringStep.BeadRecheck.FirstDetectionWrong, BeadCenteringStep.ClassifyRecheck(initial, moved, residual));
}
