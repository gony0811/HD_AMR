using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class ScanSpeedResolveTests
{
    [Fact]
    public void MmPerSec_TakesPriority_AndUsesPhysicalMode()
    {
        var s = InspectionRunStep.ResolveScanSpeed(20, explicitPercent: 5, profileRunVel: 2, defaultPercentParam: 2,
            accMmPerS2Param: 150, percentAcc: 100);
        Assert.True(s.Physical);
        Assert.Equal(20, s.Vel);
        Assert.Equal(150, s.Acc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public void NoMmPerSec_FallsBackToPercent(double? mm)
    {
        var s = InspectionRunStep.ResolveScanSpeed(mm, explicitPercent: 7, profileRunVel: 2, defaultPercentParam: 2,
            accMmPerS2Param: 150, percentAcc: 100);
        Assert.False(s.Physical);
        Assert.Equal(7, s.Vel);
        Assert.Equal(100, s.Acc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void MissingAccParam_UsesDefaultAcceleration(double? acc)
    {
        var s = InspectionRunStep.ResolveScanSpeed(20, null, 0, null, acc, 100);
        Assert.Equal(InspectionRunStep.DefaultScanAccMmPerS2, s.Acc);
    }

    [Fact]
    public void MmPerSec_IsCappedAtMax()
    {
        var s = InspectionRunStep.ResolveScanSpeed(1000, null, 0, null, null, 100);
        Assert.Equal(InspectionRunStep.MaxScanSpeedMmPerSec, s.Vel);
    }
}
