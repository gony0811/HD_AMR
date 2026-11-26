using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>사전 점검의 코루게이션 기울임(45°) 판정 — 실기 2026-11-18 PL L1-R01C12 정차 대조.</summary>
public class ReachTiltCheckTests
{
    [Fact]
    public void 기울인_자세는_TCP_그대로_법선이_안쪽으로_45도()
    {
        // 광축 −Z(툴 −Z 방향을 봄) → 바깥 법선 = 툴 +Z. 자세 0 이면 법선 = BASE +Z.
        var focus = new double[] { 100, 200, 300, 0, 0, 0 };
        var toward = new double[] { 400, 200, 300, 0, 0, 0 };   // BASE +X 쪽이 용접선 안쪽
        var t = ReachMap.TiltedPose(focus, toward, ToolAxisDir.MinusZ, 45)!;
        Assert.Equal(100, t[0], 6); Assert.Equal(200, t[1], 6); Assert.Equal(300, t[2], 6);
        var m = FrameMath.PoseToMatrix(t);
        double[] n = { m[0, 2], m[1, 2], m[2, 2] };              // 바깥 법선 = −광축 = +툴Z
        Assert.Equal(Math.Sqrt(0.5), n[0], 6);                    // +X(안쪽)로 45°
        Assert.Equal(0, n[1], 6);
        Assert.Equal(Math.Sqrt(0.5), n[2], 6);
    }

    [Fact]
    public void 방향이_법선과_같으면_판정하지_않는다()
        => Assert.Null(ReachMap.TiltedPose(new double[] { 0, 0, 0, 0, 0, 0 }, new double[] { 0, 0, 50, 0, 0, 0 }, ToolAxisDir.MinusZ, 45));

    [Fact]
    public void 가지_판정은_반전과_큰_튐만()
    {
        Assert.Null(ReachMap.TiltBranchReason(new double[] { 0, -20, 60, -80, 90, 0 }, new double[] { 5, -30, 70, -40, 50, 0 }));
        Assert.Contains("팔꿈치 반전", ReachMap.TiltBranchReason(new double[] { 0, -20, 58, -80, 90, 0 }, new double[] { 0, -20, -37, -80, 90, 0 }));
        Assert.Contains("J1 172°", ReachMap.TiltBranchReason(new double[] { 0, -20, 58, -80, 90, 0 }, new double[] { 172, -20, 58, -80, 90, 0 }));
    }

    // 09:38 정차(11.49, 2.266): V10 시작은 기울임에서 막히고(실기 #4 에서 191° 튐), V8 은 통과(실기 완료).
    // 09:24 정차(11.488, 2.043): V9 통과(실기 완료). 오프라인 모델(현장 링크·장착 927mm).
    [Theory]
    [InlineData(2.266, -1.554, 0, 0, 0, 0, false)]                        // V10 시작(좌표는 본문)
    [InlineData(2.266, -1.554, 0.8715, 0.3378, 0.6914, 0.5178, true)]    // V8 시작
    [InlineData(2.043, -1.581, 0.6914, 0.5178, 0.5115, 0.6978, true)]    // V9 시작
    public async Task 실기_정차_대조(double amrY, double yaw, double sy, double sz, double ey, double ez, bool usable)
    {
        double[] s = { 11.9488, sy, sz }, e = { 11.9490, ey, ez };
        if (!usable) { s = new[] { 11.9490, 0.5115, 0.6978 }; e = new[] { 11.9491, 0.3314, 0.8779 }; }
        var links = new[] { 180, -970.317, -815.788, 159.25, 114.077, 105 };
        var kin = Fr5Kinematics.FromLinks(links).Kinematics;
        var limits = new PostureLimits(new double[] { -175, -175, -160, -175, -175, -180 }, new double[] { 175, 175, 160, 175, 175, 180 },
            2, 8, 180, 250, 350).Normalized();
        var robot = new ReachMapRobot(new double[] { 0, 0, 927, 0, 0, 180 }, ApproachReach.ToolLengthMm(Fr5Kinematics.InspectionTool), limits,
            ToolAxisDir.PlusZ, Envelope: new CobotEnvelope { BaseHeightMm = 927 },
            StartJointsByWall: new Dictionary<string, double[]> { ["PL"] = new[] { -2.656, -21.445, 102.369, -81.165, 88.494, -179.653 } },
            MaxEntryJointTravelDeg: CobotInspectionMoveStep.MaxEntryJointTravelDeg);
        var ik = ReachMapService.OfflineInverseKin(kin, Fr5Kinematics.InspectionTool, limits, null, 180);
        var b = ReachStationFrame.WorldToBodyMm(s, 11.49, amrY, yaw, 110);
        var t = ReachStationFrame.WorldToBodyMm(e, 11.49, amrY, yaw, 110);
        var spec = new ReachMapSpec(new[] { "PL" }, new[] { 0.0 }, new[] { b[0] }, new[] { b[1] }, new[] { b[2] },
            TiltTowardMm: t, TiltDeg: ReachMap.CorrugationTiltDeg);
        var c = (await ReachMap.BuildAsync(spec, robot, ik)).Cells[0];
        Assert.Equal(usable, c.Usable);
        if (!usable) Assert.Contains("코루게이션 기울임", c.Reason);
    }

