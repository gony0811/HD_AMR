using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ② 평면 경유 접근 — 진입점·들어갈 점이 모두 후퇴 평면(접근점 면 이격 + 후퇴) 위에 있고, 면 방향 이동이
/// 그 평면 안에서만 일어나는지 고정한다(실기 2026-11-02 L4-R03C09 PU #3 한 번의 MoveJ 대체).
/// </summary>
public class PlanarApproachTests
{
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

    [Fact]
    public void 진입점은_현재_TCP_를_후퇴_평면에_법선_투영한_점()
    {
        var plan = CobotInspectionMoveStep.PlanPlanarApproach(
            new[] { 300.0, 400.0, 800.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 100);

        Assert.Equal(new[] { 300.0, 400.0, 100.0 }, plan.Entry);
        Assert.Equal(new[] { 0.0, 0.0, 100.0 }, plan.Plunge);
        Assert.Equal(500.0, plan.InPlaneMm, 9);
    }

    // PU 챔퍼처럼 45° 기운 면: 두 점 모두 접근점에서 법선 방향 후퇴 거리만큼 떨어진 평면 위.
    [Fact]
    public void 기운_면에서도_두_점은_같은_후퇴_평면_위()
    {
        var s = Math.Sqrt(0.5);
        var wallOut = new[] { s, 0.0, -s };
        var approach = new[] { -1678.0, -536.0, 188.0 };
        var plan = CobotInspectionMoveStep.PlanPlanarApproach(new[] { -419.0, -154.0, 949.0 }, approach, wallOut, 120);

        Assert.Equal(120.0, Dot(Sub(plan.Entry, approach), wallOut), 6);
        Assert.Equal(120.0, Dot(Sub(plan.Plunge, approach), wallOut), 6);
        Assert.Equal(0.0, Dot(Sub(plan.Plunge, plan.Entry), wallOut), 6);   // 면 방향 이동은 법선 성분이 없다
    }

    // 후보 순서: 들어갈 점 직접 → 투영 진입점 → 들어갈 점 쪽으로(투영점 쪽부터). 들어갈 점은 한 번만.
    [Fact]
    public void 진입점_후보는_들어갈_점부터_그다음_투영점_쪽부터()
    {
        var plan = CobotInspectionMoveStep.PlanPlanarApproach(
            new[] { 300.0, 0.0, 800.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 100);
        var c = CobotInspectionMoveStep.EntryCandidates(plan);

        Assert.Equal(plan.Plunge, c[0]);
        Assert.Equal(plan.Entry, c[1]);
        Assert.Equal(new[] { 200.0, 0.0, 100.0 }, c[2]);
        Assert.Equal(new[] { 100.0, 0.0, 100.0 }, c[3]);
        Assert.Equal(4, c.Count);
    }

    [Fact]
    public void 면_방향_거리가_짧으면_들어갈_점만_후보()
    {
        var plan = CobotInspectionMoveStep.PlanPlanarApproach(
            new[] { 20.0, 0.0, 800.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 100);

        Assert.Single(CobotInspectionMoveStep.EntryCandidates(plan));
    }

    // 실기 2026-11-02 13:23 H1: 투영 진입점 해가 J4 를 ~220° 돌렸고, 면 방향 경로 도중 해가 433° 튀었는데 통과했다.
    [Fact]
    public void 경로_점_사이_관절_점프는_막는다()
    {
        var entry = new[] { 0.0, -100, -60, -150, 80, -155 };
        var prev = new[] { 0.0, -100, -60, -150, 80, -173.7 };
        var flipped = new[] { 0.0, -100, -60, -10, 80, -33.1 };
        var smooth = new[] { 2.0, -98, -62, -148, 79, -176 };

        Assert.NotNull(CobotInspectionMoveStep.PathStepBlocker(prev, flipped, entry));
        Assert.Null(CobotInspectionMoveStep.PathStepBlocker(prev, smooth, entry));
    }

    [Fact]
    public void 경로_점에서_J5_부호가_바뀌면_막는다()
    {
        var entry = new[] { 0.0, -100, -60, -150, 10, -155 };
        var next = new[] { 0.0, -100, -60, -150, -3, -155 };

        Assert.NotNull(CobotInspectionMoveStep.PathStepBlocker(entry, next, entry));
    }

    [Fact]
    public void 진입_관절_이동량은_관절별_최댓값()
    {
        Assert.Equal(219.0, CobotInspectionMoveStep.MaxJointTravel(
            new[] { 0.18, 4.2, -155.8, 69.0, 90.0, -179.7 }, new[] { 10.0, -50, -140, -150.0, 80, -155 }), 6);
    }

    [Fact]
    public void 경로_샘플은_간격_이하로_끝점까지()
    {
        var pts = CobotInspectionMoveStep.SampleSegment(new[] { 0.0, 0, 0 }, new[] { 250.0, 0, 0 }, 100);

        Assert.Equal(3, pts.Count);
        Assert.Equal(new[] { 250.0, 0, 0 }, pts[^1]);
        Assert.All(pts.Zip(pts.Skip(1)), p => Assert.True(p.Second[0] - p.First[0] <= 100.0 + 1e-9));
    }
}
