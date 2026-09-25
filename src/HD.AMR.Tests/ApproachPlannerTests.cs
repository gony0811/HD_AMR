using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

/// <summary>
/// 접근 자세 탐색. 역기구학을 델리게이트로 주입받으므로 하드웨어 없이 전 경로를 검증할 수 있다.
///
/// 가상 로봇 모델: <b>손목각 |J5| 가 "광축을 법선에서 기울인 각" 에 비례해 커진다</b>. 실제 UR 형 6축에서
/// 손목 특이점을 벗어나는 유일한 수단이 광축 방향 변경이라는 성질을 최소한으로 흉내 낸 것이다.
/// spin 은 광축 방향을 바꾸지 않으므로 이 모델에서도 J5 를 바꾸지 못한다 — 실제와 같다.
/// </summary>
public class ApproachPlannerTests
{
    private static readonly double[] Mount = { 0, 0, 700, 0, 0, 180 };

    /// <summary>AMR 이 맵 +Y 를 보고 정차, 용접선은 1m 앞 벽면 위. wall_code 는 좌현벽.</summary>
    private static SeamBaseInput Input(double standoff = 400, double spin = 0) => new(
        SeamStartW: new[] { 5.7, 14.8, 1.2 },
        SeamEndW: new[] { 6.7, 14.8, 1.2 },
        AmrXm: 5.7, AmrYm: 13.8, AmrYawRad: Math.PI / 2,
        MountAtHome: Mount, TelescopicStrokeMm: 0, ZDatumOffsetMm: 0,
        StandoffMm: standoff, WallFacingThetaRad: Math.PI / 2,
        WallCode: "PM", OpticalAxis: ToolAxisDir.MinusZ, ToolSpinDeg: spin);

    private static PostureLimits Limits(double wrist = 15) => new(
        JointMinDeg: Enumerable.Repeat(-175.0, 6).ToArray(),
        JointMaxDeg: Enumerable.Repeat(175.0, 6).ToArray(),
        WristMarginDeg: wrist);

    // ── 가상 역기구학 ───────────────────────────────────────────────

    /// <summary>pose 의 광축(툴 −Z) 이 이상값에서 몇 도 틀어졌는지 → |J5| = base + gain×각.</summary>
    private static Func<double[], CancellationToken, Task<double[]?>> TiltDrivenIk(
        double[] idealPose, double wristBase, double gain, List<double[]>? queried = null)
    {
        var idealAxis = OpticalAxisOf(idealPose);
        return (pose, _) =>
        {
            queried?.Add(pose);
            var deg = AngleDeg(OpticalAxisOf(pose), idealAxis);
            return Task.FromResult<double[]?>(new[] { 0.0, -30, 90, -60, wristBase + gain * deg, 0 });
        };
    }

    /// <summary>툴 −Z 축의 BASE 방향.</summary>
    private static double[] OpticalAxisOf(double[] pose)
    {
        var m = FrameMath.PoseToMatrix(pose);
        return new[] { -m[0, 2], -m[1, 2], -m[2, 2] };
    }

    private static int IndexOf(IReadOnlyList<ApproachCandidate> list, Func<ApproachCandidate, bool> match)
    {
        for (var i = 0; i < list.Count; i++) if (match(list[i])) return i;
        return -1;
    }

    private static double AngleDeg(double[] a, double[] b)
    {
        var d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        return Math.Acos(Math.Clamp(d, -1.0, 1.0)) * 180.0 / Math.PI;
    }

    // ── 후보 생성 ───────────────────────────────────────────────────

    [Fact]
    public void 후보는_이상값_먼저_비용_오름차순이다()
    {
        var list = ApproachPlanner.Candidates(ApproachSearchSpace.Default);

        var first = list[0];
        Assert.Equal(0, first.TiltUpDeg);
        Assert.Equal(0, first.TiltSideDeg);
        Assert.Equal(0, first.SpinDeg);
        Assert.Equal(0, first.StandoffDeltaMm);

        for (var i = 1; i < list.Count; i++)
            Assert.True(list[i].Cost >= list[i - 1].Cost, $"{i}번째 후보의 비용이 역전됐습니다.");
    }

    [Fact]
    public void spin_은_틸트보다_먼저_시도된다()
    {
        // 영상만 도는 spin 이 검사 입사각을 깎는 틸트보다 싼 자유도다.
        var list = ApproachPlanner.Candidates(ApproachSearchSpace.Default);

        var firstSpin = IndexOf(list, c => Math.Abs(c.SpinDeg) > 0 && c.TiltUpDeg == 0 && c.TiltSideDeg == 0);
        var firstTilt = IndexOf(list, c => Math.Abs(c.TiltUpDeg) + Math.Abs(c.TiltSideDeg) > 0);

        Assert.True(firstSpin < firstTilt, "spin 후보가 틸트 후보보다 뒤에 있습니다.");
    }

    // ── 탐색 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 이상값이_통과하면_기울이지_않는다()
    {
        var input = Input();
        var ideal = SeamBaseTransform.Resolve(input);
        var ik = TiltDrivenIk(ideal.TargetPoseBase!, wristBase: 80, gain: 1.0);

        var plan = await ApproachPlanner.PlanAsync(input, ApproachSearchSpace.Default, Limits(), ik);

        Assert.True(plan.Feasible);
        Assert.Equal(0, plan.Candidate!.TiltUpDeg);
        Assert.Equal(0, plan.Candidate.TiltSideDeg);
        Assert.Equal(0, plan.Candidate.SpinDeg);
        Assert.Equal(1, plan.Tried);                         // 첫 후보에서 즉시 종료
    }

