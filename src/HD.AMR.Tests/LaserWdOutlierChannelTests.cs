using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>레이저 튀는 채널 제외 — 두 채널이 맞고 하나만 튀면 두 채널 평균을 쓴다.</summary>
public class LaserWdOutlierChannelTests
{
    [Theory]
    [InlineData(null, true, 10)]
    [InlineData(null, false, 0)]       // 비전(용접검사기)은 비우면 끔 — 3채널 평균 그대로
    [InlineData(0.0, true, 0)]
    [InlineData(20.0, false, 20)]
    [InlineData(500.0, true, 10)]
    public void 한계를_해석한다(double? raw, bool gas, double expected)
        => Assert.Equal(expected, LaserWorkingDistanceStep.ResolveOutlierMm(raw, gas));

    [Fact]
    public void M2_22시39분_CH3_291_은_빼고_400_5()
    {
        var r = LaserWorkingDistanceStep.AgreeingPairZ(new double?[] { 400.44, 400.54, 291.0 }, 10)!.Value;
        Assert.Equal(3, r.Excluded);
        Assert.Equal(400.49, r.Z, 2);
    }

    [Fact]
    public void 세_채널이_맞으면_평소대로()
        => Assert.Null(LaserWorkingDistanceStep.AgreeingPairZ(new double?[] { 400.1, 399.6, 405.0 }, 10));

    [Fact]
    public void 한_채널이_범위_밖이면_나머지_둘()
    {
        var r = LaserWorkingDistanceStep.AgreeingPairZ(new double?[] { null, 439.6, 438.8 }, 10)!.Value;
        Assert.Equal(1, r.Excluded);
        Assert.Equal(439.2, r.Z, 2);
    }

    [Fact]
    public void 맞는_짝이_없으면_null()
        => Assert.Null(LaserWorkingDistanceStep.AgreeingPairZ(new double?[] { 380, 400, 420 }, 10));

    [Fact]
    public void 두_채널이_범위_밖이면_null()
        => Assert.Null(LaserWorkingDistanceStep.AgreeingPairZ(new double?[] { null, null, 400 }, 10));

    [Fact]
    public void 끄면_null()
        => Assert.Null(LaserWorkingDistanceStep.AgreeingPairZ(new double?[] { 400.44, 400.54, 291.0 }, 0));
}
