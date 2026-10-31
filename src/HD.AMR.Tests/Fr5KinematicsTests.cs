using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

/// <summary>
/// FR5-WML 오프라인 기구학. DH 는 장비 STEP 의 관절 축선에서 실측했다 — 여기서는 순·역기구학 정합,
/// STEP 자세 재현, 공구 오프셋, 오프라인 리치 맵 연결을 확인한다. 관절 0점 규약(UR 형)은 실물 대조 대상이다.
/// </summary>
public class Fr5KinematicsTests
{
    private static readonly Fr5Kinematics Kin = Fr5Kinematics.Fr5Wml;

    [Fact]
    public void 전관절_0_은_UR_규약대로_팔이_수평으로_펴진다()
    {
        var f = Kin.ForwardFlange(new double[6]);
        // (a2 + a3, −(d4 + d6), d1 − d5)
        Assert.Equal(-1786, f[0, 3], 6);
        Assert.Equal(-265, f[1, 3], 6);
        Assert.Equal(66, f[2, 3], 6);
    }

    [Fact]
    public void 순기구학_역기구학_왕복()
    {
        var rng = new Random(7);
        for (var n = 0; n < 200; n++)
        {
            var q = Enumerable.Range(0, 6).Select(_ => rng.NextDouble() * 340 - 170).ToArray();
            if (Math.Abs(q[4]) < 5 || Math.Abs(q[2]) < 5) continue;   // 손목·팔꿈치 특이점은 해가 유일하지 않다
            var pose = Kin.Forward(q);
            var sols = Kin.InverseAll(pose);
            Assert.Contains(sols, s => Enumerable.Range(0, 6).All(i => AngleDiff(s[i], q[i]) < 1e-6));
            Assert.All(sols, s => AssertPose(pose, Kin.Forward(s), 1e-6));
        }
    }

    /// <summary>
    /// STEP 에 그려진 자세(BASE 기준 플랜지 위치·J6 축)를 역으로 푼 해가 J3·J4 축선을 STEP 과 같은 자리에 놓는다
    /// — 링크 치수와 축 배치가 도면과 맞는다는 검증. 좌표는 STEP(Y 위)을 BASE(Z 위, 원점 장착면)로 옮긴 값.
    /// </summary>
    [Fact]
    public void STEP_자세를_재현한다()
    {
        var q = new[] { 99.3693, 118.6672, 155.4141, 177.1684, 84.5079, -102.839 };
        var f = Kin.ForwardFlange(q);
        Assert.Equal(81.66, f[0, 3], 1);
        Assert.Equal(544.08, f[1, 3], 1);
        Assert.Equal(39.83, f[2, 3], 1);
        Assert.Equal(-0.9952, f[2, 2], 3);   // 플랜지 z 가 아래를 향한다(툴이 매달린 자세)

        var t3 = Partial(q, 2);
        var t4 = Partial(q, 3);
        Assert.True(LineDistance(t3, new[] { -75.79, 459.1, -671.11 }, new[] { 0.9866, 0.1629, 0 }) < 0.1);
        Assert.True(LineDistance(t4, new[] { -66.34, 401.83, 142.83 }, new[] { 0.9866, 0.1629, 0 }) < 0.1);
    }

    [Fact]
    public void 검사툴_TCP_는_플랜지_z_로_375()
    {
        var q = new[] { 10.0, -80, 100, -110, -70, 30 };
        var flange = Kin.ForwardFlange(q);
        var tcp = PoseMath.FromPose(Kin.Forward(q, Fr5Kinematics.InspectionTool));
        for (var i = 0; i < 3; i++)
            Assert.Equal(flange[i, 3] + 375 * flange[i, 2], tcp[i, 3], 6);
        Assert.Contains(Kin.InverseAll(PoseMath.ToPose(tcp), Fr5Kinematics.InspectionTool),
            s => Enumerable.Range(0, 6).All(i => AngleDiff(s[i], q[i]) < 1e-6));
    }

    [Fact]
    public void 닿지_않는_자세는_해가_없다()
        => Assert.Empty(Kin.InverseAll(new double[] { 2500, 0, 180, 180, 0, 0 }));

