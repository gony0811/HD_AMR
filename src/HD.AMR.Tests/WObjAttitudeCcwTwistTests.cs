using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 검사 수행 첫 경유점의 비틀림(0/180°) 선택 — J6 는 안전 범위(실제값 −330°~−160°, 2026-10-16 실측)를 벗어나면 안 된다.
/// 실기 2026-10-13 16:12: 수평 비드, 현재 J6 −179.6°, cos(rz0=−94.2°) 부호로 180° 를 골라 J6 −92.2° 로
/// 시계 회전 → EMO. 반시계 후보(0°)는 약 −274°(물리 +90° = 수평 검사 자세).
/// </summary>
public class WObjAttitudeCcwTwistTests
{
    [Fact]
    public void 수평_비드_실기값_시계_후보를_버리고_반시계_선택()
    {
        var pick = WObjAttitude.SelectSafeTwist(-179.6, new (double, double?)[]
        {
            (0.0, -273.8),     // 반시계 94.2°
            (180.0, -92.2),    // 시계 87.4° — 실기에서 선택됐던 쪽
        });
        Assert.NotNull(pick);
        Assert.Equal(0.0, pick!.Value.TwistDeg);
        Assert.True(pick.Value.DeltaJ6 < 0);
    }

    [Fact]
    public void 수직_비드_이미_정렬이면_회전하지_않는_후보()
    {
        var pick = WObjAttitude.SelectSafeTwist(-179.6, new (double, double?)[]
        {
            (0.0, -178.9),     // 거의 그대로(+0.7° — 허용 여유 안)
            (180.0, 0.4),      // 반 바퀴
        });
        Assert.Equal(0.0, pick!.Value.TwistDeg);
    }

    [Fact]
    public void 반시계로_갈_수_있는_후보가_없으면_null()
    {
        // 반시계 후보가 IK 불가(관절 한계 등)이고 남은 후보는 시계 회전.
        var pick = WObjAttitude.SelectSafeTwist(-179.6, new (double, double?)[]
        {
            (0.0, null),
            (180.0, -92.2),
        });
        Assert.Null(pick);
    }

    [Fact]
    public void 바닥_B_실기값_반시계_134도도_범위_안이면_허용()
    {
        // 실기 2026-10-16 B-A0001 #2: 현재 −180°, 0°→−132.8°(범위 밖), 180°→−313.9°(범위 안, Δ−134°).
        // 옛 규칙(반시계 100° 이내)에선 둘 다 거부돼 검사가 멈췄다.
        var pick = WObjAttitude.SelectSafeTwist(-180.0, new (double, double?, double?)[]
        {
            (0.0, -132.8, 101.0),
            (180.0, -313.9, 188.0),
        });
        Assert.Equal(180.0, pick!.Value.TwistDeg);
    }

    [Fact]
    public void 실측_한계를_넘는_반시계_후보는_거부()
    {
        var pick = WObjAttitude.SelectSafeTwist(-270.0, new (double, double?)[]
        {
            (0.0, -335.0),     // 실측 −330° 밖
            (180.0, -155.0),   // 실측 −160° 밖
        });
        Assert.Null(pick);
    }

    [Fact]
    public void 지정_비틀림이_cos_부호보다_우선()
    {
        var a = new WObjAttitude(-94.2, -1.0);
        Assert.Equal(180.0, a.AbsoluteTwistDeg);                        // 기존 cos 규칙
        Assert.Equal(0.0, (a with { TwistOverrideDeg = 0.0 }).AbsoluteTwistDeg);
    }

    [Fact]
    public void 수평_자세_범위끝에서_대기쪽으로_소량_되돌아가는_후보는_허용()
    {
        // 실기 2026-10-15 PL-A0002: 현재 −270°(물리 90°), 정차 방향 오차로 첫 경유점 J6 −263.7°(물리 96.3°).
        var pick = WObjAttitude.SelectSafeTwist(-270.0, new (double, double?)[]
        {
            (0.0, -263.7),
            (180.0, -263.5),
        });
        Assert.NotNull(pick);
        Assert.Equal(0.0, pick!.Value.TwistDeg);
        Assert.Equal(6.3, pick.Value.DeltaJ6, 1);
    }

    [Fact]
    public void 범위_밖으로_나가는_시계_후보는_거부()
    {
        // 현재 −270° → −150°·−80° 은 실측 범위(−160° 위쪽) 밖.
        var pick = WObjAttitude.SelectSafeTwist(-270.0, new (double, double?)[]
        {
            (0.0, -150.0),
            (180.0, -80.0),
        });
        Assert.Null(pick);
    }

    [Theory]
    [InlineData(-270.0, true)]
    [InlineData(-179.6, true)]
    [InlineData(-330.0, true)]
    [InlineData(-160.0, true)]
    [InlineData(-330.5, false)]
    [InlineData(-155.0, false)]
    [InlineData(96.3, false)]     // 물리각은 범위 안이지만 와인딩이 달라 실측 밖
    [InlineData(30.0, false)]
    [InlineData(-92.2, false)]
    public void 안전_범위_판정은_실제_관절값(double j6, bool expected)
        => Assert.Equal(expected, WObjAttitude.IsInSafeJ6Range(j6));

    [Fact]
    public void J6_변화가_같으면_손목을_뒤집지_않는_후보()
    {
        // 실기 2026-10-15 F-A0002: 두 후보 모두 J6 −270° 그대로, 총 변위 87.4° vs 308.5°(손목 반전 해).
        var pick = WObjAttitude.SelectSafeTwist(-270.0, new (double, double?, double?)[]
        {
            (0.0, -270.02, 87.4),
            (180.0, -270.01, 308.5),
        });
        Assert.Equal(0.0, pick!.Value.TwistDeg);
    }

    [Fact]
    public void J6_변화가_작아도_손목을_뒤집는_후보보다_총_변위가_작은_후보()
    {
        // 실기 2026-10-15 천장 T-A0001 #2: 0°=Δ−7.3°·총 변위 369°(손목 반전), 180°=Δ+9.8°·55°.
        var pick = WObjAttitude.SelectSafeTwist(-209.9, new (double, double?, double?)[]
        {
            (0.0, -217.3, 368.7),
            (180.0, -200.1, 55.1),
        });
        Assert.Equal(180.0, pick!.Value.TwistDeg);
    }

    [Theory]
    [InlineData(180.0, -179.6, -180.0)]
    [InlineData(180.0, -210.4, -180.0)]
    [InlineData(90.0, -229.7, -270.0)]
    [InlineData(90.0, 85.0, 90.0)]
    [InlineData(180.0, 175.0, 180.0)]
    public void J6_목표는_현재_와인딩_쪽_같은_물리각(double target, double current, double expected)
        => Assert.Equal(expected, CobotInspectionMoveStep.NearestWinding(target, current), 6);
}
