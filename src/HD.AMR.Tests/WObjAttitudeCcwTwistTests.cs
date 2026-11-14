using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 검사 수행 첫 경유점의 비틀림(0/180°) 선택 — J6 는 안전 범위(실제값 −310°~−160°, 2026-10-16 실측 −330° 를
/// 2026-11-05 운영 결정으로 좁힘)를 벗어나면 안 된다.
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
    public void 바닥_B_실기값_시계_134도는_넓힌_범위_안이라_허용()
    {
        // 실기 2026-10-16 B-A0001 #2: 현재 −180°, 0°→−132.8°(범위 밖), 180°→−313.9°(Δ−134°).
        // 2026-11-05~11-13 범위(−300°·−310°)에서는 거부됐고, 2026-11-14 −320° 로 넓혀 다시 180° 후보를 쓴다.
        var pick = WObjAttitude.SelectSafeTwist(-180.0, new (double, double?, double?)[]
        {
            (0.0, -132.8, 101.0),
            (180.0, -313.9, 188.0),
        });
        Assert.NotNull(pick);
    }

    [Fact]
    public void 시계_120도_까지는_허용()
    {
        var pick = WObjAttitude.SelectSafeTwist(-180.0, new (double, double?, double?)[]
        {
            (0.0, -132.8, 101.0),
            (180.0, -299.5, 188.0),
        });
        Assert.Equal(180.0, pick!.Value.TwistDeg);
    }

    [Fact]
    public void 실측_한계를_넘는_반시계_후보는_거부()
    {
        var pick = WObjAttitude.SelectSafeTwist(-270.0, new (double, double?)[]
        {
            (0.0, -325.0),     // −320° 밖
            (180.0, -140.0),   // −145° 밖
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
        // 현재 −270° → −140°·−80° 은 범위(−145° 위쪽) 밖.
        var pick = WObjAttitude.SelectSafeTwist(-270.0, new (double, double?)[]
        {
            (0.0, -140.0),
            (180.0, -80.0),
        });
        Assert.Null(pick);
    }

    [Theory]
    [InlineData(-270.0, true)]
    [InlineData(-179.6, true)]
    [InlineData(-300.0, true)]
    [InlineData(-310.0, true)]
    [InlineData(-305.3, true)]    // 실기 2026-11-10 PU 용접선 가로 정렬 후보
    [InlineData(-160.0, true)]
    [InlineData(-310.5, true)]
    [InlineData(-314.9, true)]    // 실기 2026-11-14 F CROSS4 경유점 #45 — 종전 −310° 로 거부되던 값
    [InlineData(-320.0, true)]
    [InlineData(-145.0, true)]
    [InlineData(-155.0, true)]
    [InlineData(-320.5, false)]
    [InlineData(-144.5, false)]
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

    [Fact]
    public void 반대_감김_보고값에서도_물리적으로_같은_반시계_회전은_허용()
    {
        // 실기 2026-11-03 L2-R02C05 F 수평: 현재 J6 +180.4°(= −179.6° 와 같은 물리각), 0°→+90.0°(Δ−90.4),
        // 180°→+270.0°(Δ+89.6). 실제값 그대로 보면 둘 다 범위 밖이라 검사가 멈췄다.
        var pick = WObjAttitude.SelectSafeTwist(180.4, new (double, double?, double?)[]
        {
            (0.0, 90.0, 150.0),
            (180.0, 270.0, 151.0),
        });
        Assert.NotNull(pick);
        Assert.Equal(0.0, pick!.Value.TwistDeg);
        Assert.Equal(-90.4, pick.Value.DeltaJ6, 1);
    }

    [Fact]
    public void 반대_감김에서도_시계_회전은_거부()
    {
        // +180.4° → +270°(Δ+89.6) = −179.6° → −90° — 실측 범위(−160° 위쪽) 밖, 2026-10-13 EMO 와 같은 방향.
        var pick = WObjAttitude.SelectSafeTwist(180.4, new (double, double?)[]
        {
            (0.0, null),
            (180.0, 270.0),
        });
        Assert.Null(pick);
    }

    [Fact]
    public void 다른_감김의_목표는_같은_물리각이어도_거부()
    {
        // 현재 +180.4° 에서 −270° 로 가면 실제로 450° 를 돈다 — 현재 감김 기준 −630° 라 범위 밖.
        var pick = WObjAttitude.SelectSafeTwist(180.4, new (double, double?)[] { (0.0, -270.0) });
        Assert.Null(pick);
    }

    [Theory]
    [InlineData(-180.0, -320.0, true)]
    [InlineData(-180.0, -145.0, true)]
    [InlineData(-180.0, -125.6, false)]   // 실기 2026-11-05 천장 T 진입점 해(+54°)
    [InlineData(-180.0, -320.5, false)]
    [InlineData(180.4, 40.0, true)]       // +180° 감김 보고값 — 같은 물리 범위 +40°~+215°
    [InlineData(180.4, 215.0, true)]
    [InlineData(180.4, 39.0, false)]
    [InlineData(180.4, 216.0, false)]
    public void 현재_감김_기준_J6_범위_판정(double current, double target, bool ok)
        => Assert.Equal(ok, WObjAttitude.J6RangeBlocker(current, target) is null);

    [Theory]
    [InlineData(-179.6, 0.0)]
    [InlineData(180.4, -360.0)]
    [InlineData(90.0, -360.0)]
    [InlineData(-540.0, 360.0)]
    [InlineData(-92.2, 0.0)]     // 어느 감김으로도 범위 밖 — 종전 판정
    public void 안전_범위_감김_보정량(double current, double expected)
        => Assert.Equal(expected, WObjAttitude.SafeJ6WindingShiftDeg(current));

    [Theory]
    [InlineData(180.0, -179.6, -180.0)]
    [InlineData(180.0, -210.4, -180.0)]
    [InlineData(90.0, -229.7, -270.0)]
    [InlineData(90.0, 85.0, 90.0)]
    [InlineData(180.0, 175.0, 180.0)]
    public void J6_목표는_현재_와인딩_쪽_같은_물리각(double target, double current, double expected)
        => Assert.Equal(expected, CobotInspectionMoveStep.NearestWinding(target, current), 6);
}