    [Fact]
    public void 최선해는_허용범위_여유가_가장_크다()
    {
        var limits = PostureLimits.Default;
        var pose = Kin.Forward(new[] { 0.0, -90, 90, -90, -90, 0 }, Fr5Kinematics.InspectionTool);
        var best = Kin.InverseBest(pose, Fr5Kinematics.InspectionTool, limits)!;
        var bestMargin = limits.Evaluate(new[] { best[0], best[1], best[2], best[3], best[4], 0 }).MarginDeg;
        foreach (var s in Kin.InverseAll(pose, Fr5Kinematics.InspectionTool))
        {
            var j = limits.NormalizeJoints(s);
            Assert.True(limits.Evaluate(new[] { j[0], j[1], j[2], j[3], j[4], 0 }).MarginDeg <= bestMargin + 1e-9);
        }
        AssertPose(pose, Kin.Forward(best, Fr5Kinematics.InspectionTool), 1e-6);
    }

    /// <summary>오프라인 모델로 리치 맵을 돌리면 가까운 정면 벽은 닿고, 팔 길이를 넘는 벽은 닿지 않는다.</summary>
    [Fact]
    public async Task 오프라인_리치맵()
    {
        var limits = PostureLimits.Default;
        var robot = new ReachMapRobot(Fr5Kinematics.StepMount, 375, limits, ToolAxisDir.PlusZ);
        var spec = new ReachMapSpec(new[] { "SM" }, new[] { 0.0 }, new[] { 1500.0, 3200 }, new[] { 0.0 }, new[] { 1800.0 },
            MaxBaseDistanceMm: 5000);
        var r = await ReachMap.BuildAsync(spec, robot,
            ReachMapService.OfflineInverseKin(Kin, Fr5Kinematics.InspectionTool, limits));

        Assert.True(r.Cells[0].Usable, r.Cells[0].Reason);
        Assert.Equal(ReachStatus.IkFailed, r.Cells[1].Status);
    }

    /// <summary>② 와 최종은 100mm 떨어진 같은 방향 자세다 — 오프라인 IK 는 둘을 같은 가지로 이어 관절 변화가 작아야 한다.</summary>
    [Fact]
    public async Task 오프라인_IK_는_최종을_2번_해와_같은_가지로_잇는다()
    {
        var ik = ReachMapService.OfflineInverseKin(Kin, Fr5Kinematics.InspectionTool, PostureLimits.Default);
        var q = new[] { 5.0, -70, 100, -120, -80, 0 };
        var approach = Kin.Forward(q, Fr5Kinematics.InspectionTool);
        var final = PoseMath.ToPose(PoseMath.Multiply(PoseMath.FromPose(approach),
            PoseMath.FromPose(new double[] { 0, 0, 100, 0, 0, 0 })));   // 광축(+Z)으로 100mm 전진

        var ja = (await ik(approach, default))!;
        var jf = (await ik(final, default))!;
        Assert.True(Enumerable.Range(0, 5).Max(i => AngleDiff(ja[i], jf[i])) < 30);
    }

    /// <summary>
    /// 앞 <paramref name="links"/> 개 링크 끝 프레임의 원점(= 관절 links+1 축 위의 점). 뒤 링크 길이와 관절을 0 으로
    /// 둔 모델의 플랜지 원점으로 구한다 — 뒤 링크는 회전만 남아 원점을 옮기지 않는다.
    /// </summary>
    private static double[,] Partial(double[] q, int links)
    {
        var head = q.Take(links).Concat(Enumerable.Repeat(0.0, 6 - links)).ToArray();
        var cut = Kin with { A3 = links >= 3 ? Kin.A3 : 0, D4 = 0, D5 = 0, D6 = 0 };
        return cut.ForwardFlange(head);
    }

    private static double LineDistance(double[,] t, double[] point, double[] dir)
    {
        var n = Math.Sqrt(dir.Sum(v => v * v));
        var d = dir.Select(v => v / n).ToArray();
        var w = new[] { t[0, 3] - point[0], t[1, 3] - point[1], t[2, 3] - point[2] };
        var s = w[0] * d[0] + w[1] * d[1] + w[2] * d[2];
        return Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(w[i] - s * d[i], 2)));
    }

    private static double AngleDiff(double a, double b)
    {
        var d = (a - b) % 360.0;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return Math.Abs(d);
    }

    private static void AssertPose(double[] expected, double[] actual, double tol)
    {
        var e = PoseMath.FromPose(expected);
        var a = PoseMath.FromPose(actual);
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 4; j++)
            Assert.True(Math.Abs(e[i, j] - a[i, j]) < (j == 3 ? tol * 1000 : tol), $"T[{i},{j}] {e[i, j]} ≠ {a[i, j]}");
    }
}
