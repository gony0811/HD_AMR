using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ⑱ 검사 수행 — 움직이기 전 경유점 관절 해 사슬 검증. 실기 2026-11-03 L2-R02C05 H04: 경유점 #5 해 J5 0.48° 로
/// MoveL 하다 rc=185 로 멈춰, 팔이 특이 자세에 남아 이후 task 4건이 복귀 경로 검증에서 모두 실패했다.
/// </summary>
public class InspectionRunWaypointChainTests
{
    private static readonly double[] Start = { -45.0, -40.0, 80.0, -220.0, 30.0, 90.0 };

    [Fact]
    public void 실기_H04_경유점5_손목_특이점은_거부()
    {
        var why = InspectionRunStep.WaypointChainBlocker(Start, new (int, double[])[]
        {
            (0, new[] { -46.0, -40.0, 80.0, -221.0, 20.0, 90.0 }),
            (4, new[] { -49.86, -38.87, 80.35, -223.91, 0.48, -87.57 }),
        });
        Assert.NotNull(why);
        Assert.Contains("#5", why);
        Assert.Contains("손목 특이점", why);
    }

    [Fact]
    public void J5_부호가_바뀌면_사이에서_0을_지나므로_거부()
    {
        var why = InspectionRunStep.WaypointChainBlocker(Start, new (int, double[])[]
        {
            (0, new[] { -45.0, -40.0, 80.0, -220.0, -12.0, 90.0 }),
        });
        Assert.Contains("#1", why);
    }

    [Fact]
    public void 팔꿈치_가지가_바뀌면_거부()
    {
        var why = InspectionRunStep.WaypointChainBlocker(Start, new (int, double[])[]
        {
            (0, new[] { -45.0, -40.0, 80.0, -220.0, 25.0, 90.0 }),
            (1, new[] { -45.0, -40.0, -60.0, -220.0, 25.0, 90.0 }),
        });
        Assert.Contains("#2 팔꿈치", why);
    }

    [Fact]
    public void J6_가_크게_바뀌어도_안전_범위_안이고_손목_여유가_있으면_통과()
    {
        // 출발 J6 90°(= 감김 기준 −270°) → 150°(−210°): 60° 회전이지만 −310°~−160° 안.
        var why = InspectionRunStep.WaypointChainBlocker(Start, new (int, double[])[]
        {
            (0, new[] { -45.0, -40.0, 80.0, -220.0, 28.0, 90.0 }),
            (1, new[] { -47.0, -39.0, 81.0, -221.0, 10.0, 150.0 }),
        });
        Assert.Null(why);
    }

    [Theory]
    [InlineData(-60.0)]    // 감김 기준 −420° — 시계로 150° 더 감김
    [InlineData(220.0)]    // 감김 기준 −140° — −145° 위쪽(반시계 한계 밖)
    [InlineData(35.0)]     // 감김 기준 −325° — −320° 아래쪽(시계 한계 밖)
    public void J6_안전_범위_밖_경유점은_거부(double j6)
    {
        var why = InspectionRunStep.WaypointChainBlocker(Start, new (int, double[])[]
        {
            (0, new[] { -45.0, -40.0, 80.0, -220.0, 28.0, 90.0 }),
            (1, new[] { -47.0, -39.0, 81.0, -221.0, 10.0, j6 }),
        });
        Assert.NotNull(why);
        Assert.Contains("#2 J6 안전 범위", why);
    }
}
