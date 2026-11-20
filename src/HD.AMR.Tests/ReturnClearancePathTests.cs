using HD.AMR.App.Communication;
using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>기준 자세 복귀 — 관절 이동 전 면 여유 확보(실기 2026-11-20 19:26 SL 상부 → 작업 준비 EMO).</summary>
public class ReturnClearancePathTests
{
    private static readonly Fr5Kinematics Kin = Fr5Kinematics.Fr5Wml;
    private static readonly double[] Tool = Fr5Kinematics.InspectionTool;
    private static readonly double[] Ready = { -0.4, -9.4, 120.0, -110.8, 90.0, -179.6 };

    private static double Wrap(double a, double b) { var x = (a - b) % 360; if (x > 180) x -= 360; if (x < -180) x += 360; return x; }

    private static double[]? NearestIk(double[] pose, double[] seed)
    {
        var sols = Kin.InverseAll(pose, Tool);
        if (sols.Count == 0) return null;
        var best = sols.OrderBy(s => s.Zip(seed, (a, b) => Math.Abs(Wrap(a, b))).Max()).First();
        return best.Zip(seed, (a, b) => b + Wrap(a, b)).ToArray();
    }

    private static (double, string) Wall(double[] q, double[] point, double[] outDir)
        => CobotEnvelope.Default.WallClearance(Kin, q, point, outDir);

    private static string? Self(double[] q)
    {
        var c = CobotEnvelope.Default.Evaluate(Kin, q, 0, CobotObstacleZone.FallbackMountYawDeg);
        return c.Collides ? c.ToString() : null;
    }

    [Fact]
    public void 높은_자세에서_300mm_후퇴만으로는_면쪽으로_휘고_높이를_먼저_맞추면_통과한다()
    {
        // 19:25:58 Teaching 홈 복귀 시작 관절(SL 상부, BASE z≈1255mm)
        var j0 = new[] { -15.2, -79.6, 63.2, 16.2, 79.7, -179.5 };
        var p0 = Kin.Forward(j0, Tool);
        var m = FrameMath.PoseToMatrix(p0);
        double[] outDir = { -m[0, 2], -m[1, 2], -m[2, 2] };   // 모델 툴 +Z 는 면 쪽 — 면에서 코봇 쪽은 그 반대
        var point = p0.Take(3).ToArray();

        // 300mm 후퇴(종전 동작) 뒤 작업 준비로 바로 MoveJ → 면 여유 위반
        var p300 = new[] { p0[0] + outDir[0] * 300, p0[1] + outDir[1] * 300, p0[2] + outDir[2] * 300, p0[3], p0[4], p0[5] };
        var j300 = NearestIk(p300, j0)!;
        Assert.NotNull(CobotInspectionMoveStep.JointPathBlocker(j300, Ready, null, q => Wall(q, point, outDir)));

        // 후보를 짧은 순으로 보아 처음 통과하는 것 — 작업 준비 높이 쪽(아래)으로 옮긴 후보여야 한다
        var readyZ = Kin.Forward(Ready, Tool)[2];
        double[]? picked = null;
        foreach (var off in SequenceEntry.ClearMoveCandidates(p300, outDir, readyZ))
        {
            var pe = new[] { p300[0] + off[0], p300[1] + off[1], p300[2] + off[2], p300[3], p300[4], p300[5] };
            if (NearestIk(pe, j300) is not { } je) continue;
            if (CobotInspectionMoveStep.JointPathBlocker(je, Ready, Self, q => Wall(q, point, outDir)) is null) { picked = off; break; }
        }
        Assert.NotNull(picked);
        Assert.True(picked![2] < -300, $"높이를 내려야 통과 — 고른 후보 [{string.Join(", ", picked.Select(v => v.ToString("0")))}]");
    }

    [Fact]
    public void 후보는_짧은_순이고_목표_높이를_넘지_않는다()
    {
        var c = SequenceEntry.ClearMoveCandidates(new[] { -1100.0, 0, 1250 }, new[] { 1.0, 0, 0 }, -534);
        var costs = c.Select(o => Math.Sqrt(o[0] * o[0] + o[1] * o[1]) + Math.Abs(o[2])).ToList();
        Assert.Equal(costs.OrderBy(x => x), costs);
        Assert.All(c, o => Assert.InRange(o[2], -1784.0 - 1e-6, 0));   // 1250 → −534 까지만
        Assert.Contains(c, o => Math.Abs(o[2] + 1784) < 1e-6);          // 목표 높이 그대로도 후보
        Assert.DoesNotContain(c, o => o.All(v => Math.Abs(v) < 1e-9));  // 제자리 제외
    }

    [Fact]
    public void 천장처럼_법선이_연직이면_물러나기_없이_높이만()
    {
        var c = SequenceEntry.ClearMoveCandidates(new[] { 400.0, 0, 1880 }, new[] { 0.0, 0, -1 }, -534);
        Assert.All(c, o => Assert.Equal(0, Math.Sqrt(o[0] * o[0] + o[1] * o[1]), 6));
    }
}
