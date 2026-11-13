using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Motion;

/// <summary>사전 점검할 용접선 점 하나. 좌표는 맵(m).</summary>
/// <param name="Label">로그·배너에 붙는 이름(jobRef + 시작/끝/시험이동).</param>
/// <param name="WallCode">면 코드. 없거나 미정의면 그 점을 도달 불가로 본다.</param>
/// <param name="StrokeMm">그 작업의 리프트 높이. null 이면 현재 높이를 쓰고, 현재도 모르면 막는다.</param>
/// <param name="SeamW">용접선 점 [x,y,z] m.</param>
/// <param name="TestMove">코봇 시험이동(moveToSeamStart)이면 true — 실패를 오류(적색)로 알린다.</param>
/// <param name="AllowBackup">자동 시퀀스에서 뻗음 부족일 때 정차 보정 후진 자리를 볼지. 시험이동은 false.</param>
public sealed record ReachPoint(
    string Label,
    string? WallCode,
    double? StrokeMm,
    double[] SeamW,
    bool TestMove,
    bool AllowBackup);

/// <summary>사전 점검 결과. <see cref="Ok"/> 가 false 면 호출측은 AMR·리프트를 움직이지 않는다.</summary>
/// <param name="AsError">true 면 시험이동 불가(오류 로그·적색 배너). false 면 자동 시퀀스 경고.</param>
public sealed record ReachGateResult(bool Ok, string Message, bool AsError)
{
    public static ReachGateResult Pass(string message) => new(true, message, false);
    public static ReachGateResult Block(string message, bool asError) => new(false, message, asError);
}

/// <summary>
/// 다가올 작업의 코봇 도달·순회를, AMR 과 리프트를 움직이기 전에 판정한다.
/// 순회는 리치 맵과 같다 — ② 접근, 정렬 준비(레이저 WD), 초점(면 위)을 한 가지로 이어 풀고
/// 자세 한계·자기 간섭·손목 경로까지 본다. 시작점만 보면 시험이동, 시작·끝 둘 다 보면 자동 시퀀스다.
///
/// 판정 출처는 둘 중 하나다. 실행 폴더 <c>reachmaps</c> 의 최신 CSV 에 같은 장비 조건의 셀이 있으면 그 결과를 쓰고,
/// 없으면 그 점 한 칸을 새로 계산한다(코봇 연결 시 컨트롤러 IK, 미연결이고 광축이 +Z 일 때만 오프라인 모델).
/// 장착 보정·광축·리프트 높이·wall 처럼 시뮬레이션을 돌릴 자료가 없으면 경고하고 막는다.
/// 통과하면 알람을 울리지 않는다.
/// </summary>
public sealed class CobotReachPrecheck
{
    /// <summary>VDA state.errors 와 액션 실패에 쓰는 errorType. 설비 고장(equipmentError)이 아니므로 장비를 잠그지 않는다.</summary>
    public const string BlockedErrorType = "cobotReachUnreachable";

    private readonly ReachMapService _maps;
    private readonly CalibrationService _calib;
    private readonly PostureLimitsService _limits;
    private readonly ParameterService _param;
    private readonly TelescopicService _lift;
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly OperatorAlarmState _alarm;
    private readonly OperationLogService _opLog;
    private readonly SequenceMonitorService _monitor;
    private readonly ILogger<CobotReachPrecheck> _logger;

