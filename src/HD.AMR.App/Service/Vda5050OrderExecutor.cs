using System.Text.Json;
using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vda5050;
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
    private readonly ILogger<Vda5050OrderExecutor> _logger;

    private const double DefaultDeviationXy = 0.1;     // [m] — 사양 §4.2 미지정 기본값
    private const double DefaultDeviationTheta = 0.1;  // [rad]

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
        IOptions<AmrRestSettings> restOptions, ILogger<Vda5050OrderExecutor> logger)
    {
        _rest = rest;
        _amr = amr;
        _inspection = inspection;
        _restSettings = restOptions.Value;
        _logger = logger;
    }

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
            // 1) 이동 명령 — 검사 정차는 항상 stopFlag=true (부록 D-2: false 면 각도 미보정).
            //    주행 발생 = 정렬(anchor) 캐시 무효(사양 §8.1 — 정차점이 바뀌면 정렬 재수행).
            _inspection.InvalidateAnchor();
            lock (_gate) _driving = true;
            StateChanged?.Invoke();

            var go = await _rest.GoAsync(pos.X, pos.Y, pos.Theta ?? 0.0, stopFlag: true, ct);
            if (!go.Ok)
            {
                FailMission($"이동 명령 실패 (code={go.Code}): {go.Message}");
                return;
            }

            // 2) 도착 대기 — 자체 위치 판정(부록 D-4) + status.error 감시(보조).
            var arrived = await WaitForArrivalAsync(pos, ct);
            if (!arrived.Ok)
            {
                FailMission(arrived.Reason!);
                return;
            }

            lock (_gate)
            {
                _driving = false;
                _lastNodeId = node.NodeId;
                _lastNodeSequenceId = node.SequenceId;
                _nodeStates.Clear();   // 도달한 노드는 nodeStates 에서 제거(§6.2)
            }
            _logger.LogInformation("VDA5050 노드 도달: {NodeId} (seq={Seq})", node.NodeId, node.SequenceId);
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
                    ReportError("orderValidationError", $"actionId={action.ActionId}: {desc}");
                    _logger.LogWarning("VDA5050 {Desc} ({ActionId})", desc, action.ActionId);
                }
                StateChanged?.Invoke();
            }
            _logger.LogInformation("VDA5050 order 완결: {OrderId} (액션 {N}건)", _orderId, node.Actions.Count);
            // 완결 후에도 orderId·actionStates 는 다음 Order 수신까지 유지 보고(§4.5.4).
        }
        catch (OperationCanceledException)
        {
            // 신규 orderId 교체 또는 emergencyStop — 후속 상태는 폐기/정지 경로가 정리한다.
            _logger.LogInformation("VDA5050 임무 중단: {OrderId}", _orderId);
        }
    }

    private async Task<(bool Ok, string? Reason)> WaitForArrivalAsync(NodePosition pos, CancellationToken ct)
    {
        var devXy = pos.AllowedDeviationXY ?? DefaultDeviationXy;
        var devTheta = pos.AllowedDeviationTheta ?? DefaultDeviationTheta;
        var deadline = DateTime.UtcNow.AddSeconds(_restSettings.DriveTimeoutSec);

        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow > deadline)
                return (false, $"주행 타임아웃 ({_restSettings.DriveTimeoutSec}s) — 목표 미도달");

            // 자체 도착 판정: Modbus 폴링 pose (1초 주기 갱신, AMRService).
            var st = _amr.LatestStatus;
            if (st is not null)
            {
                var dx = st.Pose.X - pos.X;
                var dy = st.Pose.Y - pos.Y;
                var dxy = Math.Sqrt(dx * dx + dy * dy);
                var dTheta = pos.Theta is double t ? Math.Abs(NormalizeRad(st.Pose.Angle - t)) : 0.0;
                if (dxy <= devXy && dTheta <= devTheta)
                    return (true, null);
            }

            // 보조: status.error 감시 — 값 해석 미확정(D-12/D-9)이라 "존재하고 비어 있지 않음"만 실패로 본다.
            var status = await _rest.GetStatusAsync(ct);
            if (status.Ok && status.Data is JsonElement data && TryGetErrorText(data, out var errText))
                return (false, $"로봇 이동 오류 보고: {errText}");

            await Task.Delay(_restSettings.StatusPollMs, ct);
        }
        ct.ThrowIfCancellationRequested();
        return (false, "cancelled");
    }

    /// <summary>status 응답의 error 필드가 "오류 있음"을 나타내면 true. 스키마 미확정 — 문자열 비어있지 않음
    /// 또는 숫자 0 아님만 오류로 간주하고 원문을 그대로 전달한다(벤더 회신 D-9/D-12 후 정교화).</summary>
    private static bool TryGetErrorText(JsonElement data, out string text)
    {
        text = "";
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("error", out var err))
            return false;
        switch (err.ValueKind)
        {
            case JsonValueKind.String:
                text = err.GetString() ?? "";
                return !string.IsNullOrWhiteSpace(text) && text != "0";
            case JsonValueKind.Number:
                text = err.ToString();
                return err.TryGetInt64(out var n) && n != 0;
            case JsonValueKind.Object or JsonValueKind.Array:
                text = err.GetRawText();
                return err.ValueKind == JsonValueKind.Object
                    ? err.EnumerateObject().Any()
                    : err.GetArrayLength() > 0;
            default:
                return false;
        }
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
