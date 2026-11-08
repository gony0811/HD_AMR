using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class BeadCenteringManualShiftTests
{
    private const double MmPerPx = 0.5;
    private static double ToMm(PeakMeasurement m) => m.DPixel * MmPerPx;

    [Fact]
    public void ManualLineShiftedByMoveLeavesResidualOfD1MinusMove()
    {
        var manual = new PeakMeasurement { PeakId = 1, DPixel = 40, DepthZ = 300, Manual = true, Confidence = 1 };
        // d1 = 20mm, 클램프로 15mm 만 이동 → 잔차 5mm(10px).
        var shifted = WeldTrackingService.ShiftedMeasurement(manual, 15 / MmPerPx);
        Assert.Equal(10, shifted.DPixel, 9);
        Assert.Equal(5, ToMm(shifted), 9);
        Assert.True(shifted.Manual);
        Assert.Equal(manual.DepthZ, shifted.DepthZ);

        // 전량 이동이면 잔차 0.
        Assert.Equal(0, WeldTrackingService.ShiftedMeasurement(manual, 40).DPixel, 9);
    }

    [Fact]
    public void ManualDetectionRequiresManualFlagAndMatchingD1()
    {
        var manual = new PeakMeasurement { PeakId = 1, DPixel = 40, Manual = true };
        var auto = new PeakMeasurement { PeakId = 1, DPixel = 40, Manual = false };

        Assert.True(BeadCenteringStep.IsManualMeasurement(manual, ToMm, 20.0));
        Assert.False(BeadCenteringStep.IsManualMeasurement(auto, ToMm, 20.0));
        Assert.False(BeadCenteringStep.IsManualMeasurement(manual, ToMm, 12.0));   // 다른 실행에서 남은 슬롯
        Assert.False(BeadCenteringStep.IsManualMeasurement(null, ToMm, 20.0));
    }
}
