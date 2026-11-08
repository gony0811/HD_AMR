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

    // 실기 2026-11-02 13:45·13:48 #3: 접근점이 면을 따라 멀면(> 500mm) 들어갈 점으로 바로 MoveJ 하지 않고,
    // 투영점에서 500mm 이내 후보에서만 진입해 나머지는 후퇴 평면 MoveL 로 간다.
    [Fact]
    public void 먼_접근점은_직접_진입하지_않고_투영점_근처에서만_진입()
    {
        var plan = CobotInspectionMoveStep.PlanPlanarApproach(
            new[] { 1500.0, 0.0, 800.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 100);
        var c = CobotInspectionMoveStep.EntryCandidates(plan);

        Assert.DoesNotContain(c, p => ReferenceEquals(p, plan.Plunge));
        Assert.Equal(plan.Entry, c[0]);
        Assert.All(c, p => Assert.True(1500.0 - p[0] <= CobotInspectionMoveStep.MaxEntryInPlaneMm + 1e-6));
        Assert.Equal(6, c.Count);   // 투영점 + 100·200·300·400·500mm
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

    // 진입 한계 140°(2026-11-02 ACS 분석) — 완화로 들어올 수 있는 손목 뒤집기 해는 따로 막는다.
    // 14:05 후보 [-661, -274, 881]: J1 117°·J5 +90→−50°(관절 이동 143°).
    [Fact]
    public void 손목_뒤집기_해는_진입_한계와_별개로_막는다()
    {
        var prep = new[] { 0.18, 4.2, -155.82, 68.99, 90.05, -179.69 };
        var flipped = new[] { -117.0, -70, -148, -74, -50, -217 };
        var normal = new[] { 23.0, 13, -145, 84, 108, -178 };   // 14:05 선택된 진입점(23°)

        Assert.True(CobotInspectionMoveStep.IsWristFlip(prep, flipped));
        Assert.False(CobotInspectionMoveStep.IsWristFlip(prep, normal));
        Assert.True(CobotInspectionMoveStep.MaxJointTravel(prep, normal) <= CobotInspectionMoveStep.MaxEntryJointTravelDeg);
    }

    [Fact]
    public void 진입_한계는_140도()
    {
        Assert.Equal(140.0, CobotInspectionMoveStep.MaxEntryJointTravelDeg);
        // #3 직접 진입(82°)·정상 진입(16~29°)은 통과, J4 먼 길 회전(300°대)은 거부.
        Assert.True(82.0 <= CobotInspectionMoveStep.MaxEntryJointTravelDeg);
        Assert.True(308.0 > CobotInspectionMoveStep.MaxEntryJointTravelDeg);
    }

    [Fact]
    public void 진입_관절_이동량은_관절별_최댓값()
    {
        Assert.Equal(219.0, CobotInspectionMoveStep.MaxJointTravel(
            new[] { 0.18, 4.2, -155.8, 69.0, 90.0, -179.7 }, new[] { 10.0, -50, -140, -150.0, 80, -155 }), 6);
    }

    // ── 복귀 경로 재추적(SequenceEntry.PlanRetrace) ── 실기 2026-11-02 13:57 #3 복귀 EMO 대체.
    // 면 = z 0 평면, 바깥 법선 +z, 접근점 원점, 후퇴 100mm, 진입점 (1500, 0, 100).
    private static readonly App.Service.Sequence.PlanarApproachRecord Rec = new(
        new[] { 0.0, 0.0, 1.0 }, new[] { 0.0, 0.0, 0.0 }, 100, new[] { 1500.0, 0.0, 100.0 }, "PU");

    [Fact]
    public void 복귀는_법선으로_후퇴_평면까지_물러난_뒤_진입점으로()
    {
        var plan = SequenceEntry.PlanRetrace(new[] { 0.0, 0.0, 0.0 }, Rec)!;

        Assert.Equal(new[] { 0.0, 0.0, 100.0 }, plan.Lift);
        Assert.Equal(new[] { 1500.0, 0.0, 100.0 }, plan.Entry);
        Assert.Equal(1500.0, plan.InPlaneMm, 9);
    }

    // 검사(⑱) 뒤 다른 자리에 있어도 같은 면 앞이면 같은 방식으로 돌아간다.
    [Fact]
    public void 면_위_다른_자리에서도_같은_평면으로_돌아간다()
    {
        var plan = SequenceEntry.PlanRetrace(new[] { 360.0, 50.0, 30.0 }, Rec)!;

        Assert.Equal(new[] { 360.0, 50.0, 100.0 }, plan.Lift);
        Assert.Equal(new[] { 1500.0, 0.0, 100.0 }, plan.Entry);
    }

    // 이미 후퇴 평면보다 바깥이면 벽 쪽으로 다시 들어가지 않고 그 높이 그대로 면을 따라 간다.
    [Fact]
    public void 후퇴_평면보다_바깥이면_그_높이로_면을_따라간다()
    {
        var plan = SequenceEntry.PlanRetrace(new[] { 0.0, 0.0, 250.0 }, Rec)!;

        Assert.Null(plan.Lift);
        Assert.Equal(new[] { 1500.0, 0.0, 250.0 }, plan.Entry);
    }

    // 면을 따라 코봇 리치를 넘게 가야 하면 오래된 기록으로 보고 따라가지 않는다.
    [Fact]
    public void 면_방향으로_너무_멀면_재추적하지_않는다()
    {
        Assert.Null(SequenceEntry.PlanRetrace(new[] { -500.0, 0.0, 0.0 }, Rec));   // 진입점까지 2000mm
        Assert.NotNull(SequenceEntry.PlanRetrace(new[] { 0.0, 0.0, 0.0 }, Rec));   // 1500mm — 정상 기록
    }

    // 그 면 앞이 아니면(기록이 오래됨) 재추적하지 않는다.
    [Theory]
    [InlineData(-200.0)]
    [InlineData(900.0)]
    public void 그_면_앞이_아니면_재추적하지_않는다(double z)
    {
        Assert.Null(SequenceEntry.PlanRetrace(new[] { 0.0, 0.0, z }, Rec));
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
