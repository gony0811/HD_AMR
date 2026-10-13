using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

/// <summary>
/// 검사 수행 첫 경유점의 비틀림(0/180°) 선택 — J6 는 반시계(감소)로만 돌아야 한다(센서 헤드–링크암 간섭).
/// 실기 2026-10-13 16:12: 수평 비드, 현재 J6 −179.6°, cos(rz0=−94.2°) 부호로 180° 를 골라 J6 −92.2° 로
/// 시계 회전 → EMO. 반시계 후보(0°)는 약 −274°(물리 +90° = 수평 검사 자세).
/// </summary>
public class WObjAttitudeCcwTwistTests
{
    [Fact]
    public void 수평_비드_실기값_시계_후보를_버리고_반시계_선택()
    {
        var pick = WObjAttitude.SelectCounterClockwiseTwist(-179.6, new (double, double?)[]
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
        var pick = WObjAttitude.SelectCounterClockwiseTwist(-179.6, new (double, double?)[]
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
        var pick = WObjAttitude.SelectCounterClockwiseTwist(-179.6, new (double, double?)[]
        {
            (0.0, null),
            (180.0, -92.2),
        });
        Assert.Null(pick);
    }

    [Fact]
    public void 반시계라도_허용_최대량을_넘으면_버린다()
    {
        var pick = WObjAttitude.SelectCounterClockwiseTwist(0.0, new (double, double?)[]
        {
            (0.0, -WObjAttitude.MaxCounterClockwiseDeg - 1),
            (180.0, 170.0),
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
}
