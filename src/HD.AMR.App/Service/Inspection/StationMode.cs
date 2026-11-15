using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 정차 검사 방식 — `params.stationMode`(측면 정차 검사 사양 §3.1, ACS VDA 1.19).
/// FRONT 는 종전 정면 검사(노드 theta = 벽 정면). SIDE_* 는 AMR 이 벽과 나란히 서고 코봇 J1 만 ±90° 돌려 옆 벽을 본다.
/// </summary>
public enum StationMode
{
    /// <summary>정면 검사 — 노드 theta 가 곧 벽 방향(기존 동작). 값이 없으면 이것.</summary>
    Front = 0,

    /// <summary>측면 검사 — 벽이 AMR(theta 기준) 왼쪽. 벽 방향 = theta + 90°.</summary>
    SideLeft = 1,

    /// <summary>측면 검사 — 벽이 AMR(theta 기준) 오른쪽. 벽 방향 = theta − 90°.</summary>
    SideRight = 2,
}

/// <summary>
/// <see cref="StationMode"/> 해석·벽 방향 계산·좌/우 검증. 순수 계산만 한다(하드웨어 접촉 없음).
/// </summary>
public static class StationModes
{
    /// <summary>`params.stationMode` 키.</summary>
    public const string ParamKey = "stationMode";

    /// <summary>측면 검사 실패 사유 앞머리(사양 §7) — ACS 실적 대조 도구가 이 문구로 분류한다. <b>바꾸면 ACS 에 사전 통보.</b></summary>
    public const string ReachNormalPrefix = "SIDE_REACH_NORMAL";

    /// <inheritdoc cref="ReachNormalPrefix"/>
    public const string ReachAlongPrefix = "SIDE_REACH_ALONG";

    /// <inheritdoc cref="ReachNormalPrefix"/>
    public const string LrMismatchPrefix = "SIDE_LR_MISMATCH";

    /// <summary>좌/우 검증 최소 옆 거리 파라미터 키 [mm] (사양 §10, D4).</summary>
    public const string LrMinLateralKey = "Side.LrMinLateralMm";

    /// <summary><see cref="LrMinLateralKey"/> 기본값 [mm].</summary>
    public const double DefaultLrMinLateralMm = 200.0;

    /// <summary>측면 검사를 허용하는 면 — 수직벽 4종(사양 V3, ACS 확인 2026-11-15).</summary>
    public static readonly IReadOnlyList<string> SideWallCodes = new[] { "PM", "SM", "F", "A" };

    public static bool IsSide(this StationMode mode) => mode != StationMode.Front;

    /// <summary>계약 문자열(정본 대문자).</summary>
    public static string ToWire(this StationMode mode) => mode switch
    {
        StationMode.SideLeft => "SIDE_LEFT",
        StationMode.SideRight => "SIDE_RIGHT",
        _ => "FRONT",
    };

    /// <summary>계약 문자열 해석. null/빈 값 = FRONT. 미정의 값이면 false + 사유.</summary>
    public static bool TryParse(string? raw, out StationMode mode, out string? error)
    {
        error = null;
        mode = StationMode.Front;
        switch (raw)
        {
            case null or "":
            case "FRONT":
                return true;
            case "SIDE_LEFT":
                mode = StationMode.SideLeft;
                return true;
            case "SIDE_RIGHT":
                mode = StationMode.SideRight;
                return true;
            default:
                error = $"stationMode '{raw}' 미정의 (FRONT/SIDE_LEFT/SIDE_RIGHT)";
                return false;
        }
    }

    /// <summary>
    /// 벽 방향(면 법선의 방위각) [rad]. FRONT = theta, SIDE_LEFT = theta + π/2, SIDE_RIGHT = theta − π/2.
    /// AMR 자세(yaw)는 언제나 theta 이고, 이 값은 면 법선·검사 방향·후퇴 방향에만 쓴다.
    /// </summary>
    public static double WallFacing(double thetaRad, StationMode mode) => mode switch
    {
        StationMode.SideLeft => Normalize(thetaRad + Math.PI / 2),
        StationMode.SideRight => Normalize(thetaRad - Math.PI / 2),
        _ => thetaRad,
    };

    /// <inheritdoc cref="WallFacing(double, StationMode)"/>
    public static double? WallFacing(double? thetaRad, StationMode mode)
        => thetaRad is { } t ? WallFacing(t, mode) : null;

    /// <summary>측면 검사 대상 면(수직벽 4종)인가.</summary>
    public static bool IsSideWall(string? wallCode) => wallCode is not null && SideWallCodes.Contains(wallCode);

