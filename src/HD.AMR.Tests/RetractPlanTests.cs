using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 벽면 후퇴 계획(<see cref="SequenceEntry.PlanRetract"/>) — 입력은 현장 로그(2026-10-29/30)의 실제 후퇴 시작 TCP.
/// 2026-10-30 SU 챔퍼(Ry 45°)에서 툴 Z+300 후퇴가 z 229→17mm 로 AMR 쪽으로 내려가 J2·J3 가 접힌 사고를 고정한다.
/// </summary>
public class RetractPlanTests
{
    [Fact]
    public void 상부_챔퍼_자세는_높이를_유지한_수평_후퇴()
    {
        // 2026-10-30 14:37 L4-R03C05#5 — #4 챔퍼 수직 검사 종료 자세. 종전 툴 Z+300 끝점은 z≈17mm.
        var plan = SequenceEntry.PlanRetract(new[] { -1558.1, 93.5, 228.7, -180.0, 45.0, -178.2 });

        Assert.True(plan.Horizontal);
        Assert.Null(plan.Blocker);
        Assert.Equal(228.7, plan.EndTcp[2], 1);                       // 높이 유지
        Assert.Equal(0.0, plan.OffsetBase[2], 6);
        var dxy = Math.Sqrt(plan.OffsetBase[0] * plan.OffsetBase[0] + plan.OffsetBase[1] * plan.OffsetBase[1]);
        Assert.Equal(SequenceEntry.RetractDistanceMm, dxy, 6);         // 거리 그대로
        Assert.True(plan.OffsetBase[0] > 290);                         // 벽(−X) 반대쪽으로
    }

    [Fact]
    public void 낮은_상부_챔퍼_자세도_수평이라_막지_않는다()
    {
        // 2026-10-29 — 같은 자세 계열, z 98.8mm. 종전 툴 Z+300 이면 z≈−113mm.
        var plan = SequenceEntry.PlanRetract(new[] { -1889.7, -229.8, 98.8, 178.1, 44.7, 174.8 });

        Assert.True(plan.Horizontal);
        Assert.Null(plan.Blocker);
        Assert.Equal(98.8, plan.EndTcp[2], 1);
    }

    [Theory]
    [InlineData(-1414.0, -174.8, -66.2, -5.1, 89.0, -2.0)]    // 2026-10-29 옆벽 하단 — 광축 수평
    [InlineData(-1175.4, -201.9, 1.0, -7.1, 87.2, -3.0)]
    [InlineData(-765.3, -508.5, 1127.6, 178.7, 2.3, -158.0)]   // 광축이 거의 연직(툴 Z+ 아래) — 수평 성분 부족
    public void 수평_광축이나_연직_광축은_종전대로_툴Z_후퇴(double x, double y, double z, double rx, double ry, double rz)
    {
        var plan = SequenceEntry.PlanRetract(new[] { x, y, z, rx, ry, rz });

        Assert.False(plan.Horizontal);
        Assert.Null(plan.Blocker);
    }

    [Fact]
    public void 연직으로_내려가는_후퇴가_최저_높이_아래면_막는다()
    {
        // 광축이 위(툴 Z+ 가 아래)인 천장 자세를 낮은 곳에서 — 수평 성분이 없어 툴 Z+ 로 내려가야 한다.
        var plan = SequenceEntry.PlanRetract(new[] { -800.0, 0.0, 400.0, 180.0, 0.0, 0.0 });

        Assert.False(plan.Horizontal);
        Assert.Equal(100.0, plan.EndTcp[2], 6);
        Assert.NotNull(plan.Blocker);
    }

    [Fact]
    public void 연직으로_내려가도_끝점이_충분히_높으면_허용()
    {
        var plan = SequenceEntry.PlanRetract(new[] { -800.0, 0.0, 1200.0, 180.0, 0.0, 0.0 });

        Assert.False(plan.Horizontal);
        Assert.Null(plan.Blocker);
    }

    [Fact]
    public void 올라가는_후퇴는_끝점이_낮아도_막지_않는다()
    {
        // 툴 Z+ 가 위로 향하는 낮은 자세(z −66 옆벽 하단처럼 낮은 높이) — 내려가지 않으니 최저 높이 판정 대상이 아니다.
        var plan = SequenceEntry.PlanRetract(new[] { -1400.0, 0.0, -100.0, 0.0, 0.0, 0.0 });

        Assert.False(plan.Horizontal);
        Assert.Equal(200.0, plan.EndTcp[2], 6);
        Assert.Null(plan.Blocker);
    }
}
