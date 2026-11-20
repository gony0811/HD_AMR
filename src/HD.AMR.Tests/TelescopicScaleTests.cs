using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>
/// 텔레스코픽 컨트롤러 값 ↔ 실제 mm 환산(<see cref="TelescopicSerialSettings.MmPerUnit"/>) — 2026-10-08 줄자 실측
/// (지령 10/20/100/200 → 25/48/240/482mm) 기준.
/// </summary>
public class TelescopicScaleTests
{
    private static readonly TelescopicSerialSettings S = new();   // 기본 2.41mm/단위, 오프셋 0

    [Theory]
    [InlineData(100, 241)]
    [InlineData(200, 482)]
    [InlineData(404, 974)]    // 현장 표시 404 ≈ 실측 1000mm 근처
    [InlineData(0, 0)]
    public void 컨트롤러_값을_실제_mm로_환산한다(int units, int mm)
        => Assert.Equal(mm, S.ToMm(units));

    [Fact]
    public void 파싱_실패_표식_음수는_그대로_둔다()
        => Assert.Equal(-1, S.ToMm(-1));

    [Theory]
    [InlineData(482, 200)]
    [InlineData(241, 100)]
    [InlineData(500, 207)]
    public void 실제_mm를_컨트롤러_지령값으로_환산한다(int mm, int units)
        => Assert.Equal(units, S.ToControllerUnits(mm));

    [Theory]
    [InlineData(0.292, 3, 0, 291)]    // M2 현장 설정 — 999 × 0.292 = 291.7 → 내림 291
    [InlineData(0.292, 4, 0, 2919)]
    [InlineData(2.41, 3, 0, 2407)]
    [InlineData(2.5, 3, 30, 2527)]
    public void 지령_가능_최대_높이는_자릿수와_환산값으로_정해진다(double scale, int digits, int offset, int expectedMm)
    {
        var s = new TelescopicSerialSettings { MmPerUnit = scale, TargetDigits = digits, HeightOffsetMm = offset };
        Assert.Equal(expectedMm, s.MaxReachableHeightMm);
        Assert.True(s.ToControllerUnits(s.MaxReachableHeightMm) <= s.MaxTargetUnits);
    }

    [Fact]
    public void 지령_가능_최대_높이를_넘으면_자릿수_한계를_넘는다()
    {
        var s = new TelescopicSerialSettings { MmPerUnit = 0.292, TargetDigits = 3 };
        Assert.Equal(1370, s.ToControllerUnits(400));
        Assert.True(s.ToControllerUnits(400) > s.MaxTargetUnits);
    }

    [Fact]
    public void 오프셋을_반영해_왕복한다()
    {
        var s = new TelescopicSerialSettings { MmPerUnit = 2.5, HeightOffsetMm = 30 };
        Assert.Equal(280, s.ToMm(100));
        Assert.Equal(100, s.ToControllerUnits(280));
    }
}