    /// <summary>
    /// 좌/우 검증(사양 §4). 정차 (x, y, theta) 기준 차체 좌표에서 용접선 두 끝점의 옆 성분(+ = 왼쪽)이 모두
    /// stationMode 쪽이고 <paramref name="minLateralMm"/> 이상이면 null, 아니면 사유(앞머리 없이)를 돌려준다.
    /// FRONT 는 검사하지 않는다(null).
    /// </summary>
    public static string? CheckLeftRight(StationMode mode, double nodeXm, double nodeYm, double thetaRad,
        double[] seamStartW, double[]? seamEndW, double minLateralMm)
    {
        if (!mode.IsSide()) return null;
        var sign = mode == StationMode.SideLeft ? 1.0 : -1.0;
        var side = mode == StationMode.SideLeft ? "왼쪽" : "오른쪽";

        var points = seamEndW is { Length: 3 } ? new[] { seamStartW, seamEndW } : new[] { seamStartW };
        foreach (var (p, label) in points.Zip(new[] { "시작점", "끝점" }))
        {
            // z 는 판정에 쓰지 않으므로 기준 보정 0.
            var body = ReachStationFrame.WorldToBodyMm(p, nodeXm, nodeYm, thetaRad, 0);
            var lateral = body[1] * sign;
            if (lateral < minLateralMm)
                return $"stationMode={mode.ToWire()} 인데 용접선 {label}이 차체 {side}에 있지 않습니다 " +
                       $"(차체 옆 성분 {body[1]:+0;-0;0}mm, + = 왼쪽, 필요 ≥ {minLateralMm:0}mm {side}). " +
                       "노드 theta 또는 stationMode 를 확인하세요.";
        }
        return null;
    }

    /// <summary>
    /// 한 노드의 검사 액션(startWeldInspection·moveToSeamStart)을 수신 검증한다(사양 §3.2 V1~V5).
    /// 문제없으면 null 과 그 정차의 방식을, 있으면 거부 사유를 돌려준다. 검사 액션이 없으면 FRONT.
    /// 위치 파싱 실패 등 다른 계약 위반은 여기서 판정하지 않는다(액션 실행 시 종전대로 거부).
    /// </summary>
    public static string? ValidateNode(OrderNode node, double minLateralMm, out StationMode mode)
    {
        mode = StationMode.Front;
        var modes = new List<(VdaAction Action, StationMode Mode)>();
        foreach (var a in node.Actions.Where(IsInspectionAction))
        {
            if (!WeldInspectionActionParser.TryParseStationMode(a, out var m, out var err))
                return err;   // V1
            modes.Add((a, m));
        }
        if (modes.Count == 0) return null;

        var distinct = modes.Select(m => m.Mode).Distinct().ToList();
        if (distinct.Count > 1)   // V2
            return "stationMode 혼재 (" +
                   string.Join(", ", modes.GroupBy(m => m.Mode).Select(g => $"{g.Key.ToWire()}×{g.Count()}")) +
                   ") — 한 정차의 검사 액션은 같은 값이어야 합니다";
        mode = distinct[0];
        if (!mode.IsSide()) return null;

        var pos = node.NodePosition;
        if (pos?.Theta is not { } theta)   // V4
            return $"측면 검사({mode.ToWire()})에 노드 theta 가 필요합니다";

        foreach (var (a, _) in modes)
        {
            if (!WeldInspectionActionParser.TryReadSeam(a, out var start, out var end, out var wall))
                continue;   // 위치 계약 위반은 액션 실행 시 종전 경로로 거부
            if (!IsSideWall(wall))   // V3
                return $"측면 검사({mode.ToWire()})는 수직벽(PM/SM/F/A)만 — wall_code={wall} (actionId={a.ActionId})";
            if (CheckLeftRight(mode, pos.X, pos.Y, theta, start!, end, minLateralMm) is { } lr)   // V5
                return $"{LrMismatchPrefix}: {lr} (actionId={a.ActionId})";
        }
        return null;
    }

    /// <summary>stationMode 를 싣는 검사 액션인가.</summary>
    public static bool IsInspectionAction(VdaAction a)
        => a.ActionType is "startWeldInspection" or "moveToSeamStart";

    private static double Normalize(double rad)
    {
        while (rad > Math.PI) rad -= 2 * Math.PI;
        while (rad <= -Math.PI) rad += 2 * Math.PI;
        return rad;
    }
}