    [Fact]
    public void 광축_둘레_비틀림은_위치와_광축을_유지한다()
    {
        var pose = new double[] { 100, -200, 300, 12, -34, 56 };
        var spun = ReachMap.SpinAboutAxis(pose, ToolAxisDir.PlusZ, 180);
        Assert.Equal(100, spun[0], 6); Assert.Equal(-200, spun[1], 6); Assert.Equal(300, spun[2], 6);
        var a = FrameMath.PoseToMatrix(pose);
        var b = FrameMath.PoseToMatrix(spun);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(a[i, 2], b[i, 2], 6);    // 광축(툴 Z) 그대로
            Assert.Equal(-a[i, 0], b[i, 0], 6);   // 툴 X·Y 는 반대
            Assert.Equal(-a[i, 1], b[i, 1], 6);
        }
    }

    /// <summary>
    /// 실기 2026-11-26 16:50 PL L2-R01C03-4(세로, 경사 위로 한 칸): 사전 점검이 "기울임 45° — 손목 반전"으로 거부했으나 같은 정차
    /// (28.822, 2.079, θ −1.601)에서 수동 순회는 비틀림 180° 로 완료. 두 비틀림을 모두 보면 통과해야 한다.
    /// </summary>
    [Theory]
    [InlineData(0.18)]
    [InlineData(-0.18)]
    public async Task 실기_20261126_PL_세로_용접선은_비틀림_반대로_통과(double dy)
    {
        double[] s = { 28.629, 0.719, 0.181 }, e = { 28.629, 0.719 + dy, 0.181 + 0.18 };
        var links = new[] { 180, -970.317, -815.788, 159.25, 114.077, 105 };
        var kin = Fr5Kinematics.FromLinks(links).Kinematics;
        var limits = new PostureLimits(new double[] { -175, -175, -160, -175, -175, -180 }, new double[] { 175, 175, 160, 175, 175, 180 },
            2, 8, 180, 250, 350).Normalized();
        var robot = new ReachMapRobot(new double[] { 0, 0, 927, 0, 0, 180 }, ApproachReach.ToolLengthMm(Fr5Kinematics.InspectionTool), limits,
            ToolAxisDir.PlusZ, Envelope: new CobotEnvelope { BaseHeightMm = 927 },
            StartJointsByWall: new Dictionary<string, double[]> { ["PL"] = new[] { -2.656, -21.445, 102.369, -81.165, 88.494, -179.653 } },
            MaxEntryJointTravelDeg: CobotInspectionMoveStep.MaxEntryJointTravelDeg);
        var ik = FlipFirstTiltIk(ReachMapService.OfflineInverseKin(kin, Fr5Kinematics.InspectionTool, limits, null, 180));
        var b = ReachStationFrame.WorldToBodyMm(s, 28.822, 2.079, -1.601, 0);
        var t = ReachStationFrame.WorldToBodyMm(e, 28.822, 2.079, -1.601, 0);
        var spec = new ReachMapSpec(new[] { "PL" }, new[] { 0.0 }, new[] { b[0] }, new[] { b[1] }, new[] { b[2] },
            TiltTowardMm: t, TiltDeg: ReachMap.CorrugationTiltDeg);
        var c = (await ReachMap.BuildAsync(spec, robot, ik)).Cells[0];
        Assert.True(c.Usable, c.Reason);
    }

    /// <summary>
    /// 현장 컨트롤러 IK 흉내 — ② · 정렬 준비 · 초점(앞 3번)은 그대로 풀고, 첫 기울인 자세(4번째 호출, 비틀림 0°)에는 현장처럼
    /// 같은 자세의 손목 반전 해(J4+180°, −J5, J6+180°)를 돌려준다(실기 J5 77.8° → −90.6°). 오프라인 IK 는 가지를 유지해
    /// 그날 거부가 재현되지 않으므로, "한 비틀림이 막혀도 다른 비틀림으로 판정한다"는 규칙만 이 방식으로 고정한다.
    /// </summary>
    private static Func<double[], double, double[]?, CancellationToken, Task<double[]?>> FlipFirstTiltIk(
        Func<double[], double, double[]?, CancellationToken, Task<double[]?>> inner)
    {
        var n = 0;
        return async (pose, h, seed, ct) =>
        {
            var j = await inner(pose, h, seed, ct);
            if (++n != 4 || j is not { Length: 6 }) return j;
            static double Wrap(double d) { d %= 360; if (d > 180) d -= 360; if (d <= -180) d += 360; return d; }
            return new[] { j[0], j[1], j[2], Wrap(j[3] + 180), -j[4], Wrap(j[5] + 180) };
        };
    }
}
