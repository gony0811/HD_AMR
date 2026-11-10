using HD.AMR.App.Communication;
using HD.AMR.App.Data.Entities;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// Teaching 페이지 '이동' 버튼 — Web(Teaching.razor)·Desktop(TeachingViewModel) 공용.
/// 베이스 기준 위치는 <b>저장된 관절값으로 MoveJ</b> 한다. 홈 → 홈_1.5 → 홈_2 처럼 J3 부호가 바뀌는
/// (팔꿈치 자세가 뒤집히는) 위치는 MoveL 이 직선 경로 중 J3=0 특이점을 지나야 해 rc=38/112 로 갈 수 없고,
/// 자세(pose)에서 IK 를 다시 풀면 다른 팔꿈치 해로 갈 수 있으므로 캡처한 J1~J6 를 그대로 쓴다.
/// 작업물 좌표계 기준(UserFrame&gt;0) 위치는 프레임 재등록 시 목표가 바뀌어 저장 관절값이 무효라 기존 MoveL 유지.
/// </summary>
public static class TeachingMove
{
    /// <summary>관절 이동(MoveJ) 속도 비율 파라미터 키. 같은 속도 % 라도 MoveJ 는 관절 최대속도 기준이라
    /// MoveL 보다 약 2배 빠르다 — 이 비율을 화면 속도에 곱해 종전 MoveL 체감 속도에 맞춘다.</summary>
    public const string JointVelocityScaleKey = "Teaching.JointMoveVelocityScale";
    public const double DefaultJointVelocityScale = 0.45;

    /// <summary>비율 로드 — 없거나 (0, 1] 밖이면 기본값.</summary>
    public static async Task<double> LoadJointVelocityScaleAsync(ParameterService param)
        => await param.GetDoubleAsync(JointVelocityScaleKey) is > 0 and <= 1 and var s ? s : DefaultJointVelocityScale;

    /// <summary>true = 저장된 관절값으로 MoveJ 할 위치(베이스 기준 + 관절값 캡처됨).</summary>
    public static bool UsesJointMove(TeachingPosition p)
        => p.UserFrame is not > 0 && p.J1.HasValue && p.J2.HasValue && p.J3.HasValue
           && p.J4.HasValue && p.J5.HasValue && p.J6.HasValue;

    /// <summary>홈 → 홈 2 자세 전환 경유 순서(홈 2 → 홈 은 역순). 홈에서 홈 2 로 바로 MoveJ 하면 관절 보간 경로에서
    /// J5 가 AMR 차체와 충돌하므로(2026-11-09 현장) 이 순서로 경유한다. ready 는 미티칭이면 건너뛰고, 홈 1.5·1.6 은 필수.</summary>
    public static readonly string[] HomeRouteKeys = { "home", "ready", "home15", "home16", "home2" };

    /// <summary>현재 관절이 홈/홈 2 에 '있다'고 보는 관절 허용오차 [도] (최대 관절 차).</summary>
    internal const double RouteSnapToleranceDeg = 2.0;

    /// <summary>경로 계획 결과. <see cref="Error"/> 가 있으면 움직이지 않는다.</summary>
    public sealed record RoutePlan(IReadOnlyList<TeachingPosition> Steps, string? Error = null)
    {
        public bool IsVia => Steps.Count > 1;
    }

    private static string SlotName(string key)
        => TeachingService.Slots.FirstOrDefault(x => x.Key == key).Name ?? key;

    private static double[] Joints(TeachingPosition p)
        => new[] { p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value };

    /// <summary>최대 관절 차 [도] — ±360° 감김은 같은 각으로 본다.</summary>
    internal static double JointDistance(double[] a, double[] b)
    {
        var max = 0.0;
        for (var i = 0; i < 6; i++)
        {
            var d = (a[i] - b[i]) % 360.0;
            if (d > 180.0) d -= 360.0;
            else if (d < -180.0) d += 360.0;
            max = Math.Max(max, Math.Abs(d));
        }
        return max;
    }

