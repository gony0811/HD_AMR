using System.Text.Json;
using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// VDA5050 order 실행기 — 단일 노드 Order(사양 §4.1)를 TARS-M REST 로 이행한다.
///
/// 수명주기(§4.5): 수신 검증 실패 → 폐기 + orderValidationError 보고 / 신규 orderId → 이전 임무 즉시 폐기.
/// 폐기 경로는 반드시 <b>정지(POST /robot/state) → 이동(POST /robot/go)</b> 순서다 — /go 는 큐 추가(append)라
/// 정지 없이 재발행하면 이전 목적지를 먼저 경유하는 "조용한 오검사"가 된다(부록 D-3).
///
/// 도착 판정(부록 D-4): 로봇 이동 명령에 허용 오차 파라미터가 없으므로 Modbus 폴링 pose 와 목표를
/// 자체 비교한다 — allowedDeviationXY/Theta, 미지정 시 0.1 m / 0.1 rad. status.schedule 값 해석은
/// 미확정(D-12)이라 error 필드 감시만 보조로 쓴다.
///
/// 검사 액션(startWeldInspection)은 <see cref="Inspection.IWeldInspectionExecutor"/>에 위임한다
/// (2차 연동, §8.5.1) — 파라미터 해석·레시피 매핑·검사 시퀀스 실행은 그쪽 책임이고, 이 클래스는
/// 액션 상태(RUNNING→FINISHED/FAILED)와 errors 보고만 담당한다.
/// 액션 없는 Order(actions:[])는 노드 도달만으로 완결(§4.1).
///
/// errors 는 확정 7종 중 판별 가능한 것을 보고: orderValidationError / emergencyStopActive /
/// inspectionFailed / equipmentError. (drivingFailed/localizationLost 는 로봇 오류 코드 회신 D-9 후.)
/// 같은 errorType 은 최신 1건만 유지, 해소 시 제거(§6.4).
/// </summary>
public sealed class Vda5050OrderExecutor
{
    private readonly AmrRestClient _rest;
    private readonly AMRService _amr;
    private readonly Inspection.IWeldInspectionExecutor _inspection;
    private readonly AmrRestSettings _restSettings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OperationLogService _opLog;
    private readonly ILogger<Vda5050OrderExecutor> _logger;

    private readonly object _gate = new();
    private CancellationTokenSource? _missionCts;
    private Task? _missionTask;

    // ── 보고 상태 (state 채널로 나가는 스냅샷 원본) ──
    private string _orderId = "";                       // §4.5.4: 부팅 후 무수신 상태에만 ""
    private int _orderUpdateId;
    private string _lastNodeId = "";
    private int _lastNodeSequenceId;
    private bool _driving;
    private readonly List<ActionState> _actionStates = new();
    private readonly List<NodeState> _nodeStates = new();
    private readonly Dictionary<string, VdaError> _errors = new();   // errorType → 최신 1건

    /// <summary>보고 상태 변화(즉시 state 발행 트리거). 어댑터가 구독한다.</summary>
    public event Action? StateChanged;

    public Vda5050OrderExecutor(AmrRestClient rest, AMRService amr,
        Inspection.IWeldInspectionExecutor inspection,
        IOptions<AmrRestSettings> restOptions, IServiceScopeFactory scopeFactory,
        OperationLogService opLog, ILogger<Vda5050OrderExecutor> logger)
    {
        _rest = rest;
        _amr = amr;
        _inspection = inspection;
        _restSettings = restOptions.Value;
        _scopeFactory = scopeFactory;
        _opLog = opLog;
        _logger = logger;
    }

    /// <summary>운영 로그(ACS 출처) 축약 — orderId 를 상관 식별자로 남긴다.</summary>
    private void OpLog(string category, string name, bool? success, string detail, string? orderId = null)
        => _opLog.Log(OperationLogService.SourceAcs, category, name, success, detail,
            orderId ?? (_orderId.Length > 0 ? _orderId : null));

