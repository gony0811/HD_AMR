using HD.AMR.App.Service.Inspection;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 접근 자세 탐색 — "용접선을 standoff 거리에서 본다" 는 <b>과제 불변식은 유지한 채</b>, 남는 자유도를 훑어
/// 특이점·관절한계에 걸리지 않는 자세를 고른다.
///
/// <b>왜 필요한가.</b> 목표를 "광축 = 면 법선" 하나로 못박으면 관절각은 단 한 벌로 결정되고, 그 한 벌이
/// 손목 특이점(J5≈0)이나 툴 간섭 범위에 들어가면 할 수 있는 게 없다. 그런데 검사 성립 조건은 법선 정렬이
/// 아니라 "용접선이 광축 위, standoff 거리" 다. 그래서 다음이 전부 공짜(또는 거의 공짜) 자유도다:
///
///  · <b>틸트</b>(상하·좌우): 법선에서 몇 도 기울여 봐도 검사는 성립한다. <b>J5 를 바꾸는 유일한 수단</b>이다.
///  · <b>spin</b>(광축 둘레 회전): 영상이 도는 것 외에 검사에 영향이 없다.
///    ※ 주의 — 광축이 플랜지 Z 와 나란한 보통의 장착에서는 spin 이 <b>J5 를 바꾸지 못한다</b>. 광축 둘레 회전이
///      곧 마지막 관절(J6) 회전이라, 손목 각 J5 를 정하는 성분(R_3_6 의 3열)이 그대로이기 때문이다. 따라서
///      손목 특이점 회피는 틸트(또는 스트로크·정차위치)가 해야 하고, spin 은 J4/J6 관절한계·케이블용이다.
///      광축이 플랜지 Z 와 기울어져 장착됐다면 spin 도 J5 를 조금 움직인다 — 탐색이 그 경우도 같이 훑는다.
///  · <b>standoff</b>: 레시피 허용 범위 안에서 ±수십 mm.
///
/// 탐색은 "이상값에서 얼마나 벗어나는가"(cost) 순으로 진행해, <b>가장 덜 타협한 해</b>를 먼저 채택한다.
/// 역기구학은 컨트롤러가 풀어야 하므로 델리게이트로 주입받는다 — 하드웨어 없이 단위 테스트가 가능하다.
/// </summary>
public static class ApproachPlanner
{
    /// <summary>
    /// 후보를 cost 오름차순으로 만든다. cost = 틸트각 + spin + standoff 편차에 가중치를 준 값으로,
    /// "검사 품질을 덜 건드리는 순서" 를 뜻한다.
    /// </summary>
    public static IReadOnlyList<ApproachCandidate> Candidates(ApproachSearchSpace space)
    {
        var list = new List<ApproachCandidate>();
        foreach (var up in Distinct(space.TiltUpDeg))
        foreach (var side in Distinct(space.TiltSideDeg))
        foreach (var spin in Distinct(space.SpinDeg))
        foreach (var ds in Distinct(space.StandoffDeltaMm))
        {
            var cost = space.TiltCostPerDeg * (Math.Abs(up) + Math.Abs(side))
                     + space.SpinCostPerDeg * Math.Abs(spin)
                     + space.StandoffCostPerMm * Math.Abs(ds);
            list.Add(new ApproachCandidate(up, side, spin, ds, cost));
        }

        return list
            .OrderBy(c => c.Cost)
            .ThenBy(c => Math.Abs(c.TiltUpDeg) + Math.Abs(c.TiltSideDeg))
            .ThenBy(c => Math.Abs(c.SpinDeg))
            .ToList();
    }

