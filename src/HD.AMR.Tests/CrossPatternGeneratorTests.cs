using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

public class CrossPatternGeneratorTests
{
    // 시드 기본값(arm 180, spacing 30): 편측 6점 → 주 13 + 회전점 1 + 교차 13 = 27점.
    [Fact]
    public void Generate_DefaultSeed_ProducesExpectedLayout()
    {
        var ok = CrossPatternGenerator.TryGenerate(
            new CrossPatternParams(ArmMm: 180, SpacingMm: 30), out var wp, out var error);

        Assert.True(ok, error);
        Assert.Equal(27, wp.Count);

        // ① 주 arm(0~12): 프레임 X 로 −180→+180 단조 증가, Z=0, 회전 없음.
        for (var i = 0; i < 13; i++)
        {
            Assert.Equal(-180 + i * 30, wp[i].X);
            Assert.Equal(0, wp[i].Z);
            Assert.Equal(0, wp[i].RzDeg);
        }

        // ② 중심 복귀 + 제자리 회전(13): (0,0) RzDeg=−90.
        Assert.Equal((0.0, 0.0, -90.0), (wp[13].X, wp[13].Z, wp[13].RzDeg));

        // ③ 교차 arm(14~26): 프레임 Z 로 −180→+180 단조 증가, X=0, 회전 유지.
        for (var i = 0; i < 13; i++)
        {
            var w = wp[14 + i];
            Assert.Equal(0, w.X);
            Assert.Equal(-180 + i * 30, w.Z);
            Assert.Equal(-90, w.RzDeg);
        }

        // 전 경유점 θ=0 (십자 패턴은 평탄 촬상 — Surface 는 레시피 SurfaceOverride/자동 규칙 소관).
        Assert.All(wp, w => Assert.Equal(0, w.Theta));
    }

    // arm=spacing → 편측 1점: 3 + 1 + 3 = 7점.
    [Fact]
    public void Generate_ArmEqualsSpacing_MinimalCross()
    {
        var ok = CrossPatternGenerator.TryGenerate(
            new CrossPatternParams(ArmMm: 50, SpacingMm: 50), out var wp, out _);

        Assert.True(ok);
        Assert.Equal(7, wp.Count);
    }

    [Fact]
    public void Generate_CustomPerpRz_Applied()
    {
        var ok = CrossPatternGenerator.TryGenerate(
            new CrossPatternParams(ArmMm: 60, SpacingMm: 30, PerpRzDeg: 90), out var wp, out _);

        Assert.True(ok);
        Assert.Equal(90, wp[^1].RzDeg);
        Assert.Equal(0, wp[0].RzDeg);
    }

    // 파라미터 방어: 0/음수/spacing>arm 은 생성 거부.
    [Theory]
    [InlineData(0, 30)]
    [InlineData(-100, 30)]
    [InlineData(180, 0)]
    [InlineData(180, -5)]
    [InlineData(20, 30)]   // spacing > arm
    public void Generate_InvalidParams_Fails(double armMm, double spacingMm)
    {
        var ok = CrossPatternGenerator.TryGenerate(
            new CrossPatternParams(armMm, spacingMm), out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