    /// <summary>
    /// 목표까지의 이동 순서를 정한다(2026-11-09 운영안). 경유는 <b>홈 ↔ 홈 2 사이만</b> 한다:
    /// · 현재 홈(±<see cref="RouteSnapToleranceDeg"/>°)에서 홈 2 로 → 작업 준비 → 홈 1.5 → 홈 1.6 → 홈 2.
    /// · 현재 홈 2 에서 홈 으로 → 그 역순.
    /// · 그 밖(검사 자세·경유점·임의 자세에서 홈 2 등)은 목표로 바로 이동(종전 동작).
    /// 경유가 필요한데 홈 1.5·1.6 이 미티칭이면 움직이지 않고 사유를 돌려준다.
    /// </summary>
    public static RoutePlan PlanRoute(TeachingPosition target, double[] cur, IEnumerable<TeachingPosition> all)
    {
        string from;
        if (target.Key == "home2") from = "home";
        else if (target.Key == "home") from = "home2";
        else return new([target]);
        if (!UsesJointMove(target)) return new([target]);

        var byKey = all.Where(p => p.IsTaught && UsesJointMove(p))
                       .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        if (!byKey.TryGetValue(from, out var start) || JointDistance(cur, Joints(start)) > RouteSnapToleranceDeg)
            return new([target]);

        var middle = HomeRouteKeys.Skip(1).Take(HomeRouteKeys.Length - 2);
        if (from == "home2") middle = middle.Reverse();
        var steps = new List<TeachingPosition>();
        foreach (var key in middle)
        {
            if (byKey.TryGetValue(key, out var via)) { steps.Add(via); continue; }
            if (key == "ready") continue; // 선택 경유점
            return new([target],
                $"'{target.Name}' 으로 가려면 경유점 '{SlotName(key)}' 이 필요합니다 — 바로 가면 J5 가 차체와 " +
                "충돌할 수 있습니다. Teaching 에서 먼저 저장하세요.");
        }
        steps.Add(target);
        return new(steps);
    }

    /// <summary>홈/홈 2 도착 후 리프트를 내리는 높이 [mm].</summary>
    public const int HomeLiftHeightMm = 0;

    /// <summary>도착 후 리프트를 <see cref="HomeLiftHeightMm"/> 로 내리는 위치(홈 위치·홈 위치_2).</summary>
    public static bool LowersLiftOnArrival(TeachingPosition target) => target.Key is "home" or "home2";

    /// <summary>경로 이동 결과. Rc≠0 이면 <see cref="FailedAt"/> 에서 실패, <see cref="Error"/> 가 있으면 움직이지 않았다.
    /// <see cref="Lift"/> 는 홈/홈 2 도착 후 리프트 하강 결과(대상 아니거나 코봇 이동 실패면 null).</summary>
    public sealed record RouteMoveResult(int Rc, RoutePlan Plan, TeachingPosition? FailedAt = null,
        TelescopicMoveResult? Lift = null)
    {
        public bool CobotOk => Rc == 0 && Plan.Error is null;
        public bool Ok => CobotOk && Lift is not { Ok: false };
    }

    /// <summary>티칭 위치로 이동 — 홈 ↔ 홈 2 전환이면 <see cref="PlanRoute"/> 경로대로 경유한다.</summary>
    /// <param name="all">티칭 위치 전체(경유점 조회용).</param>
    /// <param name="onStep">각 구간 이동 직전에 불린다(진행 표시용).</param>
    /// <param name="lift">주면 홈/홈 2 도착 후 리프트를 <see cref="HomeLiftHeightMm"/> 로 내리고 도달까지 기다린다.</param>
    /// <param name="onLift">리프트 하강 직전에 불린다(진행 표시용).</param>
    public static async Task<RouteMoveResult> MoveAlongRouteAsync(CobotService cobot, IEnumerable<TeachingPosition> all,
        TeachingPosition target, int tool, double vel, double jointVelScale, Action<TeachingPosition>? onStep,
        CancellationToken ct, ILogger? logger = null, TelescopicService? lift = null, Action? onLift = null)
    {
        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        var plan = PlanRoute(target, cur, all);
        var curText = string.Join(", ", cur.Select(v => v.ToString("0.0")));
        if (plan.Error is not null)
        {
            logger?.LogWarning("[Teaching] '{Target}' 이동 거부 — {Error} (현재 관절 [{Cur}])", target.Name, plan.Error, curText);
            return new(-1, plan);
        }
        logger?.LogInformation("[Teaching] '{Target}' 이동 시작 — 경로 {Route} (현재 관절 [{Cur}], 속도 {Vel}%)",
            target.Name, string.Join(" → ", plan.Steps.Select(s => s.Name)), curText, vel);
        foreach (var step in plan.Steps)
        {
            onStep?.Invoke(step);
            var rc = await MoveToAsync(cobot, step, tool, vel, jointVelScale, ct);
            if (rc != 0)
            {
                logger?.LogWarning("[Teaching] '{Step}' 이동 실패 (rc={Rc}){Desc} — 목표 '{Target}'",
                    step.Name, rc, FairinoErrorCodes.Suffix(rc), target.Name);
                return new(rc, plan, step);
            }
        }
        logger?.LogInformation("[Teaching] '{Target}' 이동 완료", target.Name);

        if (lift is null || !LowersLiftOnArrival(target))
            return new(0, plan);

        // 홈/홈 2 = 코봇이 접힌 기준 자세 — 도착을 확인한 뒤에만 리프트를 내린다(이동 실패 시 위에서 이미 반환).
        onLift?.Invoke();
        logger?.LogInformation("[Teaching] '{Target}' 도착 — 리프트 {Height}mm 하강", target.Name, HomeLiftHeightMm);
        var lr = await lift.MoveToHeightAndWaitAsync(HomeLiftHeightMm, ct: ct);
        if (lr.Ok)
            logger?.LogInformation("[Teaching] 리프트 하강 완료 — {Msg}", lr.Message);
        else
            logger?.LogWarning("[Teaching] 리프트 하강 실패 — {Msg}", lr.Message);
        return new(0, plan, Lift: lr);
    }