    /// <summary>state 메시지 구성용 스냅샷 — 복사본 반환(발행 스레드와 임무 스레드 분리).</summary>
    public (string OrderId, int OrderUpdateId, string LastNodeId, int LastNodeSequenceId, bool Driving,
        List<ActionState> ActionStates, List<NodeState> NodeStates, List<VdaError> Errors) Snapshot()
    {
        lock (_gate)
        {
            return (_orderId, _orderUpdateId, _lastNodeId, _lastNodeSequenceId, _driving,
                _actionStates.Select(a => new ActionState
                {
                    ActionId = a.ActionId, ActionType = a.ActionType,
                    ActionStatus = a.ActionStatus, ResultDescription = a.ResultDescription,
                }).ToList(),
                _nodeStates.Select(n => new NodeState
                {
                    NodeId = n.NodeId, SequenceId = n.SequenceId, Released = n.Released,
                }).ToList(),
                _errors.Values.Select(e => new VdaError
                {
                    ErrorType = e.ErrorType, ErrorLevel = e.ErrorLevel, ErrorDescription = e.ErrorDescription,
                }).ToList());
        }
    }

    // ── order 수신 ────────────────────────────────────────────────────

    /// <param name="currentMapId">어댑터 보유 현재 층 mapId — 층 불일치 검증(§4.5.2)에 사용.</param>
    public async Task HandleOrderAsync(Vda5050Order order, string currentMapId)
    {
        lock (_gate)
        {
            // QoS1 중복 발행 방어 — 같은 orderId 재수신은 무시(orderUpdateId 는 항상 0, §4.1).
            if (order.OrderId == _orderId && !string.IsNullOrEmpty(_orderId))
            {
                _logger.LogInformation("VDA5050 order 중복 수신 무시: {OrderId}", order.OrderId);
                return;
            }
        }

        // §4.5.2 검증 — 실패 시 폐기(부분 실행 금지) + orderValidationError. 기존 실행 중 임무는 유지.
        var reject = Validate(order, currentMapId);
        if (reject is not null)
        {
            _logger.LogWarning("VDA5050 order 거부: {OrderId} — {Reason}", order.OrderId, reject);
            OpLog("ORDER", "order 거부", false, reject, order.OrderId);
            lock (_gate)
            {
                _errors["orderValidationError"] = new VdaError
                {
                    ErrorType = "orderValidationError",
                    ErrorLevel = "WARNING",
                    ErrorDescription = $"orderId={order.OrderId}: {reject}",
                };
            }
            StateChanged?.Invoke();
            return;
        }

        // 신규 orderId = 이전 임무 즉시 폐기(§4.5.1). 실행 태스크 취소 → 로봇 정지(D-3 순서 보장).
        await AbortMissionAsync("superseded by new order");
        var stop = await _rest.StopAsync();
        if (!stop.Ok)
            _logger.LogWarning("VDA5050 order 교체 선행 정지 실패(code={Code}) — 이동은 계속 진행: {Msg}",
                stop.Code, stop.Message);

        var node = order.Nodes[0];
        CancellationToken ct;
        lock (_gate)
        {
            _orderId = order.OrderId;
            _orderUpdateId = order.OrderUpdateId;
            _driving = false;
            _actionStates.Clear();
            _actionStates.AddRange(node.Actions.Select(a => new ActionState
            {
                ActionId = a.ActionId, ActionType = a.ActionType, ActionStatus = "WAITING",
            }));
            _nodeStates.Clear();
            _nodeStates.Add(new NodeState { NodeId = node.NodeId, SequenceId = node.SequenceId, Released = node.Released });
            // 새 임무 수신 = 이전 거부/비상정지/검사 실패 상태 해소(§6.4: 해소 시 제거)
            _errors.Remove("orderValidationError");
            _errors.Remove("emergencyStopActive");
            _errors.Remove("inspectionFailed");
            _errors.Remove("equipmentError");

            _missionCts = new CancellationTokenSource();
            ct = _missionCts.Token;
        }
        // 신규 order = 정렬(anchor) 캐시 무효(사양 §8.1 — 그룹 캐시는 order 내에서만 유효).
        _inspection.InvalidateAnchor();
        _logger.LogInformation("VDA5050 order 수리: {OrderId}, node={NodeId} → ({X:0.###}, {Y:0.###}, θ={Theta:0.###})",
            order.OrderId, node.NodeId, node.NodePosition!.X, node.NodePosition.Y, node.NodePosition.Theta);
        OpLog("ORDER", "order 수신", null,
            $"목표 ({node.NodePosition.X:0.###}, {node.NodePosition.Y:0.###}, θ={node.NodePosition.Theta:0.###}), " +
            $"액션 {node.Actions.Count}건", order.OrderId);
        StateChanged?.Invoke();

        var task = RunMissionAsync(order.OrderId, node, ct);
        lock (_gate) _missionTask = task;
        _ = task.ContinueWith(
            t => _logger.LogError(t.Exception, "VDA5050 임무 태스크 미처리 예외"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>검증 실패 사유(통과 시 null). 개별 액션 파라미터 문제는 여기가 아니라 액션 FAILED 경로(§4.5.2).</summary>
    private static string? Validate(Vda5050Order order, string currentMapId)
    {
        if (string.IsNullOrEmpty(order.OrderId)) return "orderId 누락";
        if (order.Nodes.Count == 0) return "nodes 비어 있음";
        if (order.Nodes.Count > 1) return $"단일 노드 계약 위반 (nodes={order.Nodes.Count}) — 경로형 미지원";
        var node = order.Nodes[0];
        if (node.NodePosition is null) return "nodePosition 누락";
        if (string.IsNullOrEmpty(node.NodePosition.MapId)) return "nodePosition.mapId 누락";
        if (!string.Equals(node.NodePosition.MapId, currentMapId, StringComparison.Ordinal))
            return $"현재 층 불일치 (order={node.NodePosition.MapId}, 현재={currentMapId})";
        if (string.IsNullOrEmpty(node.NodeId)) return "nodeId 누락";
        return null;
    }

    // ── 임무 실행 ─────────────────────────────────────────────────────

    private async Task RunMissionAsync(string orderId, OrderNode node, CancellationToken ct)
    {
        var pos = node.NodePosition!;
        try
        {
            // 허용 오차 결정: ParameterService(DB) → Order → AmrRestSettings
            var (devXy, devTheta) = await ResolveDeviationsAsync(pos);

            // 코봇 홈 복귀 — 팔이 뻗은 채 AMR 이 주행하면 구조물 충돌 위험.
            await EnsureCobotHomeBeforeDriveAsync(ct);

            // 0) 이미 목표 위치면 이동 생략 — 측위 신뢰도가 낮을 때 제자리 go 가 W13(경로탐색 실패)로
            //    전체 임무를 죽이는 것을 방지. 축별 ±devXy + theta 판정(사양과 별개의 현장 합의).
            //    주행이 없으므로 정렬(anchor) 캐시도 유지한다(§8.1 — 정차점 불변).
            if (IsAlreadyAtTarget(pos, devXy, devTheta))
            {
                OpLog("ORDER", "이동 생략", null, "이미 목표 위치", orderId);
            }
            else
            {
                // 1) 이동 명령 — 검사 정차는 항상 stopFlag=true (부록 D-2: false 면 각도 미보정).
                //    주행 발생 = 정렬(anchor) 캐시 무효(사양 §8.1 — 정차점이 바뀌면 정렬 재수행).
                _inspection.InvalidateAnchor();
                lock (_gate) _driving = true;
                StateChanged?.Invoke();

                // 이동 전 status.error 에 이미 남아 있던 항목은 과거 실패의 잔재다(플랫폼이 배열을
                // 비우지 않고 유지하는 것을 실증) — 도착 감시에서 제외할 기준선으로 캡처.
                var staleErrors = await SnapshotErrorKeysAsync(ct);

                var go = await _rest.GoAsync(pos.X, pos.Y, pos.Theta ?? 0.0, stopFlag: true, ct);
                if (!go.Ok)
                {
                    FailMission($"이동 명령 실패 (code={go.Code}): {go.Message}");
                    return;
                }

                // 2) 도착 대기 — 자체 위치 판정(부록 D-4) + status.error 감시(보조).
                var arrived = await WaitForArrivalAsync(pos, devXy, devTheta, staleErrors, ct);
                if (!arrived.Ok)
                {
                    FailMission(arrived.Reason!);
                    return;
                }
            }

            lock (_gate)
            {
                _driving = false;
                _lastNodeId = node.NodeId;
                _lastNodeSequenceId = node.SequenceId;
                _nodeStates.Clear();   // 도달한 노드는 nodeStates 에서 제거(§6.2)
            }
            _logger.LogInformation("VDA5050 노드 도달: {NodeId} (seq={Seq})", node.NodeId, node.SequenceId);
            OpLog("ORDER", "노드 도달", true, $"node={node.NodeId} ({pos.X:0.###}, {pos.Y:0.###})", orderId);
            StateChanged?.Invoke();

            // 3) 액션 순차 실행 — 배열 순서 = 실행 순서(§4.2). 액션 없는 Order(actions:[])는
            //    노드 도달만으로 완결(§4.1). startWeldInspection 은 검사 실행기에 위임(§8.5.1 2차 연동).
            //    개별 액션 실패는 다음 액션 계속 — 단 equipmentError(설비 불능)면 잔여 전건 FAILED(§6.4).
            //    마지막 검사 액션에서만 코봇을 홈으로 되돌린다 — 노드 안의 task 는 정렬(작업물 좌표계)을
            //    공유하므로 사이마다 홈에 다녀오면 이점이 사라지고 시간만 든다(§8.1 anchorGroupId).
            var lastInspectionIndex = node.Actions.FindLastIndex(a => a.ActionType == "startWeldInspection");
            var equipmentDown = false;
            for (var ai = 0; ai < node.Actions.Count; ai++)
            {
                var action = node.Actions[ai];
                ct.ThrowIfCancellationRequested();

                if (equipmentDown)
                {
                    SetActionStatus(action.ActionId, "FAILED", "설비 불능(equipmentError)으로 잔여 액션 중단");
                    StateChanged?.Invoke();
                    continue;
                }

                SetActionStatus(action.ActionId, "RUNNING", null);
                StateChanged?.Invoke();

                if (action.ActionType == "startWeldInspection")
                {
                    var result = await _inspection.ExecuteAsync(
                        action, orderId, pos.Theta, isLastInspection: ai == lastInspectionIndex, ct);
                    SetActionStatus(action.ActionId, result.Success ? "FINISHED" : "FAILED", result.ResultDescription);
                    OpLog("ACTION", action.ActionType, result.Success, result.ResultDescription ?? "", orderId);
                    if (!result.Success && result.ErrorType is not null)
                    {
                        ReportError(result.ErrorType, result.ErrorDescription ?? result.ResultDescription);
                        if (result.ErrorType == "equipmentError") equipmentDown = true;
                    }
                    _logger.LogInformation("VDA5050 액션 {Result}: {Type} ({ActionId}) — {Desc}",
                        result.Success ? "완료" : "실패", action.ActionType, action.ActionId, result.ResultDescription);
                }
                else
                {
                    // 카탈로그(§8) 외 노드 액션 — 계약 위반: 액션 FAILED + orderValidationError.
                    var desc = $"미지원 노드 액션 타입 '{action.ActionType}'";
                    SetActionStatus(action.ActionId, "FAILED", desc);
                    OpLog("ACTION", action.ActionType, false, desc, orderId);
                    ReportError("orderValidationError", $"actionId={action.ActionId}: {desc}");
                    _logger.LogWarning("VDA5050 {Desc} ({ActionId})", desc, action.ActionId);
                }
                StateChanged?.Invoke();
            }
            _logger.LogInformation("VDA5050 order 완결: {OrderId} (액션 {N}건)", _orderId, node.Actions.Count);
            OpLog("ORDER", "order 완결", true, $"액션 {node.Actions.Count}건 처리 완료", orderId);
            // 완결 후에도 orderId·actionStates 는 다음 Order 수신까지 유지 보고(§4.5.4).
        }
        catch (OperationCanceledException)
        {
            // 신규 orderId 교체 또는 emergencyStop — 후속 상태는 폐기/정지 경로가 정리한다.
            _logger.LogInformation("VDA5050 임무 중단: {OrderId}", _orderId);
        }
    }

    /// <summary>도착 판정 허용 오차 결정 — ① ParameterService(DB) → ② Order → ③ AmrRestSettings.</summary>
    private async Task<(double DevXy, double DevTheta)> ResolveDeviationsAsync(NodePosition pos)
    {
        double? paramXy = null, paramTheta = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            paramXy = await param.GetDoubleAsync(WeldSequenceSupport.ArrivalDeviationXyKey);
            paramTheta = await param.GetDoubleAsync(WeldSequenceSupport.ArrivalDeviationThetaKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "도착 오차 파라미터 조회 실패 — 설정/기본값 폴백");
        }

        var (devXy, srcXy) = Resolve(paramXy, pos.AllowedDeviationXY, _restSettings.DefaultDeviationXy);
        var (devTheta, srcTheta) = Resolve(paramTheta, pos.AllowedDeviationTheta, _restSettings.DefaultDeviationTheta);
        _logger.LogInformation("도착 오차 결정: dxy={DevXy:F3}m({SrcXy}), dθ={DevTheta:F3}rad({SrcTheta})",
            devXy, srcXy, devTheta, srcTheta);
        return (devXy, devTheta);

        static (double Value, string Src) Resolve(double? param, double? order, double settings)
            => param is not null ? (param.Value, "DB")
             : order is not null ? (order.Value, "Order")
             :                     (settings, "Settings");
    }

    /// <summary>AMR 주행 전 코봇 홈 복귀 — 팔이 뻗은 상태에서 주행하면 구조물 충돌 위험.
    /// 코봇 미연결 또는 홈 미티칭이면 건너뛴다(코봇 없는 주행 전용 시나리오).</summary>
    private async Task EnsureCobotHomeBeforeDriveAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var cobot = scope.ServiceProvider.GetRequiredService<CobotService>();
            if (!cobot.IsConnected)
            {
                _logger.LogDebug("코봇 미연결 — 홈 복귀 건너뜀");
                return;
            }

            var teaching = scope.ServiceProvider.GetRequiredService<TeachingService>();
            var positions = await teaching.ListAsync(ct);
            var home = positions.FirstOrDefault(p => p.Key == "home");
            if (home is null || !home.IsTaught)
            {
                _logger.LogWarning("홈 위치 미티칭 — 코봇 홈 복귀 건너뜀");
                return;
            }

            var homeJoints = new[]
            {
                home.J1!.Value, home.J2!.Value, home.J3!.Value,
                home.J4!.Value, home.J5!.Value, home.J6!.Value,
            };

            var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
            if (SequenceEntry.IsWithinJointTolerance(cur, homeJoints))
            {
                _logger.LogDebug("코봇 이미 홈 — 복귀 불필요");
                return;
            }

            _logger.LogInformation("AMR 주행 전 코봇 홈 복귀 시작");

            // ready 경유: 검사 자세에서 직접 홈으로 MoveJ 시 차체 충돌 방지
            var ready = positions.FirstOrDefault(p => p.Key == "ready");
            if (ready is not null && ready.IsTaught)
            {
                var readyJoints = new[]
                {
                    ready.J1!.Value, ready.J2!.Value, ready.J3!.Value,
                    ready.J4!.Value, ready.J5!.Value, ready.J6!.Value,
                };
                if (!SequenceEntry.IsWithinJointTolerance(cur, readyJoints))
                {
                    var readyPose = new[]
                    {
                        ready.X!.Value, ready.Y!.Value, ready.Z!.Value,
                        ready.Rx!.Value, ready.Ry!.Value, ready.Rz!.Value,
                    };
                    var rr = await cobot.Rpc.MoveJAsync(readyJoints, readyPose,
                        tool: 1, user: 0, vel: 20, ct: ct);
                    if (rr != 0)
                        _logger.LogWarning("작업 준비 위치 경유 실패 (rc={Rc}) — 직접 홈 복귀 시도", rr);
                    else
                        _logger.LogInformation("작업 준비 위치 경유 완료");
                }
            }

            var homePose = new[]
            {
                home.X!.Value, home.Y!.Value, home.Z!.Value,
                home.Rx!.Value, home.Ry!.Value, home.Rz!.Value,
            };
            var rc = await cobot.Rpc.MoveJAsync(homeJoints, homePose,
                tool: 1, user: 0, vel: 20, ct: ct);
            if (rc != 0)
                _logger.LogError("코봇 홈 복귀 실패 (rc={Rc}) — AMR 주행은 계속 진행", rc);
            else
                _logger.LogInformation("코봇 홈 복귀 완료");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "코봇 홈 복귀 중 예외 — AMR 주행은 계속 진행");
        }
    }

    /// <summary>현재 pose 가 목표와 x·y 축별 devXy 이내이고 theta 도 devTheta 이내면 true.
    /// LatestStatus 미확보(Modbus 미연결)면 판정 불가 → false(기존 이동 경로).</summary>
    private bool IsAlreadyAtTarget(NodePosition pos, double devXy, double devTheta)
    {
        var st = _amr.LatestStatus;
        if (st is null) return false;

        var dx = st.Pose.X - pos.X;
        var dy = st.Pose.Y - pos.Y;
        var dTheta = pos.Theta is double t ? Math.Abs(NormalizeRad(st.Pose.Angle - t)) : 0.0;
        if (Math.Abs(dx) > devXy || Math.Abs(dy) > devXy || dTheta > devTheta) return false;

        _logger.LogInformation("VDA5050 이동 생략: 이미 목표 위치 (dx={Dx:F3}m, dy={Dy:F3}m, dθ={DTheta:F3}rad)", dx, dy, dTheta);
        return true;
    }

    private async Task<(bool Ok, string? Reason)> WaitForArrivalAsync(
        NodePosition pos, double devXy, double devTheta,
        HashSet<string> staleErrors, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_restSettings.DriveTimeoutSec);
        var started = DateTime.UtcNow;
        var lastDiagLog = DateTime.MinValue;
        const int diagIntervalSec = 30;

        double lastDxy = double.NaN, lastDTheta = double.NaN;
        string lastPoseDesc = "미확보";

        _logger.LogInformation("VDA5050 도착 대기 시작: 목표=({X:F3}, {Y:F3}, θ={T:F3}), " +
            "허용 dxy≤{DevXy:F3}m, dθ≤{DevTheta:F3}rad, 타임아웃={Timeout}s",
            pos.X, pos.Y, pos.Theta ?? 0.0, devXy, devTheta, _restSettings.DriveTimeoutSec);

        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow > deadline)
            {
                _logger.LogWarning("VDA5050 주행 타임아웃: 최종 {Pose}, 잔여 dxy={Dxy:F3}m, dθ={DTheta:F3}rad " +
                    "(허용 dxy≤{DevXy:F3}m, dθ≤{DevTheta:F3}rad)",
                    lastPoseDesc, lastDxy, lastDTheta, devXy, devTheta);
                return (false, $"주행 타임아웃 ({_restSettings.DriveTimeoutSec}s) — 목표 미도달 " +
                    $"(최종 {lastPoseDesc}, 잔여 dxy={lastDxy:F3}m, dθ={lastDTheta:F3}rad)");
            }

            // 1차: Modbus 폴링 pose (1초 주기 갱신, AMRService).
            var st = _amr.LatestStatus;
            if (st is not null)
            {
                var dx = st.Pose.X - pos.X;
                var dy = st.Pose.Y - pos.Y;
                var dxy = Math.Sqrt(dx * dx + dy * dy);
                var dTheta = pos.Theta is double t ? Math.Abs(NormalizeRad(st.Pose.Angle - t)) : 0.0;

                lastDxy = dxy;
                lastDTheta = dTheta;
                lastPoseDesc = $"pose=({st.Pose.X:F3}, {st.Pose.Y:F3}, θ={st.Pose.Angle:F3})";

                if (dxy <= devXy && dTheta <= devTheta)
                    return (true, null);
            }
            else
            {
                // 2차: Modbus null → REST /robot/pose 폴백
                var restPose = await TryGetRestPoseAsync(ct);
                if (restPose is var (rx, ry, rTheta))
                {
                    var dx = rx - pos.X;
                    var dy = ry - pos.Y;
                    var dxy = Math.Sqrt(dx * dx + dy * dy);
                    var dThetaRest = pos.Theta is double t2 ? Math.Abs(NormalizeRad(rTheta - t2)) : 0.0;

                    lastDxy = dxy;
                    lastDTheta = dThetaRest;
                    lastPoseDesc = $"pose=({rx:F3}, {ry:F3}, θ={rTheta:F3})[REST]";

                    if (dxy <= devXy && dThetaRest <= devTheta)
                    {
                        _logger.LogInformation("VDA5050 도착 판정(REST 폴백): Modbus 미연결 중 REST pose 로 도착 확인");
                        return (true, null);
                    }
                }
                else
                {
                    lastPoseDesc = "Modbus+REST 모두 미확보";
                }
            }

            // 진단 로그 — 30초마다 현재 상태 기록
            if ((DateTime.UtcNow - lastDiagLog).TotalSeconds >= diagIntervalSec)
            {
                var elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;
                if (st is not null)
                    _logger.LogInformation("VDA5050 도착 대기 중: {Pose}, 잔여 dxy={Dxy:F3}m, dθ={DTheta:F3}rad " +
                        "(경과 {Elapsed}s/{Timeout}s)",
                        lastPoseDesc, lastDxy, lastDTheta, elapsed, _restSettings.DriveTimeoutSec);
                else
                    _logger.LogWarning("VDA5050 도착 대기 중: AMR Modbus 미연결 — {Pose} (경과 {Elapsed}s/{Timeout}s)",
                        lastPoseDesc, elapsed, _restSettings.DriveTimeoutSec);
                lastDiagLog = DateTime.UtcNow;
            }

            // 보조: status.error 감시 — 이동 전 기준선(staleErrors)에 없던 신규 항목만 실패로 본다.
            var status = await _rest.GetStatusAsync(ct);
            if (status.Ok && status.Data is JsonElement data)
            {
                var fresh = GetErrorEntries(data).Where(e => !staleErrors.Contains(e.Key)).ToList();
                if (fresh.Count > 0)
                    return (false, $"로봇 이동 오류 보고: [{string.Join(",", fresh.Select(e => e.Text))}]");
            }

            await Task.Delay(_restSettings.StatusPollMs, ct);
        }
        ct.ThrowIfCancellationRequested();
        return (false, "cancelled");
    }

    /// <summary>REST /robot/pose 에서 현재 SLAM 포즈를 조회한다. 실패 시 null.</summary>
    private async Task<(double X, double Y, double Theta)?> TryGetRestPoseAsync(CancellationToken ct)
    {
        try
        {
            var result = await _rest.GetPoseAsync(ct);
            if (!result.Ok || result.Data is not JsonElement data) return null;

            // data 가 {x, y, rz} 직접이거나, data.pose 안에 있을 수 있다 — 방어적 파싱.
            var obj = data;
            if (data.TryGetProperty("pose", out var inner) && inner.ValueKind == JsonValueKind.Object)
                obj = inner;

            if (obj.TryGetProperty("x", out var xEl) && xEl.TryGetDouble(out var x) &&
                obj.TryGetProperty("y", out var yEl) && yEl.TryGetDouble(out var y))
            {
                var rz = 0.0;
                if (obj.TryGetProperty("rz", out var rzEl)) rzEl.TryGetDouble(out rz);
                else if (obj.TryGetProperty("theta", out var thetaEl)) thetaEl.TryGetDouble(out rz);
                return (x, y, rz);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "REST pose 폴백 조회 실패");
        }
        return null;
    }

    /// <summary>현재 status.error 항목 키 집합 — 이동 직전에 찍는 묵은 오류 기준선.
    /// 조회 실패 시 빈 집합(= 이후 모든 오류를 신규로 간주, 기존 동작과 동일).</summary>
    private async Task<HashSet<string>> SnapshotErrorKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var status = await _rest.GetStatusAsync(ct);
        if (status.Ok && status.Data is JsonElement data)
            foreach (var (key, _) in GetErrorEntries(data))
                keys.Add(key);
        return keys;
    }

    /// <summary>status 응답의 error 필드를 개별 항목으로 분해. 실측 스키마는 {code,timestamp,msg} 배열 —
    /// 키는 code|timestamp 로 잡아 동일 오류의 재발(새 timestamp)은 신규로 구분된다.
    /// 그 밖의 형태(문자열/숫자/객체)는 원문 전체를 한 항목으로 취급(비어 있음·"0"·0 은 무오류).</summary>
    private static List<(string Key, string Text)> GetErrorEntries(JsonElement data)
    {
        var list = new List<(string, string)>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("error", out var err))
            return list;
        switch (err.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var e in err.EnumerateArray())
                {
                    var raw = e.GetRawText();
                    var key = raw;
                    if (e.ValueKind == JsonValueKind.Object)
                    {
                        var code = e.TryGetProperty("code", out var c) ? c.ToString() : "";
                        var ts = e.TryGetProperty("timestamp", out var t) ? t.ToString() : "";
                        if (code.Length > 0 || ts.Length > 0) key = $"{code}|{ts}";
                    }
                    list.Add((key, raw));
                }
                break;
            case JsonValueKind.String:
                var s = err.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(s) && s != "0") list.Add((s, s));
                break;
            case JsonValueKind.Number:
                if (err.TryGetInt64(out var n) && n != 0) list.Add((err.ToString(), err.ToString()));
                break;
            case JsonValueKind.Object:
                if (err.EnumerateObject().Any()) list.Add((err.GetRawText(), err.GetRawText()));
                break;
        }
        return list;
    }

    private static double NormalizeRad(double rad)
    {
        while (rad > Math.PI) rad -= 2 * Math.PI;
        while (rad < -Math.PI) rad += 2 * Math.PI;
        return rad;
    }

    /// <summary>임무 실패 — 미도달 상태로 전 미종결 액션 FAILED(§6.4 drivingFailed 계약의 액션 처리부).
    /// errors 의 drivingFailed 코드는 로봇 오류 코드 회신(D-9) 후 병기 예정 — 지금은 사유를 액션에만 남긴다.</summary>
    private void FailMission(string reason)
    {
        _logger.LogWarning("VDA5050 임무 실패: {OrderId} — {Reason}", _orderId, reason);
        OpLog("ORDER", "임무 실패", false, reason);
        lock (_gate)
        {
            _driving = false;
            foreach (var a in _actionStates.Where(a => a.ActionStatus is not ("FINISHED" or "FAILED")))
            {
                a.ActionStatus = "FAILED";
                a.ResultDescription = reason;
            }
        }
        StateChanged?.Invoke();
    }

    private void SetActionStatus(string actionId, string status, string? result)
    {
        lock (_gate)
        {
            var a = _actionStates.FirstOrDefault(x => x.ActionId == actionId);
            if (a is null) return;
            a.ActionStatus = status;
            if (result is not null) a.ResultDescription = result;
        }
    }

    /// <summary>errors 갱신 — 같은 errorType 은 최신 1건만 유지(§6.4). 해소는 다음 order 수신 시.</summary>
    private void ReportError(string errorType, string description)
    {
        lock (_gate)
        {
            _errors[errorType] = new VdaError
            {
                ErrorType = errorType,
                ErrorLevel = "WARNING",
                ErrorDescription = description,
            };
        }
    }

    // ── instantActions ────────────────────────────────────────────────

    /// <summary>emergencyStop(§5.1) — REST 정지 + 진행 임무 취소 + 미종결 액션 FAILED + emergencyStopActive 보고.
    /// 코봇/검사장비 정지는 어댑터가 별도 수행한다(이 클래스는 주행만 담당).</summary>
    public async Task EmergencyStopAsync()
    {
        _logger.LogWarning("VDA5050 emergencyStop 수신 — 주행 정지 실행");
        OpLog("ORDER", "emergencyStop", null, "ACS 비상정지 수신 — 주행 정지 및 임무 폐기");
        await AbortMissionAsync("stopped by emergencyStop");
        var stop = await _rest.StopAsync();
        if (!stop.Ok)
            _logger.LogWarning("emergencyStop REST 정지 실패(code={Code}): {Msg}", stop.Code, stop.Message);

        lock (_gate)
        {
            _driving = false;
            foreach (var a in _actionStates.Where(a => a.ActionStatus is not ("FINISHED" or "FAILED")))
            {
                a.ActionStatus = "FAILED";
                a.ResultDescription = "stopped by emergencyStop";
            }
            _errors["emergencyStopActive"] = new VdaError
            {
                ErrorType = "emergencyStopActive",
                ErrorLevel = "WARNING",
                ErrorDescription = "emergencyStop 수신에 의한 기능 정지 중 — 신규 Order 재배차로 재개",
            };
        }
        StateChanged?.Invoke();
    }

    /// <summary>진행 중 임무 태스크 취소·대기. 로봇 정지는 호출측이 이어서 수행한다(D-3 순서).</summary>
    private async Task AbortMissionAsync(string reason)
    {
        Task? running;
        lock (_gate)
        {
            _missionCts?.Cancel();
            running = _missionTask;
        }
        if (running is not null)
        {
            try { await running; }
            catch (Exception ex) { _logger.LogDebug(ex, "임무 태스크 종료 대기 중 예외({Reason})", reason); }
        }
        lock (_gate)
        {
            _missionCts?.Dispose();
            _missionCts = null;
            _missionTask = null;
        }
    }
}