    [Fact]
    public async Task 손목_특이점에_걸리면_광축을_기울여_빠져나온다()
    {
        // 정면(틸트 0)에서 |J5| = 3° — 특이점. 1° 기울일 때마다 J5 가 1.5° 늘어난다.
        var input = Input();
        var ideal = SeamBaseTransform.Resolve(input);
        var ik = TiltDrivenIk(ideal.TargetPoseBase!, wristBase: 3, gain: 1.5);
        var space = ApproachSearchSpace.Default with { GoodEnoughMarginDeg = 1.0 };

        var plan = await ApproachPlanner.PlanAsync(input, space, Limits(wrist: 15), ik);

        Assert.True(plan.Feasible);
        Assert.True(plan.Margin!.MarginDeg > 0);

        // 10° 기울이면 |J5| = 18° → 여유 3° 로 통과한다. 비용 순서상 더 크게 기울인 해가 먼저 채택될 수 없다.
        var tilt = Math.Abs(plan.Candidate!.TiltUpDeg) + Math.Abs(plan.Candidate.TiltSideDeg);
        Assert.Equal(10, tilt);
        Assert.Contains(plan.Notes, n => n.Contains("기울였습니다"));
    }

    [Fact]
    public async Task spin_만으로는_손목_특이점을_벗어나지_못한다()
    {
        // 광축 방향을 바꾸지 않는 spin 후보는 J5 가 그대로다 — 채택 해는 반드시 틸트를 포함해야 한다.
        var input = Input();
        var ideal = SeamBaseTransform.Resolve(input);
        var ik = TiltDrivenIk(ideal.TargetPoseBase!, wristBase: 3, gain: 1.5);

        var plan = await ApproachPlanner.PlanAsync(input, ApproachSearchSpace.Default, Limits(wrist: 15), ik);

        Assert.True(Math.Abs(plan.Candidate!.TiltUpDeg) + Math.Abs(plan.Candidate.TiltSideDeg) > 0);
    }

    [Fact]
    public async Task 어떤_자세도_안_되면_정차위치_변경을_안내한다()
    {
        var input = Input();
        var ideal = SeamBaseTransform.Resolve(input);
        var ik = TiltDrivenIk(ideal.TargetPoseBase!, wristBase: 1, gain: 0.0);   // 무엇을 해도 J5 = 1°

        var plan = await ApproachPlanner.PlanAsync(input, ApproachSearchSpace.Default, Limits(wrist: 15), ik);

        Assert.False(plan.Feasible);
        Assert.True(plan.Reachable > 0);
        Assert.Contains(plan.Notes, n => n.Contains("정차 위치"));
    }

    [Fact]
    public async Task 역기구학이_전부_실패하면_도달_불가로_보고한다()
    {
        var input = Input();
        Task<double[]?> Failing(double[] _, CancellationToken __) => throw new InvalidOperationException("112");

        var plan = await ApproachPlanner.PlanAsync(input, ApproachSearchSpace.Default, Limits(), Failing);

        Assert.False(plan.Feasible);
        Assert.Equal(0, plan.Reachable);
        Assert.Contains(plan.Notes, n => n.Contains("도달 불가"));
    }

    [Fact]
    public async Task wall_code_가_없으면_탐색하지_않는다()
    {
        var input = Input() with { WallCode = null };

        var plan = await ApproachPlanner.PlanAsync(input, ApproachSearchSpace.Default, Limits(),
            (_, _) => Task.FromResult<double[]?>(new[] { 0.0, -30, 90, -60, 90, 0 }));

        Assert.False(plan.Feasible);
        Assert.Equal(0, plan.Tried);
        Assert.Contains(plan.Notes, n => n.Contains("wall_code"));
    }

    [Fact]
    public async Task 현재_자세와_J5_부호가_다르면_경로_위험을_알린다()
    {
        // 목표는 J5 = +90 인데 현재가 −40 이면, 관절 보간 도중 J5 가 0 을 지난다.
        var input = Input();
        var ideal = SeamBaseTransform.Resolve(input);
        var ik = TiltDrivenIk(ideal.TargetPoseBase!, wristBase: 90, gain: 0.0);

        var plan = await ApproachPlanner.PlanAsync(input, ApproachSearchSpace.Default, Limits(), ik,
            fromJointsDeg: new[] { 0.0, -30, 90, -60, -40, 0 });

        Assert.True(plan.Feasible);
        Assert.False(plan.PathSafe);
        Assert.Contains(plan.Notes, n => n.Contains("손목 특이점을 지납니다"));
    }

    [Fact]
    public async Task 평가_상한을_넘기지_않는다()
    {
        var input = Input();
        var calls = new List<double[]>();
        var ideal = SeamBaseTransform.Resolve(input);
        var ik = TiltDrivenIk(ideal.TargetPoseBase!, wristBase: 1, gain: 0.0, queried: calls);
        var space = ApproachSearchSpace.Default with { MaxEvaluations = 7 };

        var plan = await ApproachPlanner.PlanAsync(input, space, Limits(), ik);

        Assert.Equal(7, calls.Count);
        Assert.Equal(7, plan.Tried);
    }
}