    /// <summary>
    /// 후보를 순서대로 역기구학에 물어 보고, <paramref name="limits"/> 를 만족하는 자세를 고른다.
    /// </summary>
    /// <param name="baseInput">이상값(틸트 0·spin 기준값·표준 standoff) 기준 입력. 후보마다 여기서 파생한다.</param>
    /// <param name="inverseKin">BASE pose → 관절각[6]. 도달 불가면 null 반환 또는 예외(예외는 도달 불가로 센다).</param>
    /// <param name="fromJointsDeg">현재 관절각 — 주면 이동량과 <b>관절 경로 안전성</b>(J5 부호 유지)까지 본다.</param>
    public static async Task<ApproachPlan> PlanAsync(
        SeamBaseInput baseInput,
        ApproachSearchSpace space,
        PostureLimits limits,
        Func<double[], CancellationToken, Task<double[]?>> inverseKin,
        double[]? fromJointsDeg = null,
        CancellationToken ct = default)
    {
        var notes = new List<string>();

        // 자세를 만들 수 없으면(=wall_code 없음) 탐색할 대상 자체가 없다.
        var ideal = SeamBaseTransform.Resolve(baseInput);
        if (ideal.TargetPoseBase is null)
        {
            notes.Add("wall_code 가 없어 목표 자세가 만들어지지 않습니다 — 자세 탐색은 면 법선 자세에서만 의미가 있습니다.");
            return new ApproachPlan(false, false, null, ideal, null, null, 0, 0, notes);
        }

        var candidates = Candidates(space);
        ApproachPlan? best = null;
        var tried = 0;
        var reachable = 0;

        foreach (var c in candidates)
        {
            if (tried >= space.MaxEvaluations) { notes.Add($"평가 상한 {space.MaxEvaluations}개에서 중단했습니다."); break; }
            ct.ThrowIfCancellationRequested();
            tried++;

            var input = Apply(baseInput, c);
            SeamBaseTarget target;
            try { target = SeamBaseTransform.Resolve(input); }
            catch (ArgumentException) { continue; }
            if (target.TargetPoseBase is null) continue;

            double[]? joints;
            try { joints = await inverseKin(target.TargetPoseBase, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }                      // 도달 불가 — 다음 후보
            if (joints is not { Length: 6 }) continue;
            reachable++;

            var margin = limits.Evaluate(joints, target.PlanarDistanceMm, fromJointsDeg);
            var pathSafe = fromJointsDeg is not { Length: 6 } || limits.JointPathWithin(fromJointsDeg, joints);
            var plan = new ApproachPlan(margin.Feasible, pathSafe, c, target, joints, margin, tried, reachable, notes);

            if (margin.Feasible && pathSafe && margin.MarginDeg >= space.GoodEnoughMarginDeg)
                return plan with { Notes = Finish(notes, plan, space, candidates.Count) };

            if (Better(plan, best)) best = plan;
        }

        if (best is null)
        {
            notes.Add(reachable == 0
                ? "모든 후보가 역기구학 실패(도달 불가)입니다 — 정차 위치·텔레스코픽 높이를 바꿔야 합니다."
                : "쓸 수 있는 자세를 찾지 못했습니다.");
            return new ApproachPlan(false, false, null, ideal, null, null, tried, reachable, notes);
        }

        return best with { Tried = tried, Reachable = reachable, Notes = Finish(notes, best, space, candidates.Count) };
    }

    /// <summary>후보 파라미터를 이상값 입력에 입힌다. spin 은 사용자가 준 기준값에 더한다.</summary>
    public static SeamBaseInput Apply(SeamBaseInput baseInput, ApproachCandidate c) => baseInput with
    {
        TiltUpDeg = c.TiltUpDeg,
        TiltSideDeg = c.TiltSideDeg,
        ToolSpinDeg = baseInput.ToolSpinDeg + c.SpinDeg,
        StandoffMm = baseInput.StandoffMm + c.StandoffDeltaMm,
    };

    /// <summary>우선순위: ① 한계 통과 ② 경로 안전 ③ 여유 큰 것.</summary>
    private static bool Better(ApproachPlan a, ApproachPlan? b)
    {
        if (b is null) return true;
        if (a.Feasible != b.Feasible) return a.Feasible;
        if (a.PathSafe != b.PathSafe) return a.PathSafe;
        return (a.Margin?.MarginDeg ?? double.NegativeInfinity) > (b.Margin?.MarginDeg ?? double.NegativeInfinity);
    }

    private static IReadOnlyList<string> Finish(List<string> notes, ApproachPlan plan, ApproachSearchSpace space, int total)
    {
        var result = new List<string>(notes);
        if (plan.Candidate is { } c)
        {
            if (Math.Abs(c.TiltUpDeg) > 1e-9 || Math.Abs(c.TiltSideDeg) > 1e-9)
                result.Add($"법선 정면으로는 한계에 걸려 광축을 기울였습니다 — 상하 {c.TiltUpDeg:+0.0;-0.0;0}° / 좌우 {c.TiltSideDeg:+0.0;-0.0;0}°. " +
                           "검사 영상이 그만큼 비스듬해지니 레시피 허용 입사각을 확인하세요.");
            if (Math.Abs(c.SpinDeg) > 1e-9)
                result.Add($"광축 둘레로 {c.SpinDeg:+0.0;-0.0;0}° 돌렸습니다(영상 회전 — 검사 기하는 동일). J4/J6 관절한계 회피용입니다.");
            if (Math.Abs(c.StandoffDeltaMm) > 1e-9)
                result.Add($"standoff 를 {c.StandoffDeltaMm:+0;-0;0}mm 조정했습니다 — 레시피 목표거리 허용폭 안인지 확인하세요.");
        }

        if (!plan.Feasible)
            result.Add($"{total}개 후보 중 쓸 수 있는 자세가 없습니다 — 가장 나은 것도 {plan.Margin?.Limiting}. " +
                       "텔레스코픽 높이나 AMR 정차 위치를 바꾸는 것이 유일한 해법입니다(팔만으로는 해결되지 않습니다).");
        else if (!plan.PathSafe)
            result.Add("자세는 찾았지만 현재 자세에서 바로 관절 이동하면 J5 가 부호를 바꾸며 손목 특이점을 지납니다 — " +
                       "홈/대기 자세로 먼저 복귀한 뒤 이동하세요.");
        else if (plan.Margin is { MarginDeg: var m } && m < space.GoodEnoughMarginDeg)
            result.Add($"여유 {m:0.0}° — 목표({space.GoodEnoughMarginDeg:0}°)보다 빠듯합니다. 가장 빠듯한 제약: {plan.Margin.Limiting}.");

        return result;
    }

    private static IEnumerable<double> Distinct(double[]? values)
        => values is { Length: > 0 } ? values.Distinct() : new[] { 0.0 };
}

/// <summary>탐색 격자와 비용 가중치.</summary>
/// <param name="TiltUpDeg">시험할 상하 틸트 [도].</param>
/// <param name="TiltSideDeg">시험할 좌우 틸트 [도].</param>
/// <param name="SpinDeg">시험할 광축 둘레 회전 [도] — 기준 spin 에 더해진다.</param>
/// <param name="StandoffDeltaMm">시험할 standoff 편차 [mm].</param>
/// <param name="GoodEnoughMarginDeg">이 여유를 넘으면 즉시 채택하고 탐색을 끝낸다 [도].</param>
/// <param name="MaxEvaluations">역기구학 호출 상한 — 컨트롤러 왕복이라 무한 탐색은 금물.</param>
public sealed record ApproachSearchSpace(
    double[] TiltUpDeg,
    double[] TiltSideDeg,
    double[] SpinDeg,
    double[] StandoffDeltaMm,
    double GoodEnoughMarginDeg = 20.0,
    int MaxEvaluations = 80,
    double TiltCostPerDeg = 1.0,
    double SpinCostPerDeg = 0.05,
    double StandoffCostPerMm = 0.05)
{
    /// <summary>기본 격자 — 틸트 ±20° 까지, spin 8분할, standoff ±60mm.</summary>
    public static ApproachSearchSpace Default => new(
        TiltUpDeg: new[] { 0.0, -10, 10, -20, 20 },
        TiltSideDeg: new[] { 0.0, -10, 10, -20, 20 },
        SpinDeg: new[] { 0.0, 90, -90, 180, 45, -45 },
        StandoffDeltaMm: new[] { 0.0, -60, 60 });
}

/// <summary>후보 한 벌 — 이상값에서의 편차.</summary>
public sealed record ApproachCandidate(double TiltUpDeg, double TiltSideDeg, double SpinDeg, double StandoffDeltaMm, double Cost);

/// <summary>탐색 결과.</summary>
/// <param name="Feasible">한계를 만족하는 자세를 찾았는가.</param>
/// <param name="PathSafe">현재 자세에서 관절 이동으로 곧바로 갈 때 손목 특이점을 지나지 않는가.</param>
/// <param name="Candidate">채택한 후보(없으면 null).</param>
/// <param name="Target">채택한 후보의 환산 결과(없으면 이상값 기준).</param>
/// <param name="JointsDeg">채택한 자세의 관절각 [도].</param>
/// <param name="Margin">채택한 자세의 여유 평가.</param>
/// <param name="Tried">평가한 후보 수.</param>
/// <param name="Reachable">역기구학이 풀린 후보 수.</param>
/// <param name="Notes">사용자에게 보일 설명.</param>
public sealed record ApproachPlan(
    bool Feasible,
    bool PathSafe,
    ApproachCandidate? Candidate,
    SeamBaseTarget Target,
    double[]? JointsDeg,
    PostureMargin? Margin,
    int Tried,
    int Reachable,
    IReadOnlyList<string> Notes);
