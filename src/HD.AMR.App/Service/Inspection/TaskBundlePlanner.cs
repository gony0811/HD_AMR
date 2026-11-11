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
               && origin.SeamEndW is { Length: 3 };
    }

    private static BundleAssignment Solo(BundleTaskInput t) => new(
        t.ActionId, t.JobRef, BundleRole.None, null, t.WallCode, t.LiftStrokeMm, t.Direction,
        null, null, null);

    private static double[]? Clone3(double[]? v) => v is { Length: 3 } a ? (double[])a.Clone() : null;
}
