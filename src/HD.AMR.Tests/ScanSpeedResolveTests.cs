using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class ScanSpeedResolveTests
{
    [Fact]
    public void MmPerSec_TakesPriority_AndConvertsToPercent()
    {
        // 20mm/s ÷ 120mm/s(100% 기준) = 16.67%
        var s = InspectionRunStep.ResolveScanSpeed(20, explicitPercent: 5, profileRunVel: 2, defaultPercentParam: 2,
            linearSpeedAt100PctParam: 120, percentAcc: 100);
        Assert.Equal(16.67, s.Vel, 2);
        Assert.Equal(100, s.Acc);
        Assert.Equal(20, s.TargetMmPerSec);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public void NoMmPerSec_FallsBackToPercent(double? mm)
    {
        var s = InspectionRunStep.ResolveScanSpeed(mm, explicitPercent: 7, profileRunVel: 2, defaultPercentParam: 2,
            linearSpeedAt100PctParam: 120, percentAcc: 100);
        Assert.Null(s.TargetMmPerSec);
        Assert.Equal(7, s.Vel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void MissingBasis_UsesDefaultBasis(double? basis)
    {
        var s = InspectionRunStep.ResolveScanSpeed(12, null, 0, null, basis, 100);
        Assert.Equal(12 / InspectionRunStep.DefaultLinearSpeedAt100Pct * 100, s.Vel, 2);
    }

    [Fact]
    public void Percent_IsClampedToValidRange()
    {
        Assert.Equal(100, InspectionRunStep.ResolveScanSpeed(200, null, 0, null, 120, 100).Vel);
        Assert.Equal(InspectionRunStep.MinScanPercent, InspectionRunStep.ResolveScanSpeed(0.01, null, 0, null, 120, 100).Vel);
        Assert.Equal(InspectionRunStep.MaxScanSpeedMmPerSec, InspectionRunStep.ResolveScanSpeed(1000, null, 0, null, 10_000, 100).TargetMmPerSec);
    }
}
