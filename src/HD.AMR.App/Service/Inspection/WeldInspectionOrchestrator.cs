using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// `startWeldInspection` 액션 실행 총괄 (singleton). 파이프라인:
///
///   파싱(<see cref="WeldInspectionActionParser"/>) → 레시피 매핑(<see cref="InspectionRecipeResolver"/>)
///   → scope 생성(scoped 서비스 경계 해소) → 레시피 로드(Enabled 게이트)
///   → 레시피에 지정된 InspectionProfile 조회(사전 티칭 경유점)
///   → SequenceContext 구성(ACS 필드 주입) → SequenceService.RunSequenceAsync
///   → 결과 → FINISHED / FAILED + errorType(orderValidationError·equipmentError·inspectionFailed).
///
/// anchorGroupId 캐시(사양 §8.1): 같은 (orderId, anchorGroupId)의 연속 액션이고 사이에 주행이 없었으면
/// 정렬 스텝군을 생략하고 ⑱ 검사 수행만 실행한다. 무효화: 주행 발생(<see cref="InvalidateAnchor"/>),
/// 시퀀스 실패, 그룹 변경, 신규 order.
/// </summary>
public sealed class WeldInspectionOrchestrator : IWeldInspectionExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CobotService _cobot;
    private readonly TelescopicService _lift;
    private readonly VisionInterfaceService _vision;
    private readonly ILogger<WeldInspectionOrchestrator> _logger;

    private readonly object _sync = new();
    private (string OrderId, string AnchorGroupId, InspectionMoveDirection Direction, string? WallCode)? _lastAnchor;   // wall_code 포함 — 꺾인 용접선(벽→챔퍼)이 같은 그룹·방향이어도 다른 면 프레임을 재사용하지 않는다
    private double[]? _lastInternalWorkpieceFrame;                 // 같은 anchor에서 재사용할 BASE 기준 내부 프레임
    private double[]? _lastAnchorStartBase;                        // 그 프레임을 쓴 task 의 ② 시작 접근점(BASE) — 다음 task 프레임 평행 이동 기준
    private double[]? _lastAnchorEndTcp;                           // 그 task 종료 시 TCP(BASE) — 다음 task 연속 이동 판정
    private double? _lastAnchorStrokeMm;                           // 그 프레임을 쓴 task 의 텔레스코픽 스트로크(mm) — 높이 변경 시 캐시 무효화
    private CancellationTokenSource? _currentRunCts;

    /// <summary>
    /// <b>정렬·작업물 좌표계 교시 스텝군(③~⑯)</b> — anchor 적중(같은 노드의 2번째 이후 task) 시 건너뛴다.
    ///
    /// 첫 task 가 ③~④⁺에서 맞춘 거리·평행도와 ⑤~⑯에서 계산한 내부 작업물 좌표계(T_BW)를 오케스트레이터가
    /// 캐시해 이후 task 에 복사하고, ②(<see cref="Sequence.Steps.CobotInspectionMoveStep"/>)가 그 프레임을
    /// 이번 용접선 시작점으로 평행 이동한 뒤 곧바로 ⑱ 검사 수행으로 넘어간다.
    /// </summary>
    private static readonly HashSet<string> AlignmentStepKeys = new(StringComparer.Ordinal)
    {
        "cameraAlign", "cameraAlignRefine", "flatSurfaceAlign", "laserWorkingDistance",
        // ⑤ Peak 탐색 준비 이동 — ⑤가 빠지면 의미가 없고, 남기면 ⑱ 직전 자세만 옮긴다.
        Sequence.Steps.DepthCamCenterShiftStep.StepKey,
        "peak1Find", "peak1Center", "bead1Find", "bead1Center", "wobjPoint1",
        "peak2Approach", "peak2Find", "peak2Center", "bead2Find", "bead2Center", "wobjPoint2",
        "wobjRegister",
        // 드라이런 2점 교시의 끝점 이동 — anchor 적중 시 캐시 프레임 재사용으로 재교시 생략.
        "cobotSeamEnd",
    };

    /// <summary>코봇 홈 복귀 스텝 — 노드의 <b>마지막</b> 검사 액션에서만 실행한다.</summary>
    private const string HomeStepKey = "cobotHome";

    /// <summary>드라이런 레시피(<see cref="Data.Entities.InspectionRecipe.DryRun"/>=true)가 돌릴 스텝 집합.
    /// 비전 정렬(③~⑯) 없이 ACS 용접선 시작/끝점으로 2점 교시(wobjPoint1→cobotSeamEnd→wobjPoint2)만 해
    /// 내부 작업물 좌표계를 잡고(wobjRegister), 경유점을 이동만 순회(inspectionRunDry)한 뒤 반납·홈 복귀한다.
    /// 순서는 각 스텝 DefaultOrder 가 정하므로(SequenceService) 배열 순서는 무관하다. StepKeysJson 은 무시된다.</summary>
    private static readonly string[] DryRunStepKeys =
    {
        "cobotInspection", "wobjPoint1", "cobotSeamEnd", "wobjPoint2",
        "wobjRegister", "inspectionRunDry", "wobjReset", "cobotHome", "monitorClose",
    };

    /// <summary>드라이런에서만 의미 있는 스텝 — 일반 레시피가 풀시퀀스(StepKeysJson 미지정)로 돌 때 제외한다.
    /// cobotSeamEnd 가 섞이면 bead2Center 직후 끝점으로 이동해 wobjPoint2 가 비전 Bead2 대신 그 위치를 기록한다.</summary>
    private static IReadOnlySet<string> DryRunOnlyStepKeys => SequenceService.DryRunOnlyStepKeys;

    private readonly OperationLogService _opLog;
    private readonly SequenceRunGate _runGate;

    public WeldInspectionOrchestrator(
        IServiceScopeFactory scopeFactory,
        CobotService cobot,
        TelescopicService lift,
        VisionInterfaceService vision,
        OperationLogService opLog,
        SequenceRunGate runGate,
        ILogger<WeldInspectionOrchestrator> logger)
    {
        _opLog = opLog;
        _runGate = runGate;
        _scopeFactory = scopeFactory;
        _cobot = cobot;
        _lift = lift;
        _vision = vision;
        _logger = logger;
    }

    /// <summary>`moveToSeamStart` 에서 실행할 단계 — 본검사 ② 검사위치 이동(ready 경유 → 면 이격 접근점)만.
    /// ③④는 카메라·레이저 실패가 reach 결과에 섞이므로 넣지 않는다. 홈 복귀도 하지 않는다(도달 자세 확인용 정지,
    /// 다음 order 는 주행 전 홈 복귀가 보장한다).</summary>
    private static readonly string[] SeamStartStepKeys = { "cobotInspection" };

    public async Task<InspectionActionResult> ExecuteMoveToSeamStartAsync(VdaAction action, string orderId,
        double? nodeThetaRad, CancellationToken ct)
    {
        try
        {
            return await ExecuteMoveToSeamStartCoreAsync(action, orderId, nodeThetaRad, ct);
        }
        catch (OperationCanceledException)
        {
            throw;   // 임무 폐기/emergencyStop — 상위(Vda5050OrderExecutor)가 정리
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "moveToSeamStart 실행 중 미처리 예외 (action={ActionId})", action.ActionId);
            return InspectionActionResult.Fail("inspectionFailed", $"내부 오류: {ex.Message}");
        }
    }

    private async Task<InspectionActionResult> ExecuteMoveToSeamStartCoreAsync(VdaAction action, string orderId,
        double? nodeThetaRad, CancellationToken ct)
    {
        if (!WeldInspectionActionParser.TryParseSeamStart(action, out var request, out var parseError))
            return InspectionActionResult.Fail("orderValidationError", $"파라미터 해석 실패: {parseError}");
        var req = request!;

        if (!_cobot.IsConnected)
            return InspectionActionResult.Fail("equipmentError", "코봇 RPC 미연결 — seam 시작점 이동 불가");

        using var scope = _scopeFactory.CreateScope();
        var sequence = scope.ServiceProvider.GetRequiredService<SequenceService>();
        var param = scope.ServiceProvider.GetRequiredService<ParameterService>();

        // 접근 거리 = 본검사 ②와 동일한 면 이격(파라미터 inspection.approachStandoffMm) — 같은 단계를
        // 그대로 실행하므로 시험 결과(reach 가부)가 실제 검사 접근과 일치한다(standoff 산정 데이터).
        var approachMm = await CobotInspectionMoveStep.GetApproachStandoffMmAsync(param, _logger);

        var context = new SequenceContext
        {
            Tool = 1,
            Velocity = 20,
            InspectionSurfaceId = WallCodeToSurfaceId(req.DrawingPos.WallCode),
            InspectionOffsetU = await param.GetDoubleAsync(WeldSequenceSupport.InspectionOffsetUKey) ?? 0.0,
            InspectionOffsetV = await param.GetDoubleAsync(WeldSequenceSupport.InspectionOffsetVKey) ?? 0.0,
            AcsJobRef = req.JobRef,
            AcsOrderId = orderId,
            AcsActionId = action.ActionId,
            SeamStartW = req.SeamStartW,
            SeamEndW = req.SeamEndW,
            WallCode = req.DrawingPos.WallCode,
            WallFacingThetaRad = nodeThetaRad,
            ZDatumOffsetMm = await param.GetDoubleAsync(WeldSequenceSupport.ZDatumOffsetKey) ?? 0.0,
        };

        _logger.LogInformation(
            "moveToSeamStart 수리: jobRef={JobRef}, wall={Wall}, seamStartW=[{S}], 면 이격 {Approach}mm, nodeθ={Theta}",
            req.JobRef, req.DrawingPos.WallCode, string.Join(", ", req.SeamStartW.Select(v => v.ToString("0.###"))),
            approachMm, nodeThetaRad?.ToString("0.###") ?? "(미상)");
        _opLog.Log(OperationLogService.SourceAcs, OpCategory.Action, "seam 시작점 시험 구성", null,
            $"jobRef={req.JobRef}, wall={req.DrawingPos.WallCode}, seamStartW=[{string.Join(", ", req.SeamStartW.Select(v => v.ToString("0.###")))}], " +
            $"면 이격 {approachMm:0}mm(본검사 'Cobot 검사위치 이동'과 동일), 노드 θ={nodeThetaRad?.ToString("0.###") ?? "미상"}, " +
            $"z 보정 {context.ZDatumOffsetMm:0}mm, 단계: {string.Join(",", SeamStartStepKeys)} (촬영·홈 복귀 없음)", orderId);

        // 코봇이 움직이므로 같은 order 의 정렬 캐시는 더 이상 믿을 수 없다.
        InvalidateAnchor();

        CancellationTokenSource runCts;
        lock (_sync)
        {
            _currentRunCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runCts = _currentRunCts;
        }

        SequenceRunResult runResult;
        try
        {
            await WaitForIdleGateAsync(req.JobRef, runCts.Token);
            if (await EnsureLiftStrokeAsync(
                    scope.ServiceProvider, req.LiftStrokeMm, req.DrawingPos.WallCode, req.JobRef, orderId, runCts.Token)
                is { } liftFail)
            {
                return liftFail;
            }

            runResult = await sequence.RunSequenceAsync(context, SeamStartStepKeys, runCts.Token);
        }
        finally
        {
            lock (_sync)
            {
                if (_currentRunCts == runCts) _currentRunCts = null;
                runCts.Dispose();
            }
        }

        switch (runResult.Outcome)
        {
            case SequenceRunOutcome.Busy:
                return InspectionActionResult.Fail("equipmentError",
                    "onboard sequence busy — UI 등 다른 실행이 장비 점유 중");

            case SequenceRunOutcome.Failed:
                // 리치 부족이면 "정차 거리 부족 — 필요 ≥ N mm, 현재 M mm …", 이동 실패면 rc=… 가 그대로 실린다(N10 데이터).
                return ShortfallResult(context, $"reach 불가 — {runResult.Message}")
                       ?? InspectionActionResult.Fail("inspectionFailed", $"reach 불가 — {runResult.Message}");

            case SequenceRunOutcome.Completed:
            default:
                var stepMsg = sequence.StepStatuses.GetValueOrDefault("cobotInspection")?.Message ?? "";
                string j5 = "";
                try
                {
                    var joints = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
                    j5 = $", |J5|={Math.Abs(joints[4]):0.0}°";
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* 관절 읽기 실패 — 수치만 생략 */ }

                // 도달 후 면의 태그(기본 AprilTag 36h11)로 화면 중심(광축) 대비 편차를 기록 — reach 결과와 무관(미검출이어도 FINISHED).
                string aruco;
                try
                {
                    var meter = scope.ServiceProvider.GetRequiredService<ArucoCenterOffsetMeter>();
                    var off = await meter.MeasureAsync(ct);
                    if (!off.Found)
                        _logger.LogWarning("moveToSeamStart 태그 중심 편차 측정 실패: {Note} (jobRef={JobRef})", off.Note, req.JobRef);
                    _opLog.Log(OperationLogService.SourceAcs, OpCategory.Action, $"{off.Kind} 중심 편차", off.Found,
                        $"jobRef={req.JobRef}, wall={req.DrawingPos.WallCode} — {off.Describe()}", orderId);
                    aruco = off.Summary();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "moveToSeamStart 태그 측정 오류 — reach 결과는 그대로 보고");
                    _opLog.Log(OperationLogService.SourceAcs, OpCategory.Action, "태그 중심 편차", false,
                        $"측정 오류: {ex.Message}", orderId);
                    aruco = $"태그 측정 오류({ex.Message})";
                }
                return InspectionActionResult.Ok($"reached — {stepMsg}{j5} · {aruco}");
        }
    }

    /// <summary>다른 실행(UI 세미오토 등)이 게이트를 잡고 있으면 끝날 때까지 기다리는 최대 시간.
    /// 잠깐의 점유를 equipmentError(노드 잔여 액션 전건 FAILED)로 보고하지 않기 위함이다.</summary>
    internal static readonly TimeSpan BusyWaitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>실행 게이트가 비기를 최대 <see cref="BusyWaitTimeout"/> 기다린다. 끝까지 점유 중이면 그대로 진행하고,
    /// 이어지는 게이트 획득이 Busy 로 판정한다.</summary>
    private async Task WaitForIdleGateAsync(string jobRef, CancellationToken ct)
    {
        if (!_runGate.IsBusy) return;
        _logger.LogInformation("다른 시퀀스 실행 중 — 최대 {Sec:0}s 대기 후 진행 (jobRef={JobRef})", BusyWaitTimeout.TotalSeconds, jobRef);
        var until = DateTime.UtcNow + BusyWaitTimeout;
        while (_runGate.IsBusy && DateTime.UtcNow < until)
            await Task.Delay(500, ct);
    }

    public void InvalidateAnchor()
    {
        lock (_sync)
        {
            _lastAnchor = null;
            _lastInternalWorkpieceFrame = null;
            _lastAnchorStartBase = null;
            _lastAnchorEndTcp = null;
            _lastAnchorStrokeMm = null;
        }
    }

    /// <summary>anchor 캐시 시점의 리프트 스트로크와 현재 스트로크가 허용 오차를 넘어 달라졌는지 판정한다.</summary>
    internal static bool IsAnchorStrokeChanged(double? cachedStrokeMm, double? currentStrokeMm,
        double toleranceMm = TelescopicService.DefaultTargetToleranceMm)
        => cachedStrokeMm is { } prev && currentStrokeMm is { } cur && Math.Abs(cur - prev) > toleranceMm;

    /// <summary>캐시된 anchor 를 리프트 높이 때문에 버려야 하는지 — 높이가 달라졌거나, 캐시가 있는데 캐시 시점·현재
    /// 높이 중 하나라도 모르면(통신 끊김 등) 같은 높이라고 믿을 수 없으므로 버린다.</summary>
    internal static bool ShouldInvalidateForStroke(bool anchorCached, double? cachedStrokeMm, double? currentStrokeMm)
        => IsAnchorStrokeChanged(cachedStrokeMm, currentStrokeMm)
           || (anchorCached && (cachedStrokeMm is null || currentStrokeMm is null));

    /// <summary>
    /// ② 가 리치 부족으로 실패했으면 `stationReachShortfall`(사양 §6.4, N23 A안) 결과 — resultDescription 은 사람용 사유,
    /// errorDescription 은 기계 판독부(<see cref="ReachShortfall.Describe"/>). 실행기가 actionId·보정량을 덧붙인다.
    /// 다른 원인의 실패면 null(호출측이 inspectionFailed 로 보고).
    /// </summary>
    private static InspectionActionResult? ShortfallResult(SequenceContext context, string description)
        => context.Bag.TryGetValue(CobotInspectionMoveStep.ReachShortfallBagKey, out var v) && v is ReachShortfall sf
            ? new InspectionActionResult(false, description, StationReachShortfallErrorType, sf.Describe())
            : null;

    /// <summary>리치 부족 전용 errorType(사양 §6.4, N23 A안).</summary>
    public const string StationReachShortfallErrorType = "stationReachShortfall";

    public async Task<StationAdjustDecision> PlanStationAdjustAsync(IReadOnlyList<VdaAction> actions,
        double nodeXm, double nodeYm, double? nodeThetaRad, CancellationToken ct)
    {
        var inspections = actions.Where(a => a.ActionType == "startWeldInspection").ToList();
        // ACS 는 같은 정차의 모든 액션에 같은 값을 싣는다 — 첫 액션 기준.
        StationAdjustLimits? limits = null;
        foreach (var a in inspections)
        {
            limits = WeldInspectionActionParser.TryParseStationAdjust(a, out var limitError);
            if (limitError is not null)
                return new StationAdjustDecision(null, null, $"stationAdjust 무시 — {limitError}");
            if (limits is not null) break;
        }
        if (limits is null) return new StationAdjustDecision(null, null, "");
        if (limits.MaxBackwardMm <= 0)
            return new StationAdjustDecision(limits, null, "후진 허락 0 — 정차 보정 없음(1단계는 후진만 사용)");
        if (nodeThetaRad is not { } theta)
            return new StationAdjustDecision(limits, null, "노드 theta 없음 — 벽 정면 프레임이 없어 정차 보정 생략");
        if (!_cobot.IsConnected)
            return new StationAdjustDecision(limits, null, "코봇 RPC 미연결 — 공구 길이를 읽을 수 없어 정차 보정 생략");

        var seams = new List<(string Label, double[] SeamStartW, double[]? SeamEndW, string WallCode, double? LiftStrokeMm)>();
        foreach (var a in inspections)
            if (WeldInspectionActionParser.TryParse(a, out var req, out _) && req is not null)
                seams.Add((req.JobRef, req.SeamStartW, req.SeamEndW, req.DrawingPos.WallCode, req.LiftStrokeMm));
        if (seams.Count == 0)
            return new StationAdjustDecision(limits, null, "해석 가능한 용접선 없음 — 정차 보정 생략");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<SeamApproachResolver>();
            // 공구는 본검사 기본 #1 — 프로필 RunTool 은 액션마다 프로필 조회가 필요해 계획 단계에서는 쓰지 않는다.
            var (samples, skip) = await resolver.SampleStationAsync(seams, nodeXm, nodeYm, theta, tool: 1, ct);
            if (skip is not null)
                return new StationAdjustDecision(limits, null, $"정차 보정 생략 — {skip}");
            var plan = StationAdjustPlanner.Plan(limits, samples!);
            return new StationAdjustDecision(limits, plan, plan.Summary);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "정차 보정 계획 중 예외");
            return new StationAdjustDecision(limits, null, $"정차 보정 생략 — 계획 오류: {ex.Message}");
        }
    }

    public async Task AbortAsync()
    {
        lock (_sync)
        {
            _currentRunCts?.Cancel();
            _lastAnchor = null;
            _lastAnchorStrokeMm = null;
        }
        try
        {
            await _cobot.StopMotionImmediateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "검사 중단 중 코봇 StopMotion 실패");
        }
        try
        {
            await _lift.StopMotionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "검사 중단 중 리프트 StopMotion 실패");
        }
    }

    public async Task<InspectionActionResult> ExecuteAsync(VdaAction action, string orderId, double? nodeThetaRad,
                                                           bool isLastInspection, CancellationToken ct)
    {
        try
        {
            return await ExecuteCoreAsync(action, orderId, nodeThetaRad, isLastInspection, ct);
        }
        catch (OperationCanceledException)
        {
            throw;   // 임무 폐기/emergencyStop — 상위(Vda5050OrderExecutor)가 정리
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "startWeldInspection 실행 중 미처리 예외 (action={ActionId})", action.ActionId);
            InvalidateAnchor();
            return InspectionActionResult.Fail("inspectionFailed", $"내부 오류: {ex.Message}");
        }
    }

    private async Task<InspectionActionResult> ExecuteCoreAsync(VdaAction action, string orderId, double? nodeThetaRad,
                                                                bool isLastInspection, CancellationToken ct)
    {
        // 1) 파싱 — 실패는 계약 위반(orderValidationError).
        if (!WeldInspectionActionParser.TryParse(action, out var request, out var parseError))
            return InspectionActionResult.Fail("orderValidationError", $"파라미터 해석 실패: {parseError}");
        var req = request!;

        // 2) 레시피 id 유도 (seamType × wall_code, 사양 §8.5.1).
        if (!InspectionRecipeResolver.TryResolve(req, out var recipeId, out var resolveError))
            return InspectionActionResult.Fail("orderValidationError", $"레시피 매핑 실패: {resolveError}");

        _logger.LogInformation(
            "startWeldInspection 수리: jobRef={JobRef}, seamType={SeamType}, wall={Wall} → recipe={Recipe} " +
            "(dxf={Dxf}, profileId(촬영)={ProfId}, anchor={Anchor}#{Seq}, liftStroke={Lift}mm, taskId={TaskId}, attempt={Attempt})",
            req.JobRef, req.SeamType, req.DrawingPos.WallCode, recipeId,
            req.SectionDxfId, req.InspectionProfileId, req.AnchorGroupId, req.SeqInGroup,
            req.LiftStrokeMm?.ToString("0") ?? "(미지정)",
            req.TaskId?.ToString() ?? "(미수신)", req.Attempt?.ToString() ?? "(미수신→1)");

        // taskId 미탑재는 계약상 허용(선택 필드)이지만 비전 측 이력 누적 키가 없다는 뜻이라 흔적을 남긴다.
        // 형식 오류는 파서가 이미 orderValidationError 로 거부했으므로 여기 도달하지 않는다.
        if (req.TaskId is null)
            _logger.LogWarning(
                "ACS 액션에 taskId 없음 — 비전에 Guid.Empty 로 전송됩니다(SAIGE 이력 누적 불가, jobRef={JobRef})",
                req.JobRef);

        // 3) 설비 선행 확인 — 코봇 링크 불능이면 equipmentError(설비 자체 불능).
        //    비전 링크는 레시피 로드 후 드라이런이 아닐 때만 본다(5단계 뒤).
        if (!_cobot.IsConnected)
            return InspectionActionResult.Fail("equipmentError", "코봇 RPC 미연결 — 검사 실행 불가");

        // 4) scope 생성 — scoped 서비스(레시피/도면/시퀀스) 사용.
        using var scope = _scopeFactory.CreateScope();
        var recipeService = scope.ServiceProvider.GetRequiredService<InspectionRecipeService>();
        var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
        var sequence = scope.ServiceProvider.GetRequiredService<SequenceService>();
        var param = scope.ServiceProvider.GetRequiredService<ParameterService>();

        // 5) 레시피 로드 + Enabled 게이트 — 매핑은 됐지만 실행 미구현이면 inspectionFailed(실행 불가, §8.5.1 (4)).
        var recipe = await recipeService.GetAsync(recipeId!, ct);
        if (recipe is null)
            return InspectionActionResult.Fail("inspectionFailed",
                $"recipe {recipeId} 미등록 — 레시피 시드/DB 확인 필요");
        if (!recipe.Enabled)
            return InspectionActionResult.Fail("inspectionFailed",
                $"recipe {recipeId} resolved but not enabled (실행 미구현 — N13/2차 대기)");
        if (recipe.SeamType == SeamTypeKind.Corner)
            return InspectionActionResult.Fail("inspectionFailed",
                "CORNER3 검사 시퀀스가 제거되어 실행할 수 없습니다.");

        // 비전 링크 — 드라이런 스텝 집합은 촬영(CAPTURE_REQ)을 하지 않으므로 링크 다운이어도 진행한다.
        if (!_vision.Client.IsConnected)
        {
            if (!recipe.DryRun)
                return InspectionActionResult.Fail("equipmentError", "비전 S/W 링크 다운 — 검사 실행 불가");
            _logger.LogInformation("비전 링크 확인 생략(드라이런) — 비전 S/W 링크 다운 상태로 진행 (recipe={Recipe})",
                recipeId);
        }

        // 6) 검사 방향 자동 유도(§4.4·§8.1) — 프로필 선택(챔퍼 수직 전용)과 anchor 판정보다 먼저 결정한다.
        //    seam 벡터를 노드 theta(벽 정면) 기준 벽면-로컬 투영.
        //    theta 미상(방어적)이면 현행 기본 Horizontal 폴백. 판정 근거는 로그로 남긴다.
        var direction = InspectionMoveDirection.Horizontal;
        if (nodeThetaRad is { } theta)
        {
            direction = SeamDirectionResolver.Resolve(req.SeamStartW, req.SeamEndW, theta, out var dirReason);
            _logger.LogInformation("검사 방향 유도: {Reason} (jobRef={JobRef})", dirReason, req.JobRef);
        }
        else
        {
            _logger.LogWarning("노드 theta 미상 — 검사 방향 Horizontal 폴백 (jobRef={JobRef})", req.JobRef);
        }

        // 7) 사전 티칭 경유점 조회: 레시피에 지정된 InspectionProfile(검사 레시피 페이지에서 지정).
        //    레시피는 면 자세-독립 단일(LINE/CROSS4)이고, 면별 경유점 차이는 지정된 프로파일이 흡수한다 —
        //    접근 자세·방향은 런타임이 wall_code 로 계산하므로 같은 LINE 프로파일이 모든 면에 쓰인다(§9-8).
        //    예외: LINE 의 챔퍼 면 수직(경사 방향) 진행은 코로게이션 피치가 254.6mm 라 전용 프로필을 쓴다.
        //    CORNER 는 도면 프로필을 쓰지 않는다(고정 티칭 슬롯 corner3.* 직접 순회) — 조회 생략.
        InspectionProfile? profile = null;
        var chamferSlope = false;
        if (recipe.SeamType != SeamTypeKind.Corner)
        {
            (var profileId, chamferSlope) =
                InspectionRecipeResolver.SelectProfileId(recipe, req.DrawingPos.WallCode, direction);
            var (found, profileError) = await FindProfileAsync(db, recipe, profileId, chamferSlope, req.SectionDxfId, ct);
            if (found is null)
                return InspectionActionResult.Fail("inspectionFailed", profileError!);
            profile = found;
        }

        // 7.5) 리프트 목표 높이(params.liftStrokeMm, 0~1000mm) 적용 — 이동 전에 코봇 홈 위치를 확인(필요 시 복귀)하고,
        //      목표 높이가 현재와 달라 이동이 필요하면 기존 anchorGroupId 작업물 좌표계 캐시를 먼저 무효화한다.
        //      도달 완료 후 동기화·진동 안정화를 위해 1초 추가 대기한다.
        CancellationTokenSource runCts;
        lock (_sync)
        {
            _currentRunCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runCts = _currentRunCts;
        }

        SequenceRunResult runResult;
        bool anchorHit;
        string? cornerSide = null;
        try
        {
            await WaitForIdleGateAsync(req.JobRef, runCts.Token);
            if (await EnsureLiftStrokeAsync(
                    scope.ServiceProvider, req.LiftStrokeMm, req.DrawingPos.WallCode, req.JobRef, orderId, runCts.Token)
                is { } liftFail)
            {
                return liftFail;
            }

            // 8) anchor 캐시 판정 — 같은 (orderId, anchorGroupId, direction) 연속이고 주행·리프트 높이 변경 없음 → ⑱만 실행.
            //    방향이나 리프트 높이가 달라지면 작업물 좌표계를 재교시해야 하므로 캐시 미적중 처리.
            //    CORNER 는 정렬 스텝이 없어 캐시 비적용(항상 레시피 스텝 전체 실행, 캐시 갱신도 안 함).
            double[]? cachedInternalFrame;
            double[]? cachedAnchorStartBase;
            double[]? cachedAnchorEndTcp;
            var currentStrokeMm = _lift.TryGetStrokeMm(out _);
            lock (_sync)
            {
                if (ShouldInvalidateForStroke(_lastAnchor is not null, _lastAnchorStrokeMm, currentStrokeMm))
                {
                    _logger.LogInformation(
                        "anchor 리프트 높이 변경({Old:0}→{New:0}mm) — 기존 작업물 좌표계 캐시 무효화 및 재교시 (anchorGroup={Group})",
                        _lastAnchorStrokeMm, currentStrokeMm, req.AnchorGroupId);
                    _lastAnchor = null;
                    _lastInternalWorkpieceFrame = null;
                    _lastAnchorStartBase = null;
                    _lastAnchorEndTcp = null;
                    _lastAnchorStrokeMm = null;
                }

                anchorHit = recipe.SeamType != SeamTypeKind.Corner
                            && _lastAnchor == (orderId, req.AnchorGroupId, direction, req.DrawingPos.WallCode)
                            && _lastInternalWorkpieceFrame is { Length: 6 };
                if (!anchorHit && _lastAnchor is { } prev
                    && prev.OrderId == orderId && prev.AnchorGroupId == req.AnchorGroupId
                    && (prev.Direction != direction || prev.WallCode != req.DrawingPos.WallCode))
                {
                    _logger.LogInformation(
                        "anchor 방향/면 변경({Old}/{OldWall}→{New}/{NewWall}) — 작업물 좌표계 재교시 (anchorGroup={Group})",
                        prev.Direction, prev.WallCode ?? "-", direction, req.DrawingPos.WallCode ?? "-", req.AnchorGroupId);
                }
                cachedInternalFrame = anchorHit ? (double[])_lastInternalWorkpieceFrame!.Clone() : null;
                cachedAnchorStartBase = anchorHit ? _lastAnchorStartBase?.ToArray() : null;
                cachedAnchorEndTcp = anchorHit ? _lastAnchorEndTcp?.ToArray() : null;
            }

            // 레시피가 정한 스텝(미지정이면 등록된 전체)에서 출발해, 이 task 에 맞지 않는 것만 뺀다.
            //  · anchor 적중 → 정렬 스텝군 제거(첫 task 가 잡아 둔 작업물 좌표계 재사용)
            //  · 마지막 검사 액션이 아님 → 코봇 홈 복귀 제거(task 사이에 홈 왕복 금지)
            // 드라이런 레시피는 StepKeysJson 을 무시하고 고정 드라이런 집합을 쓴다 — 수기 StepKeysJson 누락으로
            // 교시 스텝이 빠져 ⑱이 "작업물 좌표계 없음"으로 실패하던 문제를 원천 차단한다.
            // 일반 레시피의 풀시퀀스(StepKeysJson 미지정)에서는 드라이런 전용 스텝을 뺀다.
            var (recipeKeys, keysError) = ParseStepKeys(recipe.StepKeysJson);
            keysError ??= recipe.DryRun ? null
                : ValidateRecipeStepKeys(recipeKeys, sequence.Steps.Select(st => st.Key));
            if (keysError is not null)
                return InspectionActionResult.Fail("inspectionFailed",
                    $"recipe {recipeId} 실행 스텝 구성 오류 — {keysError}");

            var stepKeys = SelectStepKeys(recipe.DryRun, recipeKeys,
                sequence.Steps.Select(st => st.Key), anchorHit, isLastInspection);

            if (stepKeys.Length == 0)
                return InspectionActionResult.Fail("inspectionFailed",
                    $"recipe {recipeId} 에 실행할 스텝이 남지 않았습니다 — 레시피 실행 스텝 구성을 확인하세요.");

            // 8.5) CORNER3 좌/우 거울 side 판별 — 티칭 슬롯 접두사(corner3.L/R) 선택 키.
            if (recipe.SeamType == SeamTypeKind.Corner)
            {
                cornerSide = InspectionRecipeResolver.ResolveCornerSide(req.DrawingPos.WallCode, out var sideNote);
                _logger.LogInformation("CORNER3 side={Side} — {Note} (wall={Wall})",
                    cornerSide, sideNote, req.DrawingPos.WallCode);
            }

            var cameraTargetMm = recipe.CameraTargetDistanceMm ?? 400;
            // params.standoffMm 은 런타임에서 쓰지 않는다 — 툴 면 이격은 AMR 이 정하는 값이다(§10 N18).
            // ②는 면 이격 파라미터(inspection.approachStandoffMm)로 물러나고, ③이 카메라 목표거리로 수렴시킨다.
            var approachStandoffMm = await CobotInspectionMoveStep.GetApproachStandoffMmAsync(param, _logger);
            if (req.StandoffMm > 0 && Math.Abs(req.StandoffMm - approachStandoffMm) > 0.001)
                _logger.LogInformation(
                    "ACS standoffMm={AcsStandoff} 미사용 — 'Cobot 검사위치 이동' 면 이격은 파라미터 {Approach}mm, '카메라 거리 정렬' 목표는 레시피 {Recipe} {Target}mm (jobRef={JobRef})",
                    req.StandoffMm, approachStandoffMm, recipe.Id, cameraTargetMm, req.JobRef);
            if (req.WorkingDistanceMm is { } acsWd && Math.Abs(acsWd - cameraTargetMm) > 0.001)
                _logger.LogInformation(
                    "ACS workingDistanceMm={AcsWd} 무시 — 레시피 {Recipe} 카메라 목표거리 {Target}mm 사용 (jobRef={JobRef})",
                    acsWd, recipe.Id, cameraTargetMm, req.JobRef);

            // 9) SequenceContext 구성 — 파라미터 우선순위: ① ACS action → ② 티칭 프로필 → ③ 레시피 → ④ 전역 기본.
            //    CORNER 는 profile 이 없다 — Tool/Velocity 는 SequenceContext 기본값(단독 실행과 동일).
            //    공구는 실제 TCP 가 설정된 #1 이어야 한다 — 프로필 RunTool 이 0(미설정/플랜지)이면 1 로 보정.
            if (profile is { RunTool: <= 0 })
                _logger.LogWarning("티칭 프로필 '{Profile}' RunTool={RunTool} — 공구 #1 로 보정해 실행 (프로필 저장값 확인 필요)",
                    profile.Name, profile.RunTool);
            // 장비 튜닝값 — ACS 가 보내는 값이 아니라 현장에서 시퀀스 페이지로 맞춰 저장한 상수다.
            // 예전에는 페이지만 읽고 이 경로는 기본값(0/0/−65)으로 돌아, 맞춰 놓은 값이 실제 검사에는
            // 적용되지 않는 조용한 불일치가 있었다. 같은 키를 여기서도 읽어 해소한다.
            var offsetU = await param.GetDoubleAsync(WeldSequenceSupport.InspectionOffsetUKey) ?? 0.0;
            var offsetV = await param.GetDoubleAsync(WeldSequenceSupport.InspectionOffsetVKey) ?? 0.0;
            var camToLaserShiftY = await param.GetDoubleAsync(WeldSequenceSupport.CameraToLaserShiftYKey) ?? -75.0;
            // z 기준 보정(N17) — ACS 의 z 는 도면 전역(선창 바닥) 기준이라 L2 이상은 층 바닥 높이를 빼야 한다.
            var zDatumOffset = await param.GetDoubleAsync(WeldSequenceSupport.ZDatumOffsetKey) ?? 0.0;

            var context = new SequenceContext
            {
                InspectionDirection = direction,
                Tool = profile is { RunTool: > 0 } ? profile.RunTool : 1,
                // 이동 속도: 레시피 MoveVelPercent 우선, 없으면 종전대로 프로필 RunVel(미지정 20).
                // 검사(스캔) 속도: 레시피 InspectVelPercent — null 이면 ⑱이 이동 속도로 폴백.
                Velocity = recipe.MoveVelPercent ?? profile?.RunVel ?? 20,
                InspectVelocity = recipe.InspectVelPercent,
                InspectionDrawingId = profile?.DrawingId ?? 0,
                InspectionProfileId = profile?.Id ?? 0,
                CornerSide = cornerSide,
                InspectionSurfaceId = WallCodeToSurfaceId(req.DrawingPos.WallCode),
                // ③ 카메라 거리 정렬 목표 — 레시피 값(빈 값=전역 400). ACS workingDistanceMm 은 산출 근거가 없어
                // 사용하지 않는다(2026-09-18 결정, 스키마 항목은 유지 — 수신·로그만).
                CameraTargetDistanceMm = cameraTargetMm,
                InspectionOffsetU = offsetU,
                InspectionOffsetV = offsetV,
                CameraToLaserShiftYmm = camToLaserShiftY,
                AcsJobRef = req.JobRef,
                AcsOrderId = orderId,
                AcsActionId = action.ActionId,
                // ACS 발급 식별자 → 이 액션의 모든 CAPTURE_REQ(v3.2 [15-30]/[31])에 그대로 실린다(N14).
                // null 이면 실행 스텝이 Guid.Empty/1 로 폴백.
                AcsTaskId = req.TaskId,
                AcsAttempt = req.Attempt,
                AnchorGroupId = req.AnchorGroupId,
                SeqInGroup = req.SeqInGroup,
                StandoffMmOverride = req.StandoffMm > 0 ? req.StandoffMm : null,
                // ② 검사위치 이동의 목표 좌표 — task 마다 달라지는 유일한 위치 정보(§8.1).
                SeamStartW = req.SeamStartW,
                SeamEndW = req.SeamEndW,
                WallCode = req.DrawingPos.WallCode,
                WallFacingThetaRad = nodeThetaRad,
                ZDatumOffsetMm = zDatumOffset,
                VisionFailRatioMax = recipe.VisionFailRatioMax < 1.0 ? recipe.VisionFailRatioMax : null,
            };
            if (cachedInternalFrame is not null)
                context.Bag[InternalWorkpieceFrame.BagKey] = cachedInternalFrame;
            // ②가 캐시 프레임을 이번 시작점으로 평행 이동하고(없으면 실패), 직전 종료 자세 그대로면 복귀 없이 연속 이동한다.
            if (cachedAnchorStartBase is not null)
                context.Bag[CobotInspectionMoveStep.AnchorStartBaseBagKey] = cachedAnchorStartBase;
            if (cachedAnchorEndTcp is not null)
                context.Bag[CobotInspectionMoveStep.PreviousEndTcpBagKey] = cachedAnchorEndTcp;

            // CROSS3/CROSS4 는 LINE 과 동일하게 티칭 프로필 경유점을 실행한다 — 코로게이션 격자 교차부는
            // 평탄하지 않아 수식 생성으로 법선·깊이를 담을 수 없으므로 6-DOF 캡처 교시(/inspection-points)로
            // 단일화했다. (구 PatternJson 런타임 생성 경로는 폐기 — CrossPatternGenerator 는 교시 시작 템플릿 전용.)

            _logger.LogInformation(
                "검사 시퀀스 시작: recipe={Recipe}{DryRun}, profile='{Profile}'{Role}(id={ProfileId}, drawing={DrawingId}), " +
                "anchor {AnchorState}, steps={Steps}",
                recipeId, recipe.DryRun ? "[드라이런]" : "", profile?.Name ?? "(미사용 — CORNER 티칭 슬롯)",
                chamferSlope ? "(챔퍼 수직용)" : "",
                profile?.Id ?? 0, profile?.DrawingId ?? 0,
                anchorHit ? "적중(정렬 생략)" : "신규(정렬 수행)",
                string.Join(",", stepKeys));
            _opLog.Log(OperationLogService.SourceAcs, OpCategory.Action, "검사 구성", null,
                $"jobRef={req.JobRef}, seam={req.SeamType}, wall={req.DrawingPos.WallCode}, recipe={recipeId}" +
                (recipe.DryRun ? "[드라이런]" : "") +
                $", profile='{profile?.Name ?? "(미사용)"}'{(chamferSlope ? "(챔퍼 수직용)" : "")}, 방향={direction}, anchor={req.AnchorGroupId}#{req.SeqInGroup} " +
                (anchorHit ? "적중(정렬 생략)" : "신규(정렬 수행)") +
                (req.LiftStrokeMm is { } ls ? $", lift={ls:0}mm" : "") +
                $", tool={context.Tool}, vel={context.Velocity}%, 카메라 목표 {context.CameraTargetDistanceMm:0}mm, " +
                $"seamStartW=[{string.Join(", ", req.SeamStartW.Select(v => v.ToString("0.###")))}], " +
                $"단계 {stepKeys.Length}개: {string.Join(",", stepKeys)}", orderId);

            // 10) 실행 — 취소 전파용 CTS 는 보관(AbortAsync 가 취소).
            runResult = await sequence.RunSequenceAsync(context, stepKeys, runCts.Token);

            // 11) 결과 매핑.
            switch (runResult.Outcome)
            {
                case SequenceRunOutcome.Busy:
                    return InspectionActionResult.Fail("equipmentError",
                        "onboard sequence busy — UI 등 다른 실행이 장비 점유 중");

                case SequenceRunOutcome.Failed:
                    InvalidateAnchor();   // 정렬 신뢰 불가 — 다음 액션은 풀시퀀스
                    // 활성 작업물 좌표계 0·공구 복귀(best-effort)는 SequenceService.RunSequenceAsync 가 실패 종료
                    // 시(취소 제외) 수행한다 — UI 풀오토와 이 경로가 같은 반납을 공유한다.
                    var failDesc = $"recipe={recipeId} step={runResult.FailedStepKey ?? "?"} fail: {runResult.Message}";
                    return ShortfallResult(context, failDesc) ?? InspectionActionResult.Fail("inspectionFailed", failDesc);

                case SequenceRunOutcome.Completed:
                default:
                    // CORNER 는 정렬을 수행하지 않으므로 기존 내부 프레임 캐시를 훼손하지 않는다.
                    if (recipe.SeamType != SeamTypeKind.Corner)
                    {
                        if (!context.Bag.TryGetValue(InternalWorkpieceFrame.BagKey, out var value)
                            || value is not double[] { Length: 6 } internalFrame)
                        {
                            InvalidateAnchor();
                            return InspectionActionResult.Fail("inspectionFailed",
                                "검사는 완료됐지만 내부 작업물 좌표계가 없어 anchor를 보존할 수 없습니다.");
                        }
                        // 프레임과 짝으로 이 task 의 ② 시작 접근점·종료 TCP·리프트 스트로크를 남긴다.
                        var startBase = context.Bag.TryGetValue(CobotInspectionMoveStep.SeamStartBaseBagKey, out var sb)
                                        && sb is double[] { Length: 3 } s ? s.ToArray() : null;
                        double[]? endTcp = null;
                        try { endTcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "검사 종료 TCP 조회 실패 — 다음 task 는 연속 이동 없이 복귀 경로로 진입");
                        }
                        var strokeAtEnd = _lift.TryGetStrokeMm(out _);
                        lock (_sync)
                        {
                            _lastAnchor = (orderId, req.AnchorGroupId, direction, req.DrawingPos.WallCode);
                            _lastInternalWorkpieceFrame = (double[])internalFrame.Clone();
                            _lastAnchorStartBase = startBase;
                            _lastAnchorEndTcp = endTcp;
                            _lastAnchorStrokeMm = strokeAtEnd;
                        }
                    }
                    return InspectionActionResult.Ok(
                        $"recipe={recipeId} profile='{profile?.Name ?? $"corner3.{cornerSide}"}' anchor={req.AnchorGroupId}#{req.SeqInGroup}" +
                        (anchorHit ? " (정렬 공유)" : "") +
                        $" jobRef={req.JobRef} taskId={req.TaskId?.ToString() ?? "—"}");
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_currentRunCts == runCts) _currentRunCts = null;
                runCts.Dispose();
            }
        }
    }

    /// <summary>
    /// ACS 가 지정한 리프트 높이(<paramref name="requestedStrokeMm"/>, 0~1000mm)로 텔레스코픽 리프트를 맞춘다.
    /// 이미 목표 높이에 정지해 있으면 이동하지 않고, 높이가 바뀌어야 하면:
    ///   1) 기존 <c>anchorGroupId</c> 작업물 좌표계 캐시를 즉시 무효화(<see cref="InvalidateAnchor"/>)
    ///   2) 리프트 이동 전 코봇이 기준 홈(<c>home</c>/<c>home2</c>)에 있는지 확인하고, 아니면 안전 복귀 후 홈 도달 재확인
    ///   3) 목표 높이로 이동 및 도달 대기 + 1초 안정화 대기(<see cref="TelescopicService.MoveToHeightAndWaitAsync"/>)
    /// 통과하면 null, 실패하면 즉시 반환할 <see cref="InspectionActionResult"/>.
    /// </summary>
    private async Task<InspectionActionResult?> EnsureLiftStrokeAsync(
        IServiceProvider sp, double? requestedStrokeMm, string? wallCode, string jobRef, string orderId,
        CancellationToken ct)
    {
        if (requestedStrokeMm is not { } reqMm)
            return null;

        var targetHeightMm = (int)Math.Round(reqMm);
        if (!_lift.IsConnected)
            return InspectionActionResult.Fail("equipmentError",
                $"텔레스코픽 리프트 미연결 — 지령 높이 {targetHeightMm}mm 이동 불가");

        await _lift.RefreshAsync(ct);
        if (_lift.IsAtTargetHeight(targetHeightMm, out var currentHeightMm, out var statusError))
        {
            lock (_sync)
            {
                if (ShouldInvalidateForStroke(_lastAnchor is not null, _lastAnchorStrokeMm, currentHeightMm))
                {
                    _logger.LogInformation(
                        "리프트 높이 변경 감지({Old:0}→{New:0}mm) — 기존 anchorGroupId 작업물 좌표계 캐시 무효화 (jobRef={JobRef})",
                        _lastAnchorStrokeMm, currentHeightMm, jobRef);
                    _lastAnchor = null;
                    _lastInternalWorkpieceFrame = null;
                    _lastAnchorStartBase = null;
                    _lastAnchorEndTcp = null;
                    _lastAnchorStrokeMm = null;
                }
            }
            _logger.LogInformation(
                "리프트 이미 목표 높이 유지: 현재 {Cur}mm (지령 {Target}mm) — 이동 생략 (jobRef={JobRef})",
                currentHeightMm, targetHeightMm, jobRef);
            return null;
        }
        if (statusError is not null)
            return InspectionActionResult.Fail("equipmentError", $"텔레스코픽 리프트 상태 오류 — {statusError}");

        // 0) 장비 점유 — 홈 복귀·리프트 이동 동안 실행 게이트를 잡아 UI 시퀀스가 동시에 코봇·리프트를 움직이지 못하게 한다
        //    (스냅샷 확인만 하면 이동 중 UI 실행이 TryEnter 에 성공한다). 이동이 끝나면 놓고, 이어지는 RunSequenceAsync 가 다시 잡는다.
        if (!_runGate.TryEnter())
            return InspectionActionResult.Fail("equipmentError",
                $"onboard sequence busy — UI 등 다른 실행이 장비 점유 중이라 리프트 {targetHeightMm}mm 이동 불가");
        try
        {
            // 1) 노드 내 TASK 사이에 리프트 높이가 바뀌므로 기존 anchorGroupId 작업물 좌표계 캐시를 무효화한다.
            InvalidateAnchor();
            _logger.LogInformation(
                "리프트 높이 이동 준비({Cur}mm → {Target}mm) — 기존 anchorGroupId 작업물 좌표계 캐시 무효화 (jobRef={JobRef})",
                currentHeightMm, targetHeightMm, jobRef);

            // 2) 리프트 이동 전 코봇이 홈 위치(home / home2)에 있는지 확인(아직 이전 TASK 종료 위치면 먼저 홈 복귀).
            var teaching = sp.GetRequiredService<TeachingService>();
            var positions = await teaching.ListAsync(ct);
            var homeCtx = new SequenceContext
            {
                Positions = positions.ToDictionary(p => p.Key, p => p),
                Tool = 1,
                Velocity = 20,
                WallCode = wallCode,
            };
            await WeldSequenceSupport.LoadOpticalAxisAsync(sp.GetRequiredService<ParameterService>(), homeCtx);
            if (SequenceEntry.ValidateHome(homeCtx) is { IsValid: false } homeErr)
                return InspectionActionResult.Fail("inspectionFailed",
                    $"리프트 이동 전 코봇 홈 위치 확인 불가 — {homeErr.Message}");

            var curJoints = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
            if (!SequenceEntry.IsAtBaseHome(curJoints, homeCtx, out _, out var baseName))
            {
                _logger.LogInformation(
                    "리프트 이동 전 코봇이 {BaseName}에 있지 않음 — 안전을 위해 기준 홈 복귀 수행 (jobRef={JobRef})",
                    baseName, jobRef);
                _opLog.Log(OperationLogService.SourceAcs, OpCategory.Cobot, "리프트 이동 전 홈 복귀", null,
                    $"jobRef={jobRef}, 리프트 {currentHeightMm}→{targetHeightMm}mm 이동 전 코봇 {baseName} 복귀 수행", orderId);

                List<string> route;
                try
                {
                    route = await SequenceEntry.ReturnToBaseAsync(_cobot, homeCtx, _logger, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _opLog.Log(OperationLogService.SourceAcs, OpCategory.Cobot, "리프트 이동 전 홈 복귀 실패", false,
                        ex.Message, orderId);
                    return InspectionActionResult.Fail("inspectionFailed",
                        $"리프트 이동 전 코봇 {baseName} 복귀 실패 — 리프트를 이동하지 않고 중단합니다: {ex.Message}");
                }

                curJoints = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
                if (!SequenceEntry.IsAtBaseHome(curJoints, homeCtx, out _, out baseName))
                {
                    return InspectionActionResult.Fail("inspectionFailed",
                        $"리프트 이동 전 코봇이 {baseName}에 도달하지 못해 리프트를 이동하지 않고 중단합니다.");
                }
                _opLog.Log(OperationLogService.SourceAcs, OpCategory.Cobot, "리프트 이동 전 홈 복귀 완료", true,
                    $"{string.Join(" → ", route)} 완료 ({baseName} 확인)", orderId);
            }
            else
            {
                _logger.LogInformation("리프트 이동 전 코봇 {BaseName} 위치 확인 완료 (jobRef={JobRef})", baseName, jobRef);
            }

            // 3) 리프트 목표 높이 이동 + 도달 대기 + 1초 안정화 대기.
            _opLog.Log(OperationLogService.SourceAcs, OpCategory.Action, "리프트 높이 이동 시작", null,
                $"jobRef={jobRef}, 현재 {currentHeightMm}mm → 목표 {targetHeightMm}mm (코봇 {baseName} 확인 완료)", orderId);

            var moveResult = await _lift.MoveToHeightAndWaitAsync(targetHeightMm, ct: ct);
            if (!moveResult.Ok)
            {
                _opLog.Log(OperationLogService.SourceAcs, OpCategory.Action, "리프트 높이 이동 실패", false,
                    moveResult.Message, orderId);
                return InspectionActionResult.Fail("equipmentError", $"리프트 높이 이동 실패 — {moveResult.Message}");
            }

            _opLog.Log(OperationLogService.SourceAcs, OpCategory.Action, "리프트 높이 이동 완료", true,
                $"jobRef={jobRef} — {moveResult.Message}", orderId);
            return null;
        }
        finally
        {
            _runGate.Exit();
        }
    }

    /// <summary>레시피에 지정된 티칭설정(InspectionProfile)을 반환. 미지정/없음/타입 불일치면 (null, 사유).
    /// 도면·최신 저장 기준 자동 선택은 폐기 — 레시피 페이지의 명시 지정만 사용한다(테스트 저장이 실행 대상을 바꾸지 않도록).
    /// sectionDxfId 는 로그용으로만 받는다.</summary>
    private static async Task<(InspectionProfile? Profile, string? Error)> FindProfileAsync(
        HdAmrDbContext db, InspectionRecipe recipe, int? selectedProfileId, bool chamferSlope, string sectionDxfId,
        CancellationToken ct)
    {
        var role = chamferSlope ? "챔퍼 수직용 티칭 프로필" : "티칭 프로필";
        if (selectedProfileId is not { } profileId)
            return (null, chamferSlope
                ? $"recipe {recipe.Id} 에 챔퍼 수직용 티칭 프로필 미지정 (wall 챔퍼 + 수직 진행, 피치 254.6mm) — " +
                  "검사 레시피 페이지에서 '챔퍼 수직 프로필' 지정 필요"
                : $"recipe {recipe.Id} 에 티칭 프로필 미지정 (sectionDxfId='{sectionDxfId}') — " +
                  "검사 레시피 페이지에서 프로필 지정 필요");

        var profile = await db.InspectionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct);
        if (profile is null)
            return (null, $"recipe {recipe.Id} 지정 {role}(id={profileId}) 없음 — 삭제됨, 레시피 재지정 필요");

        var want = InspectionRecipeResolver.ProfileSeamTypeOf(recipe.Id);
        if (!InspectionRecipeService.IsSeamMatch(profile, want))
            return (null, $"recipe {recipe.Id} 지정 {role} '{profile.Name}' 타입({profile.SeamType}) ≠ {want} — 레시피 재지정 필요");

        return (profile, null);
    }

    /// <summary>StepKeysJson(문자열 배열) → 스텝 키 목록. null/빈/파싱 실패 → null(풀시퀀스).</summary>
    /// <summary>이 task 가 실행할 스텝 키 — 드라이런은 고정 집합, 일반은 레시피 지정 키(미지정이면 드라이런 전용을
    /// 뺀 전체)에서 anchor 적중 시 정렬 스텝군, 마지막 검사가 아니면 홈 복귀를 제외한다.</summary>
    internal static string[] SelectStepKeys(bool dryRun, string[]? recipeKeys, IEnumerable<string> allKeys,
                                            bool anchorHit, bool isLastInspection)
    {
        var baseSteps = dryRun
            ? DryRunStepKeys
            : recipeKeys ?? allKeys.Where(k => !DryRunOnlyStepKeys.Contains(k)).ToArray();
        return baseSteps
            // ① AMR 위치 이동은 페이지 테스트 전용 — ACS 경로의 주행은 order 실행기가 이미 마쳤다.
            .Where(k => k != Sequence.Steps.AmrMoveStep.StepKey)
            .Where(k => !(anchorHit && AlignmentStepKeys.Contains(k)))
            .Where(k => isLastInspection || k != HomeStepKey)
            .ToArray();
    }

    /// <summary>레시피 StepKeysJson 해석. 비어 있으면 (null, null) = 풀시퀀스. 형식이 틀리면 오류 — 조용히 풀시퀀스로
    /// 바꾸면 의도와 다른 스텝이 돈다.</summary>
    internal static (string[]? Keys, string? Error) ParseStepKeys(string? stepKeysJson)
    {
        if (string.IsNullOrWhiteSpace(stepKeysJson)) return (null, null);
        try
        {
            var keys = JsonSerializer.Deserialize<string[]>(stepKeysJson);
            return keys is { Length: > 0 } ? (keys, null) : (null, null);
        }
        catch (JsonException ex)
        {
            return (null, $"StepKeysJson 형식 오류({ex.Message}) — 문자열 배열이어야 합니다: {stepKeysJson}");
        }
    }

    /// <summary>
    /// 일반 레시피의 명시 스텝 목록 검사 — 등록되지 않은 키(오타)는 조용히 빠져 홈 복귀 등이 사라지므로 거부하고,
    /// 드라이런 전용 스텝(끝점 이동이 점2 를 덮어씀)과 ② 누락(anchor 적중 시 프레임을 이번 용접선으로 옮기지 못해
    /// 직전 용접선을 다시 검사)도 거부한다. 문제없으면 null.
    /// </summary>
    internal static string? ValidateRecipeStepKeys(string[]? recipeKeys, IEnumerable<string> registeredKeys)
    {
        if (recipeKeys is null) return null;
        var registered = registeredKeys.ToHashSet(StringComparer.Ordinal);
        var unknown = recipeKeys.Where(k => !registered.Contains(k)).ToArray();
        if (unknown.Length > 0)
            return $"등록되지 않은 스텝 키: {string.Join(", ", unknown.Select(k => $"'{k}'"))}";
        var dryOnly = recipeKeys.Where(DryRunOnlyStepKeys.Contains).ToArray();
        if (dryOnly.Length > 0)
            return $"드라이런 전용 스텝은 일반 레시피에 넣을 수 없습니다: {string.Join(", ", dryOnly)}";
        if (!recipeKeys.Contains("cobotInspection"))
            return "'cobotInspection'(Cobot 검사위치 이동)이 빠졌습니다 — 이번 용접선 위치로 이동·프레임 이동을 하지 못합니다";
        return null;
    }

    /// <summary>wall_code → 비전 Surface ID (vision_interface.md §5, 0x01~0x0A) — 정본 표 <see cref="WallCodes"/>.
    /// 미정의 코드는 resolver 가 먼저 거른다 — 방어적 기본 0x01.</summary>
    private static int WallCodeToSurfaceId(string wallCode) => WallCodes.Find(wallCode)?.SurfaceId ?? 0x01;
}
