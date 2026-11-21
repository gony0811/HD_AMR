using HD.AMR.App.Communication;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>코봇 전역 속도로 검사 속도 % 자동 환산 + 상태 패킷 전역 속도 판독.</summary>
public class AutoScanSpeedTests
{
    [Fact]
    public void 전역_40퍼센트면_20mm_s_는_33퍼센트()
    {
        var basis = InspectionRunStep.AutoBasisMmPerSec(150, 40);
        Assert.Equal(60, basis, 6);
        Assert.Equal(33.33, InspectionRunStep.ResolveScanSpeed(20, null, 0, null, basis, 8).Vel, 2);
    }

    [Fact]
    public void 전역_10퍼센트면_목표를_못_내고_100퍼센트()
    {
        var basis = InspectionRunStep.AutoBasisMmPerSec(150, 10);   // 15 mm/s 가 최대
        Assert.Equal(100, InspectionRunStep.ResolveScanSpeed(20, null, 0, null, basis, 8).Vel);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("off", false)]
    public void 켜기_파라미터를_해석한다(string? raw, bool expected)
        => Assert.Equal(expected, InspectionRunStep.ResolveAutoScanSpeed(raw));

    [Theory]
    [InlineData(null, 150)]
    [InlineData(0.0, 150)]
    [InlineData(140.0, 140)]
    public void 전역_100퍼센트_기준을_해석한다(double? raw, double expected)
        => Assert.Equal(expected, InspectionRunStep.ResolveFullGlobalBasis(raw));

    [Fact]
    public void 패킷에서_전역_속도를_읽는다()
    {
        var p = new byte[1600];
        BitConverter.GetBytes(40.0).CopyTo(p, 1579);
        Assert.Equal(40.0, FairinoStateClient.ReadSpeedScale(p, 1579));
    }

    [Fact]
    public void 패킷이_짧거나_값이_이상하면_null()
    {
        Assert.Null(FairinoStateClient.ReadSpeedScale(new byte[500], 1579));
        var p = new byte[1600];
        BitConverter.GetBytes(250.0).CopyTo(p, 1579);
        Assert.Null(FairinoStateClient.ReadSpeedScale(p, 1579));
    }
}