    /// <summary>화면 표시용 결과 문구(Web·Desktop 공용).</summary>
    public static string Describe(RouteMoveResult r, TeachingPosition target)
    {
        var via = r.Plan.IsVia ? $" (경유: {string.Join(" → ", r.Plan.Steps.Select(s => s.Name))})" : "";
        if (r.Plan.Error is { } err) return err;
        if (!r.CobotOk) return $"'{r.FailedAt?.Name}' 이동 실패 (rc={r.Rc}){FairinoErrorCodes.Suffix(r.Rc)}{via}";
        var liftText = r.Lift switch
        {
            null => "",
            { Ok: true, Moved: false } => $" 리프트 이미 {HomeLiftHeightMm}mm.",
            { Ok: true } l => $" 리프트 {HomeLiftHeightMm}mm 하강 완료 (현재 {l.FinalHeightMm}mm).",
            { } l => $" 리프트 {HomeLiftHeightMm}mm 하강 실패 — {l.Message}",
        };
        return $"'{target.Name}' 위치로 이동 완료.{via}{liftText}";
    }

    /// <summary>티칭 위치로 이동. 반환 rc(0=성공).</summary>
    /// <param name="vel">화면 속도(%). MoveJ 일 때는 <paramref name="jointVelScale"/> 를 곱해 보낸다.</param>
    public static async Task<int> MoveToAsync(CobotService cobot, TeachingPosition p, int tool, double vel,
                                              double jointVelScale, CancellationToken ct)
    {
        var pose = new[] { p.X!.Value, p.Y!.Value, p.Z!.Value, p.Rx!.Value, p.Ry!.Value, p.Rz!.Value };

        if (UsesJointMove(p))
        {
            var joints = new[] { p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value };
            // 판정·저장은 ±180° 범위인데 원값을 지령하면 같은 자세에서도 J6 가 한 바퀴 돈다 — 현재 관절 쪽으로 감기 보정.
            var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
            // 캡처 공구와 이동 공구가 다르면 저장 pose 가 관절값과 맞지 않으므로 0 배열 → 정기구학으로 채운다.
            var desc = p.Tool == tool ? pose : new double[6];
            return await cobot.Rpc.MoveJAsync(FairinoRpcClient.UnwrapToward(joints, cur), desc,
                tool: tool, user: 0, vel: vel * jointVelScale, ct: ct);
        }

        double[] target = pose;
        if (p.UserFrame is int n && n > 0 && p.RelX.HasValue)
        {
            // 작업물 추종: 현재(재등록된) 프레임 T_N 에 상대 pose 적용 → 베이스 목표.
            var tN = await cobot.Rpc.GetWObjCoordAsync(n, ct);
            var rel = new[] { p.RelX!.Value, p.RelY!.Value, p.RelZ!.Value, p.RelRx!.Value, p.RelRy!.Value, p.RelRz!.Value };
            target = FrameMath.FromFrame(rel, tN);
        }
        return await cobot.Rpc.MoveLAsync(target, tool: tool, user: 0, vel: vel, ct: ct);
    }
}
