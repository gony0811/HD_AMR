using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>Bead 센터링 재측정이 기대 잔차와 어긋나면(첫 검출 오검출) 다시 센터링해야 한다.</summary>
public class BeadRecenterTests
{
    [Fact]
    public void 실기_R02C05_점2_오검출은_다시_센터링한다()
    {
        // 2026-11-22 18:28: d=−113.8 → −100 이동 → 기대 −13.8 인데 재측정 +80.9.
        Assert.True(BeadCenteringStep.NeedsRecenter(-113.8, -100, 80.9));
    }

    [Fact]
    public void 기대대로_남은_잔차는_그대로_쓴다()
    {
        Assert.False(BeadCenteringStep.NeedsRecenter(-113.8, -100, -12.0));   // 클램프로 남은 잔차
        Assert.False(BeadCenteringStep.NeedsRecenter(29.8, 29.8, 1.9));       // R02C06 정상
        Assert.False(BeadCenteringStep.NeedsRecenter(100.9, 100, 10.0));      // 15:23 Bead1
    }

    [Fact]
    public void 보정_상한_밖_잔차도_다시_센터링한다()
    {
        Assert.True(BeadCenteringStep.NeedsRecenter(140, 100, 40));           // 기대와 같아도 30mm 초과
        Assert.True(BeadCenteringStep.NeedsRecenter(10, 10, double.NaN));
    }

    [Fact]
    public void 재센터링_뒤에는_잔차가_0_근처여야_한다()
    {
        // 재센터링 이동 = 재측정값 → 기대 잔차 0.
        Assert.False(BeadCenteringStep.NeedsRecenter(80.9, 80.9, 3.0));
        Assert.True(BeadCenteringStep.NeedsRecenter(80.9, 80.9, -90.0));
    }
}
