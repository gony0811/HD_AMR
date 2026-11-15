using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.App.Service.Inspection;

public enum BundleRole { None, Leader, Follower }

/// <summary>노드 안 검사 액션 하나. 방향이 null 이면 수직/수평을 확정하지 못한 것 — 묶지 않는다.</summary>
public sealed record BundleTaskInput(
    string ActionId,
    string JobRef,
    string? WallCode,
    double? LiftStrokeMm,
    InspectionMoveDirection? Direction,
    double[]? SeamStartW,
    double[]? SeamEndW);

/// <summary>묶음에서의 역할. 리더만 점1·점2 교시를 하고, 팔로워는 그 좌표계를 재사용한다.</summary>
public sealed record BundleAssignment(
    string ActionId,
    string JobRef,
    BundleRole Role,
    string? BundleId,
    string? WallCode,
    double? LiftStrokeMm,
    InspectionMoveDirection? Direction,
    double[]? FirstStartW,
    double[]? FirstEndW,
    double[]? LastStartW);

/// <summary>
/// 연속 TASK 묶음. 리프트 높이·wall_code·수직/수평이 모두 같고, 방향 판별이 확실한 액션만
/// 노드 순서대로 인접할 때 묶는다. 하나라도 다르거나 판별이 안 되면 그 자리는 묶음을 끊는다.
/// </summary>
public static class TaskBundlePlanner
{
    public const double LiftToleranceMm = 1.0;

    public static IReadOnlyList<BundleTaskInput> Read(IEnumerable<VdaAction> actions, double? nodeThetaRad)
    {
        var list = new List<BundleTaskInput>();
        foreach (var action in actions)
        {
            if (action.ActionType != "startWeldInspection") continue;
            if (!WeldInspectionActionParser.TryParse(action, out var req, out _) || req is null)
            {
                list.Add(new BundleTaskInput(action.ActionId, action.ActionId, null, null, null, null, null));
                continue;
            }
            var ok = SeamDirectionResolver.TryClassify(req.SeamStartW, req.SeamEndW, nodeThetaRad, out var dir, out _);
            list.Add(new BundleTaskInput(
                action.ActionId, req.JobRef, req.DrawingPos.WallCode, req.LiftStrokeMm,
                ok ? dir : null, req.SeamStartW, req.SeamEndW));
        }
        return list;
    }

    public static IReadOnlyList<BundleAssignment> Assign(IReadOnlyList<BundleTaskInput> tasks)
    {
        var result = new BundleAssignment[tasks.Count];
        var i = 0;
        while (i < tasks.Count)
        {
            var j = i + 1;
            while (j < tasks.Count && CanJoin(tasks[i], tasks[j])) j++;
            if (j - i >= 2 && tasks[i].SeamStartW is { Length: 3 } && tasks[j - 1].SeamStartW is { Length: 3 })
            {
                var id = tasks[i].ActionId;
                var first = tasks[i];
                var last = tasks[j - 1];
                for (var k = i; k < j; k++)
                {
                    result[k] = new BundleAssignment(
                        tasks[k].ActionId, tasks[k].JobRef,
                        k == i ? BundleRole.Leader : BundleRole.Follower,
                        id, first.WallCode, first.LiftStrokeMm, first.Direction,
                        Clone3(first.SeamStartW), Clone3(first.SeamEndW), Clone3(last.SeamStartW));
                }
            }
            else
            {
                for (var k = i; k < j; k++)
                    result[k] = Solo(tasks[k]);
            }
            i = j;
        }
        return result;
    }

    public static IReadOnlyList<BundleAssignment> Assign(IEnumerable<VdaAction> actions, double? nodeThetaRad)
        => Assign(Read(actions, nodeThetaRad));

    /// <summary>
    /// 첫 TASK 시작→끝을 진행 방향으로, 마지막 TASK 시작점까지 몇 피치인지. 최소 1.
    /// 부호는 진행 방향과 같으면 +1, 반대면 −1 (호출측이 티칭된 pitch 부호에 곱한다).
    /// </summary>
    public static (int Count, int GeomSign) PitchPlan(
        double[] firstStartW, double[] firstEndW, double[] lastStartW, double pitchMm)
    {
        var dx = firstEndW[0] - firstStartW[0];
        var dy = firstEndW[1] - firstStartW[1];
        var dz = firstEndW[2] - firstStartW[2];
        var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var vx = lastStartW[0] - firstStartW[0];
        var vy = lastStartW[1] - firstStartW[1];
        var vz = lastStartW[2] - firstStartW[2];
        if (len < 1e-9 || pitchMm <= 1)
            return (1, 1);
        var alongM = (vx * dx + vy * dy + vz * dz) / len;
        var sign = alongM >= 0 ? 1 : -1;
        var n = (int)Math.Round(Math.Abs(alongM) * 1000.0 / pitchMm);
        return (Math.Max(1, n), sign);
    }

