using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>묶음 점2 레이저·depth 교차 검증(실기 2026-11-21 18:58 S013-6: 레이저 −26.5mm, depth 는 거의 그대로).</summary>
public class BundlePoint2CrossCheckTests
{
    [Fact]
    public void 레이저만_크게_변하면_실패한다()
    {
        var (err, laser, depth) = LaserWorkingDistanceStep.CheckPoint2(400, 343, 373.5, 345, 15);
        Assert.NotNull(err);
        Assert.Equal(-26.5, laser!.Value, 3);
        Assert.Equal(2, depth!.Value, 3);
    }

    [Fact]
    public void 둘이_같이_변하면_통과한다()
        => Assert.Null(LaserWorkingDistanceStep.CheckPoint2(400, 343, 380, 325, 15).Error);

    [Fact]
    public void depth_가_없으면_판정하지_않는다()
        => Assert.Null(LaserWorkingDistanceStep.CheckPoint2(400, null, 340, 345, 15).Error);

    [Fact]
    public void 허용차_0_이면_끈다()
        => Assert.Null(LaserWorkingDistanceStep.CheckPoint2(400, 343, 340, 345, 0).Error);

    [Theory]
    [InlineData(null, 15)]
    [InlineData(0.0, 0)]
    [InlineData(25.0, 25)]
    [InlineData(-1.0, 15)]
    [InlineData(500.0, 15)]
    public void 허용차_파라미터를_해석한다(double? raw, double expected)
        => Assert.Equal(expected, LaserWorkingDistanceStep.ResolvePoint2CrossCheckMm(raw));
}
