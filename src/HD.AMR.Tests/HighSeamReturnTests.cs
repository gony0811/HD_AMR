using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// PU 높은 용접선 뒤 복귀 — 실기 2026-11-25 (12:39·13:08 팔이 면 앞에 정지, 13:28 면 여유 확보 이동이 팔을 접어 운영자 EMO).
/// ① 팔 링크끼리(손목·검사 툴 ↔ 어깨·상완) 간섭을 자기 간섭에 넣는다. ② 검사 뒤에도 진입한 평면 경로를 거슬러 나온다.
/// </summary>
public class HighSeamReturnTests
{
    private static readonly double[] Ready = { -0.48, -20.15, 127.34, -107.39, 89.91, -179.61 };
    private static readonly double[] Home = { -0.52, -12.91, 138.60, -125.89, 89.87, -179.61 };
    private static readonly double[] Home2 = { -0.41, -11.99, 121.97, -110.17, 89.97, 180.39 };
    private static readonly double[] SlotPu = { -0.53, -30.15, 125.57, -95.62, 89.86, -179.60 };
    private static readonly double[] Inspecting = { -0.89, -123.56, 141.29, -63.18, 86.62, -176.12 };   // 12:39 PU 검사 자세
    private static readonly double[] AfterRetract = { -15.22, -130.29, 142.55, -59.57, 75.95, -165.48 }; // 13:28 수평 후퇴 직후
    private static readonly double[] EmoTarget = { -22.3, -119.0, 167.0, -60.5, 67.4, -174.5 };         // 13:28 면 여유 확보 이동 끝(계획)

    private static SelfClearance Eval(double[] q)
        => CobotEnvelope.Default.Evaluate(Fr5Kinematics.Fr5Wml, q, 0, CobotObstacleZone.FallbackMountYawDeg);

    [Fact]
    public void EmoTargetPose_IsLinkCollision()
    {
        var c = Eval(EmoTarget);
        Assert.True(c.Collides, c.ToString());
        Assert.StartsWith("코봇 ", c.Obstacle);
    }

    [Fact]
    public void LinkCollision_CanBeTurnedOff()
    {
        var env = CobotEnvelope.Default with { CheckLinkCollision = false };
        Assert.DoesNotContain("코봇 ", env.Evaluate(Fr5Kinematics.Fr5Wml, EmoTarget, 0, CobotObstacleZone.FallbackMountYawDeg).Obstacle);
    }

    [Theory]
    [MemberData(nameof(NormalPoses))]
    public void NormalPoses_AreNotLinkCollisions(string name, double[] q)
    {
        var c = Eval(q);
        Assert.False(c.Collides && c.Obstacle.StartsWith("코봇 "), $"{name}: {c}");
    }

    public static TheoryData<string, double[]> NormalPoses() => new()
    {
        { "ready", Ready }, { "home", Home }, { "home2", Home2 }, { "검사 준비(PU)", SlotPu },
        { "PU 검사 중", Inspecting }, { "수평 후퇴 직후", AfterRetract },
    };

    /// <summary>면 여유 확보 이동 검증(<see cref="SequenceEntry.RetractSelfBlocker"/>)이 그 끝 자세를 거부한다.</summary>
    [Fact]
    public void RetractSelfBlocker_RejectsEmoTarget()
        => Assert.NotNull(SequenceEntry.RetractSelfBlocker(AfterRetract, EmoTarget));

    [Fact]
    public void SegmentSegmentDistance_Basics()
    {
        Assert.Equal(10.0, CobotEnvelope.SegmentSegmentDistanceMm(new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 0 }, new[] { 5.0, 10, -5 }, new[] { 5.0, 10, 5 }), 9);
        Assert.Equal(5.0, CobotEnvelope.SegmentSegmentDistanceMm(new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 0 }, new[] { 15.0, 0, 0 }, new[] { 20.0, 0, 0 }), 9);
        Assert.Equal(3.0, CobotEnvelope.SegmentSegmentDistanceMm(new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 0 }, new[] { 0.0, 3, 0 }, new[] { 10.0, 3, 0 }), 9);
    }

    // ── 검사 뒤 평면 역순 복귀 ── 13:28 수치: 바깥 법선 ≈ (0.698, 0.057, −0.713), 진입점 [-1462, -310, -236], 후퇴 100mm,
    // 접근점 = 진입점 − 100·n, 5번 끝 TCP [-912.7, -136.7, 1007.5] → 접근점 평면 뒤 약 393mm.
    private static readonly double[] N = Norm(new[] { 0.698, 0.057, -0.713 });
    private static readonly double[] Entry = { -1462, -310, -236 };
    private static readonly double[] Approach = Entry.Select((v, i) => v - 100 * N[i]).ToArray();
    private static readonly double[] Seam5End = { -912.7, -136.7, 1007.5 };

    private static double[] Norm(double[] v) { var n = Math.Sqrt(v.Sum(x => x * x)); return v.Select(x => x / n).ToArray(); }

    [Fact]
    public void AfterInspection_RetraceIsUsed_WhenStandoffKnown()
    {
        var rec = new PlanarApproachRecord(N, Approach, 100, Entry, "PU", StandoffMm: 400);
        var plan = SequenceEntry.PlanRetrace(Seam5End, rec);
        Assert.NotNull(plan);
        Assert.NotNull(plan!.Lift);
        // 들어 올린 점은 후퇴 평면(접근점 + 100) 위.
        var h = plan.Lift!.Select((v, i) => (v - Approach[i]) * N[i]).Sum();
        Assert.Equal(100.0, h, 1);
        Assert.True(plan.InPlaneMm < SequenceEntry.RetraceMaxInPlaneMm);
    }

    [Fact]
    public void StandoffUnknown_KeepsLegacyRejection()
        => Assert.Null(SequenceEntry.PlanRetrace(Seam5End, new PlanarApproachRecord(N, Approach, 100, Entry, "PU")));

    [Fact]
    public void BeyondWall_IsStillRejected()
    {
        var rec = new PlanarApproachRecord(N, Approach, 100, Entry, "PU", StandoffMm: 400);
        var beyond = Approach.Select((v, i) => v - 500 * N[i]).ToArray();   // 면(400) 너머 100mm
        Assert.Null(SequenceEntry.PlanRetrace(beyond, rec));
    }
}