    /// <summary>
    /// 리더가 실패해 공통 좌표계를 남기지 못한 묶음을, 실패한 TASK 를 빼고 <b>남은 TASK 끼리 다시 묶는다</b>.
    /// <paramref name="newLeaderActionId"/>(지금 시작하려는 팔로워)가 새 리더가 되어 자기 시작점(점1)과 묶음의
    /// 마지막 TASK 시작점(점2)으로 다시 교시하고, 그 뒤 TASK 는 새 묶음의 팔로워가 된다.
    /// 남은 TASK 가 새 리더 하나뿐이면 묶을 것이 없으므로 null(단독 교시).
    /// </summary>
    /// <param name="ordered">노드 액션 순서의 배정 목록(<see cref="Assign(IReadOnlyList{BundleTaskInput})"/> 결과).</param>
    /// <returns>바뀐 배정만(새 리더 + 뒤따르는 팔로워). 묶을 수 없으면 null.</returns>
    public static IReadOnlyList<BundleAssignment>? Rebundle(
        IReadOnlyList<BundleAssignment> ordered, string bundleId, string newLeaderActionId,
        double[]? leaderStartW, double[]? leaderEndW)
    {
        if (leaderStartW is not { Length: 3 } || leaderEndW is not { Length: 3 }) return null;

        var members = ordered.Where(a => a.Role != BundleRole.None && a.BundleId == bundleId).ToList();
        var idx = members.FindIndex(a => a.ActionId == newLeaderActionId);
        if (idx < 0 || idx == members.Count - 1) return null;          // 묶음 밖이거나 마지막 하나만 남음
        var last = members[^1];
        if (last.LastStartW is not { Length: 3 }) return null;

        var result = new List<BundleAssignment>(members.Count - idx);
        for (var k = idx; k < members.Count; k++)
            result.Add(members[k] with
            {
                Role = k == idx ? BundleRole.Leader : BundleRole.Follower,
                BundleId = newLeaderActionId,
                FirstStartW = Clone3(leaderStartW),
                FirstEndW = Clone3(leaderEndW),
                LastStartW = Clone3(last.LastStartW),
            });
        return result;
    }

    /// <summary>묶음 리더의 점2(마지막 TASK 시작점)를 도달 점검에 한 점 더 넣는다.</summary>
    public static IReadOnlyList<ReachPoint> Point2ReachPoints(IReadOnlyList<BundleAssignment> assignments, bool allowBackup)
        => assignments.Where(a => a.Role == BundleRole.Leader && a.LastStartW is { Length: 3 })
            .Select(a => new ReachPoint(
                $"{a.JobRef} 묶음 점2", a.WallCode, a.LiftStrokeMm, a.LastStartW!,
                TestMove: false, AllowBackup: allowBackup))
            .ToList();

    private static bool CanJoin(BundleTaskInput origin, BundleTaskInput next)
    {
        if (origin.Direction is null || next.Direction is null || origin.Direction != next.Direction)
            return false;
        if (string.IsNullOrWhiteSpace(origin.WallCode)
            || !string.Equals(origin.WallCode, next.WallCode, StringComparison.OrdinalIgnoreCase))
            return false;
        if (origin.LiftStrokeMm is null || next.LiftStrokeMm is null
            || Math.Abs(origin.LiftStrokeMm.Value - next.LiftStrokeMm.Value) > LiftToleranceMm)
            return false;
        return origin.SeamStartW is { Length: 3 } && next.SeamStartW is { Length: 3 }
               && origin.SeamEndW is { Length: 3 }
               && SameSeamLine(origin.SeamStartW, origin.SeamEndW, next.SeamStartW, next.SeamEndW);
    }

    /// <summary>두 용접선을 같은 줄로 보는 방향 차 한계 [도].</summary>
    public const double SeamParallelTolDeg = 20.0;

    /// <summary>
    /// 두 용접선(시작→끝, 맵 좌표 m)이 같은 방향인지 — 좌표계를 함께 쓸 수 있는 조건. 방향 차가
    /// <see cref="SeamParallelTolDeg"/> 이하(같은 진행 방향)일 때만 true. 끝점이 없거나 길이가 0 이면 false.
    /// 수직/수평 분류만으로는 AMR 이 용접선에 비스듬히(45° 부근) 섰을 때 직교하는 두 선을 가르지 못한다.
    /// </summary>
    public static bool SameSeamLine(double[]? startA, double[]? endA, double[]? startB, double[]? endB)
    {
        if (startA is not { Length: 3 } || endA is not { Length: 3 } || startB is not { Length: 3 } || endB is not { Length: 3 })
            return false;
        double ax = endA[0] - startA[0], ay = endA[1] - startA[1], az = endA[2] - startA[2];
        double bx = endB[0] - startB[0], by = endB[1] - startB[1], bz = endB[2] - startB[2];
        var la = Math.Sqrt(ax * ax + ay * ay + az * az);
        var lb = Math.Sqrt(bx * bx + by * by + bz * bz);
        if (la < 1e-6 || lb < 1e-6) return false;
        var cos = (ax * bx + ay * by + az * bz) / (la * lb);
        return cos >= Math.Cos(SeamParallelTolDeg * Math.PI / 180.0);
    }

    private static BundleAssignment Solo(BundleTaskInput t) => new(
        t.ActionId, t.JobRef, BundleRole.None, null, t.WallCode, t.LiftStrokeMm, t.Direction,
        null, null, null);

    private static double[]? Clone3(double[]? v) => v is { Length: 3 } a ? (double[])a.Clone() : null;
}
