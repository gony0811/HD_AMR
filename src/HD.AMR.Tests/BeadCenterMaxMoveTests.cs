using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>비드 센터링 1회 이동 한계 — 파라미터 해석과 잔차.</summary>
public class BeadCenterMaxMoveTests
{
    [Theory]
    [InlineData(null, true, 200)]
    [InlineData(null, false, 100)]     // 비전(용접검사기)은 비우면 종전 100
    [InlineData(150.0, false, 150)]
    [InlineData(5.0, true, 200)]       // 10 미만 → 기본값
    [InlineData(400.0, false, 100)]    // 300 초과 → 기본값
    public void 한계를_해석한다(double? raw, bool gas, double expected)
        => Assert.Equal(expected, BeadCenteringStep.ResolveMaxMoveMm(raw, gas));

    [Fact]
    public void M2_d_115_는_200mm_한계면_잔차_0()
    {
        Assert.Equal(15.4, BeadCenteringStep.ResidualAfterClamp(115.4), 6);        // 종전 100mm
        Assert.Equal(0, BeadCenteringStep.ResidualAfterClamp(115.4, 200), 6);
        Assert.Equal(-20, BeadCenteringStep.ResidualAfterClamp(-220, 200), 6);
    }
}
