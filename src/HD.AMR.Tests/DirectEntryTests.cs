using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>직접 진입 — 평면 경유를 쓰지 않는 면의 후보와 진입 MoveJ 경로 점검.</summary>
public class DirectEntryTests
{
    [Theory]
    [InlineData(null, "PU", true)]      // 기본 = PU·SU
    [InlineData(null, "SU", true)]
    [InlineData(null, "T", false)]
    [InlineData(null, "F", false)]
    [InlineData(null, "PM", false)]
    [InlineData("*", "F", true)]        // 모든 면(종전 동작)
    [InlineData("", "PU", false)]       // 빈 값 = 어느 면도 쓰지 않음
    [InlineData("pu, su ,T", "T", true)]
    [InlineData(null, null, true)]      // 면 코드를 모르면 안전 쪽
    public void 평면_경유를_쓰는_면은_설정이_정한다(string? setting, string? wall, bool expected)
        => Assert.Equal(expected, CobotInspectionMoveStep.UsesPlanarEntry(setting, wall));

    [Fact]
    public void 직접_진입은_면_방향_거리가_멀어도_들어갈_점이_첫_후보다()
    {
        // 면 방향 1,200 mm — 평면 경유는 들어갈 점 직접이 없고 500 mm 까지만, 직접 진입은 들어갈 점 직접 + 전 구간.
        var plan = CobotInspectionMoveStep.PlanPlanarApproach(
            new[] { 300.0, 0, 600 }, new[] { 1500.0, 0, 900 }, new[] { 0.0, 0, -1 }, 100);
        var planar = CobotInspectionMoveStep.EntryCandidates(plan);
        var direct = CobotInspectionMoveStep.EntryCandidates(plan, planar: false);

        Assert.DoesNotContain(planar, p => ReferenceEquals(p, plan.Plunge));
        Assert.Same(plan.Plunge, direct[0]);
        Assert.True(direct.Count > planar.Count);
        Assert.Contains(direct, p => 1500.0 - p[0] < 200);   // 들어갈 점 가까이까지 후보가 있다
    }

    [Fact]
    public void 경로_중간에_자기_간섭이_있으면_막는다()
    {
        var from = new[] { 0.0, 0, 0, 0, 90, 180 };
        var to = new[] { 60.0, 0, 0, 0, 90, 180 };
        // J1 이 28°~32° 를 지날 때만 걸리는 장애물 — 양 끝 자세는 멀쩡하다.
        string? Self(double[] j) => j[0] is > 28 and < 32 ? "hit" : null;
        Assert.Contains("자기 간섭", CobotInspectionMoveStep.JointPathBlocker(from, to, Self, null));
        Assert.Null(CobotInspectionMoveStep.JointPathBlocker(from, to, _ => null, null));
    }

    [Fact]
    public void 경로가_양_끝보다_면에_가까워지면_막는다()
    {
        var from = new[] { 0.0, 0, 0, 0, 90, 180 };
        var to = new[] { 60.0, 0, 0, 0, 90, 180 };
        // 양 끝 여유 300 mm, 가운데에서 40 mm 까지 다가감 → 기준 100 mm 미달.
        (double, string) Swing(double[] j) => (300 - 260 * Math.Sin(j[0] / 60 * Math.PI), "검사 툴");
        Assert.Contains("면 여유", CobotInspectionMoveStep.JointPathBlocker(from, to, null, Swing));
        // 양 끝이 이미 60 mm 인 자리(면 가까운 접근)는 60 mm 가 기준 — 경로가 그보다 가까워지지 않으면 통과.
        Assert.Null(CobotInspectionMoveStep.JointPathBlocker(from, to, null, _ => (60, "검사 툴")));
        // 관절 이동이 한 간격 이하면 볼 중간 자세가 없다.
        Assert.Null(CobotInspectionMoveStep.JointPathBlocker(from, new[] { 4.0, 0, 0, 0, 90, 180 }, _ => "hit", Swing));
    }

    [Fact]
    public void 면_여유는_면에서_멀어질수록_크고_면을_넘으면_음수다()
    {
        var env = CobotEnvelope.Default;
        var kin = Fr5Kinematics.Fr5Wml;
        var j = new[] { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };   // 홈 위치_2
        var up = new[] { 0.0, 0, -1 };                                        // 천장: 면에서 코봇 쪽 = 아래
        var near = env.WallClearance(kin, j, new[] { 0.0, 0, 1500 }, up);
        var far = env.WallClearance(kin, j, new[] { 0.0, 0, 2500 }, up);
        Assert.Equal(1000, far.ClearanceMm - near.ClearanceMm, 3);
        Assert.True(env.WallClearance(kin, j, new[] { 0.0, 0, 0 }, up).ClearanceMm < 0);   // BASE 높이의 면 — 팔이 넘어가 있다
        Assert.False(string.IsNullOrEmpty(near.Part));
    }
}
