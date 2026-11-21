using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>점2 평탄면 레이저 재확인 — 평탄 자리를 더 찾아 변위센서 값이 서로 맞는 자리를 쓴다.</summary>
public class Point2LaserRecheckTests
{
    [Fact]
    public void 두_자리가_맞으면_지금_자리를_쓴다()
        => Assert.Equal(1, FlatSurfaceAlignStep.PickAgreeingSpot(new[] { 373.5, 380.0 }, 12));

    [Fact]
    public void 두_자리가_다르면_짝이_없다()
        => Assert.Null(FlatSurfaceAlignStep.PickAgreeingSpot(new[] { 373.5, 407.3 }, 12));

    [Fact]
    public void 세_번째가_첫_자리와_맞으면_세_번째를_쓴다()
        => Assert.Equal(2, FlatSurfaceAlignStep.PickAgreeingSpot(new[] { 373.5, 407.3, 375.0 }, 12));

    [Fact]
    public void 세_번째가_어긋나도_앞_두_자리가_맞으면_그쪽을_쓴다()
        => Assert.Equal(1, FlatSurfaceAlignStep.PickAgreeingSpot(new[] { 373.5, 378.0, 410.0 }, 12));

    [Fact]
    public void 셋_다_다르면_짝이_없다()
        => Assert.Null(FlatSurfaceAlignStep.PickAgreeingSpot(new[] { 360.0, 380.0, 400.0 }, 12));

    [Fact]
    public void 한_자리만이면_판정하지_않는다()
        => Assert.Null(FlatSurfaceAlignStep.PickAgreeingSpot(new[] { 400.0 }, 12));

    [Theory]
    [InlineData(null, true, 12)]
    [InlineData(null, false, 0)]
    [InlineData(0.0, true, 0)]
    [InlineData(20.0, false, 20)]
    [InlineData(500.0, true, 12)]
    public void 허용차_파라미터를_해석한다(double? raw, bool usesGas, double expected)
        => Assert.Equal(expected, FlatSurfaceAlignStep.ResolvePoint2LaserRecheckMm(raw, usesGas));
}