    public CobotReachPrecheck(ReachMapService maps, CalibrationService calib, PostureLimitsService limits,
        ParameterService param, TelescopicService lift, AMRService amr, CobotService cobot,
        OperatorAlarmState alarm, OperationLogService opLog, SequenceMonitorService monitor,
        ILogger<CobotReachPrecheck> logger)
    {
        _maps = maps;
        _calib = calib;
        _limits = limits;
        _param = param;
        _lift = lift;
        _amr = amr;
        _cobot = cobot;
        _alarm = alarm;
        _opLog = opLog;
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>
    /// 노드 액션에서 점검할 점을 고른다. 시험이동은 시작점만(끝점은 방향 참고라 가지 않는다).
    /// 자동 시퀀스는 시작·끝 둘 다. 해석이 안 되는 액션은 빼 둔다 — 그 실패는 기존 파서 경로가 보고한다.
    /// </summary>
    public static IReadOnlyList<ReachPoint> FromActions(IEnumerable<VdaAction> actions, bool allowInspectionBackup)
    {
        var list = new List<ReachPoint>();
        foreach (var action in actions)
        {
            if (action.ActionType == "moveToSeamStart"
                && WeldInspectionActionParser.TryParseSeamStart(action, out var test, out _)
                && test is not null)
            {
                list.Add(new ReachPoint($"{test.JobRef} 시험이동", test.DrawingPos.WallCode, test.LiftStrokeMm,
                    test.SeamStartW, TestMove: true, AllowBackup: false));
            }
            else if (action.ActionType == "startWeldInspection"
                     && WeldInspectionActionParser.TryParse(action, out var req, out _)
                     && req is not null)
            {
                list.Add(new ReachPoint($"{req.JobRef} 시작", req.DrawingPos.WallCode, req.LiftStrokeMm,
                    req.SeamStartW, TestMove: false, AllowBackup: allowInspectionBackup));
                list.Add(new ReachPoint($"{req.JobRef} 끝", req.DrawingPos.WallCode, req.LiftStrokeMm,
                    req.SeamEndW, TestMove: false, AllowBackup: allowInspectionBackup));
            }
        }
        return list;
    }

    /// <summary>지금 AMR 측위 자리에서 판정한다. 측위가 없으면 막는다(리프트를 올리기 전의 재확인).</summary>
    public async Task<ReachGateResult> CheckHereAsync(IReadOnlyList<ReachPoint> points, CancellationToken ct)
    {
        if (points.Count == 0) return ReachGateResult.Pass("");
        if (_amr.LatestStatus?.Pose is not { } pose)
            return ReachGateResult.Block(
                "AMR 측위가 없습니다 — 코봇 도달을 확인할 수 없어 리프트를 올리지 않고 중단합니다.",
                points.Any(p => p.TestMove));
        return await CheckAsync(points, pose.X, pose.Y, pose.Angle, backup: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="amrXm"/> 정차(맵 m, yaw rad)에서 점들을 판정한다.
    /// <paramref name="backup"/> 은 벽 반대 방향으로 물러난 자리(m). 뻗음 부족이고 그 점이 후진을 허용할 때만 본다.
    /// </summary>
    public async Task<ReachGateResult> CheckAsync(
        IReadOnlyList<ReachPoint> points,
        double amrXm, double amrYm, double amrYawRad,
        (double X, double Y)? backup,
        CancellationToken ct)
    {
        if (points.Count == 0) return ReachGateResult.Pass("");

        var (ctx, setupError) = await PrepareAsync(ct).ConfigureAwait(false);
        if (ctx is null)
            return ReachGateResult.Block(setupError!, points.Any(p => p.TestMove));

        var blocked = new List<(bool Test, string Reason)>();
        var waived = new List<string>();
        foreach (var point in points)
        {
            ct.ThrowIfCancellationRequested();
            var node = await JudgeAsync(ctx, point, amrXm, amrYm, amrYawRad, ct).ConfigureAwait(false);
            if (ReachPrecheckPolicy.Allows(node.Usable, node.Status, backupUsable: null) && node.Usable)
                continue;
            if (point.AllowBackup && node.Status == ReachStatus.FlangeShort && backup is { } back)
            {
                var alt = await JudgeAsync(ctx, point, back.X, back.Y, amrYawRad, ct).ConfigureAwait(false);
                if (ReachPrecheckPolicy.Allows(node.Usable, node.Status, alt.Usable))
                {
                    waived.Add($"{point.Label} 정차점은 뻗음 부족, 후진 자리는 도달");
                    continue;
                }
                blocked.Add((point.TestMove, $"{node.Reason} / 후진 자리도 {alt.Reason}"));
                continue;
            }
            blocked.Add((point.TestMove, node.Reason));
        }

        if (blocked.Count == 0)
        {
            var msg = waived.Count == 0
                ? $"코봇 도달·순회 사전 점검 통과 ({points.Count}점)."
                : $"코봇 도달·순회 사전 점검 통과 ({points.Count}점). 뻗음 부족 {waived.Count}점은 정차 보정 후진으로 해소 가능 — {string.Join(" | ", waived)}";
            _logger.LogInformation("{Msg}", msg);
            return ReachGateResult.Pass(msg);
        }

        var text = "코봇 도달·순회 불가 — AMR 이동과 리프트 상승을 하지 않습니다. " + string.Join(" | ", blocked.Select(b => b.Reason));
        return ReachGateResult.Block(text, blocked.Any(b => b.Test));
    }

    /// <summary>배너·시퀀스 모니터·운영 로그·앱 로그를 함께 남긴다. 통과 결과에는 아무것도 하지 않는다.</summary>
    public void Raise(ReachGateResult blocked, string source, string? correlationId)
    {
        if (blocked.Ok) return;
        _alarm.Raise(blocked.Message, blocked.AsError);
        _monitor.Log((blocked.AsError ? "❌ " : "⚠ ") + blocked.Message);
        _opLog.Log(source, OpCategory.Alarm, blocked.AsError ? "코봇 시험이동 불가" : "코봇 도달 경고",
            false, blocked.Message, correlationId);
        if (blocked.AsError)
            _logger.LogError("코봇 도달 사전 점검 실패: {Msg}", blocked.Message);
        else
            _logger.LogWarning("코봇 도달 사전 점검 실패: {Msg}", blocked.Message);
    }

    private async Task<(SimContext? Ctx, string? Error)> PrepareAsync(CancellationToken ct)
    {
        var mount = await _calib.GetMountAsync().ConfigureAwait(false);
        if (mount.All(v => Math.Abs(v) < 1e-9))
            return (null, "장착 보정(T_A_B)이 없습니다 — 코봇 도달 시뮬레이션을 할 수 없어 AMR 이동과 리프트 상승을 하지 않습니다.");

        var (axis, fromParam) = await WeldSequenceSupport.GetDepthAxisAsync(_param).ConfigureAwait(false);
        if (!fromParam)
            return (null, WeldSequenceSupport.OpticalAxisMissingMessage("도달 사전 점검")
                          + " AMR 이동과 리프트 상승을 하지 않습니다.");

        var limits = (await _limits.GetAsync().ConfigureAwait(false)).Normalized();
        if (!await _limits.IsConfiguredAsync().ConfigureAwait(false))
            _logger.LogWarning("도달 사전 점검: 자세 허용 범위가 저장되지 않아 기본값으로 판정합니다.");

        var approach = await CobotInspectionMoveStep.GetApproachStandoffMmAsync(_param, _logger).ConfigureAwait(false);
        // 공통 레이저 WD — 면별 값(…WorkingDistanceMm.{wall_code})은 점마다 JudgeAsync 에서 다시 찾는다.
        var (align, alignSource) = await LaserWorkingDistanceSetting.ResolveAsync(_param, wallCode: null).ConfigureAwait(false);
        if (alignSource.StartsWith("기본값(", StringComparison.Ordinal))
            _logger.LogWarning("도달 사전 점검: 레이저 WD 를 읽지 못해 {Mm}mm 로 판정합니다 — {Source}", align, alignSource);
        var zDatum = await _param.GetDoubleAsync(WeldSequenceSupport.ZDatumOffsetKey).ConfigureAwait(false) ?? 0;

        double toolLen;
        var useController = _cobot.IsConnected;
        if (useController)
        {
            try
            {
                toolLen = ApproachReach.ToolLengthMm(await _cobot.Rpc.GetToolCoordAsync(1, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (null, $"공구 #1 좌표를 읽지 못했습니다({ex.Message}) — 도달 시뮬레이션을 할 수 없어 AMR 이동과 리프트 상승을 하지 않습니다.");
            }
            if (toolLen < 1)
                return (null, "공구 #1 오프셋이 0 입니다 — 도달 시뮬레이션을 할 수 없어 AMR 이동과 리프트 상승을 하지 않습니다.");
        }
        else
        {
            toolLen = ApproachReach.ToolLengthMm(Fr5Kinematics.InspectionTool);
            _logger.LogInformation("코봇 미연결 — 도달 사전 점검은 저장된 리치 맵을 찾고, 없으면 광축 +Z 일 때만 오프라인 모델로 계산합니다.");
        }

        var stamp = new ReachMapStamp(approach, align, FocusMm: 0, toolLen, limits.MinFlangeReachMm, limits.WristMarginDeg, mount);
        return (new SimContext(stamp, axis, useController, zDatum), null);
    }

    private async Task<PointVerdict> JudgeAsync(SimContext ctx, ReachPoint point, double xm, double ym, double yaw, CancellationToken ct)
    {
        if (point.SeamW is not { Length: 3 })
            return PointVerdict.Fail(ReachStatus.OutOfRange, $"{point.Label}: 용접선 좌표가 없습니다.");
        if (string.IsNullOrWhiteSpace(point.WallCode) || WallCodes.Find(point.WallCode) is null)
            return PointVerdict.Fail(ReachStatus.OutOfRange, $"{point.Label}: wall_code '{point.WallCode ?? "(없음)"}' 을 알 수 없습니다.");

        double h;
        if (point.StrokeMm is { } requested)
            h = requested;
        else if (_lift.TryGetStrokeMm(out var liftWhy) is { } current)
            h = current;
        else
            return PointVerdict.Fail(ReachStatus.OutOfRange, $"{point.Label}: 리프트 높이를 알 수 없습니다 — {liftWhy}.");

        var body = ReachStationFrame.WorldToBodyMm(point.SeamW, xm, ym, yaw, ctx.ZDatumMm);
        var z = ReachStationFrame.CellZMm(point.WallCode, body[2]);

        // 정렬 준비 거리 = 이 면의 레이저 WD(면별 값이 있으면 그것) — ④⁺ 가 실제로 맞추는 거리와 같아야 한다.
        if (!ctx.AlignByWall.TryGetValue(point.WallCode!, out var alignMm))
        {
            (alignMm, _) = await LaserWorkingDistanceSetting.ResolveAsync(_param, point.WallCode).ConfigureAwait(false);
            ctx.AlignByWall[point.WallCode!] = alignMm;
        }
        var stamp = ctx.Stamp with { AlignMm = alignMm };

        var stored = ReachMapCsv.FindLatest(ReachMapService.OutputDirectory, stamp, point.WallCode!, h, body[0], body[1], z);
        if (stored is not null)
        {
            // 규칙 변경 전에 만든 맵의 천장 '뻗음부족' 칸 — 리치 맵은 IK·자세·경로를 모두 통과한 뒤에만 이 상태를
            // 붙이므로(ReachMap) 규칙을 빼면 그대로 도달이다.
            if (stored.Status == ReachStatus.FlangeShort && !ApproachReach.AppliesTo(point.WallCode))
                return FromCell(point, h, body[0], body[1], z, ReachStatus.Ok, true,
                    $"도달 — 천장은 최소 뻗음 규칙 제외(저장 맵: {stored.Reason})", "저장 맵");
            return FromCell(point, h, body[0], body[1], z, stored.Status, stored.Usable, stored.Reason, "저장 맵");
        }

        if (!ctx.UseController && ctx.Axis != ToolAxisDir.PlusZ)
            return PointVerdict.Fail(ReachStatus.OutOfRange,
                $"{point.Label}: 저장된 리치 맵에 이 점이 없고 코봇이 연결되어 있지 않습니다. " +
                $"광축이 {ctx.Axis} 라 오프라인 모델(+Z)로는 운영 도달을 대신 판정하지 않습니다. " +
                $"차체 x={body[0]:0} y={body[1]:0} z={z:0} mm, 리프트 {h:0} mm.");

        try
        {
            var spec = new ReachMapSpec(
                new[] { point.WallCode! }, new[] { h }, new[] { body[0] }, new[] { body[1] }, new[] { z },
                stamp.ApproachMm, stamp.AlignMm, stamp.FocusMm);
            var result = ctx.UseController
                ? await _maps.EvaluateAsync(spec, tool: 1, progress: null, announce: false, ct).ConfigureAwait(false)
                : await _maps.EvaluateOfflineAsync(spec, progress: null, announce: false, ct).ConfigureAwait(false);
            var cell = result.Cells[0];
            var via = ctx.UseController ? "컨트롤러 IK" : "오프라인 모델";
            return FromCell(point, h, body[0], body[1], z, cell.Status, cell.Usable, cell.Reason, via);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return PointVerdict.Fail(ReachStatus.OutOfRange, $"{point.Label}: 시뮬레이션 실패 — {ex.Message}");
        }
    }

    private static PointVerdict FromCell(ReachPoint point, double stroke, double x, double y, double z,
        ReachStatus status, bool usable, string reason, string via)
        => new(usable, status,
            $"{point.Label}: {ReachMapResult.Label(status)} ({reason}) — 차체 x={x:0} y={y:0} z={z:0} mm, 리프트 {stroke:0} mm [{via}]");

    private sealed record SimContext(ReachMapStamp Stamp, ToolAxisDir Axis, bool UseController, double ZDatumMm)
    {
        /// <summary>면별 정렬 준비 거리(레이저 WD) — 한 번의 점검 안에서 면마다 한 번만 읽는다.</summary>
        public Dictionary<string, double> AlignByWall { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record PointVerdict(bool Usable, ReachStatus Status, string Reason)
    {
        public static PointVerdict Fail(ReachStatus status, string reason) => new(false, status, reason);
    }
}
