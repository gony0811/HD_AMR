using HD.AMR.App.Service;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

/// <summary>
/// 코봇 자기 간섭(<see cref="CobotEnvelope"/>) — 상완이 리프트 기둥 옆으로 늘어지는 elbow-down 가지는 팔꿈치가 기둥 단면
/// 안으로 들어가고, 실기 홈 자세(티칭값)는 닿지 않는다. 오프라인 IK 는 간섭 가지를 고르지 않는다.
/// </summary>
public class CobotEnvelopeTests
{
    private static readonly Fr5Kinematics Kin = Fr5Kinematics.Fr5Wml;
    private static readonly CobotEnvelope Env = CobotEnvelope.Default;

    // 수직벽 한가운데(데크 위 1.5 m, 정차 1.2 m)에서 여유 최대로 뽑히던 해 — ACS 리치 시뮬레이터 분석(2026-11-01).
    private static readonly double[] ElbowDownAtColumn = { 13, 85, 112, -17, 77, 180 };

    // 실기 홈 위치 티칭값(장비 DB TeachingPositions, 2026-09-16) — 팔꿈치를 BASE 높이로 내민 자세.
    private static readonly double[] TaughtHome = { -0.41, 8.99, 137.19, -146.36, 89.99, 178.14 };

    [Fact]
    public void elbow_down_가지는_팔꿈치가_리프트_기둥에_닿는다()
    {
        var self = Env.Evaluate(Kin, ElbowDownAtColumn, 300, 0);

        Assert.True(self.Collides, self.ToString());
        Assert.Equal("리프트 기둥", self.Obstacle);
    }

    [Fact]
    public void 실기_홈_자세는_닿지_않는다()
    {
        foreach (var yaw in new[] { -90.0, 0, 90, 180 })
            Assert.False(Env.Evaluate(Kin, TaughtHome, 0, yaw).Collides, $"yaw {yaw}");
    }

    [Fact]
    public void 선분_상자_거리는_가장_가까운_점까지다()
    {
        double[] min = { -100, -100, 887 }, max = { 100, 100, 1635 };
        Assert.Equal(0, CobotEnvelope.SegmentBoxDistanceMm(new[] { -300.0, 0, 1000 }, new[] { 300.0, 0, 1000 }, min, max), 6);
        Assert.Equal(50, CobotEnvelope.SegmentBoxDistanceMm(new[] { 150.0, -500, 1000 }, new[] { 150.0, 500, 1200 }, min, max), 3);
        Assert.Equal(Math.Sqrt(50 * 50 + 50 * 50 + 65 * 65),
            CobotEnvelope.SegmentBoxDistanceMm(new[] { 150.0, 150, 700 }, new[] { 150.0, 150, 822 }, min, max), 3);
    }

    [Fact]
    public void 오프라인_IK_는_간섭_가지를_고르지_않는다()
    {
        var tcp = Kin.Forward(ElbowDownAtColumn, Fr5Kinematics.InspectionTool);
        var limits = PostureLimits.Default;
        bool Collides(double[] j) => Env.Evaluate(Kin, j, 300, 0).Collides;

        Assert.Contains(Kin.InverseAll(tcp, Fr5Kinematics.InspectionTool), Collides);
        var pick = Kin.InverseBest(tcp, Fr5Kinematics.InspectionTool, limits, collides: Collides)!;
        Assert.False(Collides(pick));
        // 같은 TCP 다 — 가지만 다르다.
        var back = Kin.Forward(pick, Fr5Kinematics.InspectionTool);
        for (var i = 0; i < 3; i++) Assert.Equal(tcp[i], back[i], 3);
    }

    [Fact]
    public async Task 리치맵은_자기_간섭을_사유로_남긴다()
    {
        var robot = new ReachMapRobot(new[] { 0.0, 0, 1635, 0, 0, 0 }, 375, PostureLimits.Default,
            Envelope: CobotEnvelope.Default);
        var spec = new ReachMapSpec(new[] { "SM" }, new[] { 300.0 }, new[] { 1300.0 }, new[] { 0.0 }, new[] { 1500.0 });

        // 어느 자세든 elbow-down 해를 돌려주는 가짜 IK — J2 80° 로 실측 한계(+85) 안이라 자세는 통과하고 자기 간섭에서 걸린다.
        var hanging = new[] { 13.0, 80, 112, -17, 77, 180 };
        Assert.True(Env.Evaluate(Kin, hanging, 300, 0).Collides);
        var r = await ReachMap.BuildAsync(spec, robot, (_, _) => Task.FromResult<double[]?>(hanging));

        Assert.Equal(ReachStatus.SelfCollision, r.Cells[0].Status);
        Assert.Contains("리프트 기둥", r.Cells[0].Reason);
    }
}
