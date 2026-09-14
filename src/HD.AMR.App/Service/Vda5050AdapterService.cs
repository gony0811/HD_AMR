using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace HD.AMR.App.Service;

/// <summary>
/// ACS 프로세스 생존 상태(§7.2 N12). <see cref="Unknown"/> = 생존 신호 수신 이력 없음 —
/// ACS 쪽 미구현(N12 협의 대기)이거나 retained 메시지가 아직 안 온 상태로, UI 는 기존 3-상태로 폴백한다.
/// </summary>
public enum AcsLiveness { Unknown, Online, Offline, Broken }

/// <summary>
/// HD_AMR VDA 5050 어댑터 — ACS(마스터)에 대한 AGV 에이전트.
/// 사양: docs/VDA5050_INTERFACE_SPEC_1.pdf v1.1 (확정판, 부록 D TARS-M REST 매핑 포함).
///
/// 범위(1차): connection(ONLINE/OFFLINE/Last Will) + state 2초 주기·이벤트 즉시 발행 +
/// order 수신 → REST 이동·도달 보고(<see cref="Vda5050OrderExecutor"/>) +
/// instantActions(emergencyStop 이행 / initPosition 파싱만 — 이행은 벤더 회신 D-10 대기).
/// startWeldInspection 은 스텁 보고(FINISHED "stub") — 검사 시퀀스 연동은 2차.
///
/// 유보(부록 D.2): errorType 주행·측위 매핑(D-9), initPosition(D-10), 층 게이트(D-11),
/// schedule 해석(D-12), 큐 비우기(D-13) — 계약은 유효, 온보드 이행만 벤더 2차 회신 후.
/// </summary>
public sealed class Vda5050AdapterService : BackgroundService
{
    private readonly Vda5050AdapterSettings _s;
    private readonly AMRService _amr;
    private readonly Vda5050OrderExecutor _executor;
    private readonly CobotService _cobot;
    private readonly ILogger<Vda5050AdapterService> _logger;

    private IMqttClient? _client;
    private int _stateHeaderId;
    private int _connHeaderId;

    // 연결 상태 노출용 — _client 는 재접속 루프에서 Dispose/null 처리되므로 직접 노출하지 않는다.
    private volatile bool _brokerConnected;
    private long _lastAcsMsgTicks; // DateTime.UtcNow.Ticks, 0 = 수신 이력 없음

    // ACS 생존 신호(§7.2 N12) — retained 라 재접속 시 즉시 복원되므로, 우리 쪽 두절 시 Unknown 으로 리셋한다.
    private volatile AcsLiveness _acsLiveness = AcsLiveness.Unknown;
    private long _acsLivenessChangedTicks; // 마지막 상태 변화 시각(UtcNow.Ticks), 0 = 없음

    /// <summary>MQTT 브로커 접속 여부. ACS 자체 생존은 별개 — <see cref="AcsRecentlyActive"/> 참고.</summary>
    public bool IsBrokerConnected => _brokerConnected;

    /// <summary>ACS 발신 메시지(order/instantActions) 마지막 수신 시각(UTC). 수신 이력 없으면 null.</summary>
    public DateTime? LastAcsMessageUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastAcsMsgTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// 최근 <see cref="Vda5050AdapterSettings.AcsActiveWindowSec"/> 이내 ACS 메시지 수신 여부.
    /// VDA5050 에는 ACS 하트비트가 없어 무소식이 곧 두절은 아님 — "활동 있음" 표시로만 쓴다.
    /// </summary>
    public bool AcsRecentlyActive =>
        LastAcsMessageUtc is { } t && (DateTime.UtcNow - t).TotalSeconds <= _s.AcsActiveWindowSec;

    /// <summary>ACS 프로세스 생존 상태(§7.2 N12) — ACS 전용 connection 토픽(retained + Last Will) 기반.</summary>
    public AcsLiveness AcsConnectionLiveness => _acsLiveness;

    /// <summary><see cref="AcsConnectionLiveness"/> 마지막 변화 시각(UTC). 변화 이력 없으면 null.</summary>
    public DateTime? AcsLivenessChangedUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _acsLivenessChangedTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    // 현재 층 mapId — 로봇이 맵ID 미노출이라 어댑터가 보유(§4.3). 초기값은 설정,
    // initPosition 이행(D-10 유보) 확정 후 재측위 검증 통과 시에만 갱신한다.
    // D-10 유보 기간에는 대시보드의 수동 층 전환(SetMapId)이 유일한 갱신 경로.
    private volatile string _mapId;

    /// <summary>현재 층 mapId — state.agvPosition.mapId 로 발행되는 값.</summary>
    public string CurrentMapId => _mapId;

    /// <summary>수동 층 전환 UI 선택지(설정 <see cref="Vda5050AdapterSettings.AvailableMapIds"/>).</summary>
    public IReadOnlyList<string> AvailableMapIds => _s.AvailableMapIds;

    /// <summary>
    /// 수동 층 전환(운영자 UI). 재측위 검증 없이 mapId 만 바꾸는 임시 운영 경로 —
    /// initPosition 이행(D-10) 구현 전까지 사용. 변경 즉시 state 를 발행해 ACS에 회신한다(§6.1).
    /// </summary>
    public void SetMapId(string mapId)
    {
        mapId = mapId?.Trim() ?? "";
        if (mapId.Length == 0 || mapId == _mapId) return;

        var prev = _mapId;
        _mapId = mapId;
        _logger.LogWarning("수동 층 전환: mapId {Prev} → {New} — 재측위 검증 없이 변경(D-10 유보 기간 임시 운영). " +
                           "실제 층·맵 일치 여부는 운영자 책임.", prev, mapId);
        NudgeState();
    }

    // 이벤트 즉시 발행 트리거(§6.1) — 주기 대기를 깨우는 nudge.
    private TaskCompletionSource _stateNudge = NewNudge();

    private static TaskCompletionSource NewNudge()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Vda5050AdapterService(IOptions<Vda5050AdapterSettings> options, AMRService amr,
        Vda5050OrderExecutor executor, CobotService cobot, ILoggerFactory loggerFactory)
    {
        _s = options.Value;
        _amr = amr;
        _executor = executor;
        _cobot = cobot;
        _mapId = _s.MapId;
        _logger = loggerFactory.CreateLogger<Vda5050AdapterService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_s.Enabled)
        {
            _logger.LogInformation("VDA5050 어댑터 비활성(Enabled=false) — 유휴.");
            return;
        }

        _logger.LogInformation("VDA5050 어댑터 시작 — broker {Host}:{Port}, {Mfr}/{Serial}",
            _s.BrokerHost, _s.BrokerPort, _s.Manufacturer, _s.SerialNumber);

        _executor.StateChanged += NudgeState;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndRunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning("VDA5050 어댑터 연결 실패 — {Delay}ms 후 재시도: {Err}", _s.ReconnectDelayMs, ex.Message);
                }

                try { await Task.Delay(_s.ReconnectDelayMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _executor.StateChanged -= NudgeState;
        }
    }

    private void NudgeState() => _stateNudge.TrySetResult();

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += e =>
        {
            // 브로커 두절 = ACS 생존 판정 근거 상실(§7.2 — 브로커 장애는 ACS 두절과 동일 취급).
            // retained 라 재접속·재구독 시 즉시 복원되므로 stale ONLINE 방지를 위해 리셋한다.
            SetAcsLiveness(AcsLiveness.Unknown);

            // 정상 종료 경로는 finally 에서 미리 false 로 내려 경고를 남기지 않는다 — 예기치 못한 두절만 로그.
            if (_brokerConnected)
            {
                _brokerConnected = false;
                _logger.LogWarning("VDA5050 브로커 연결 두절: {Reason}",
                    string.IsNullOrEmpty(e.ReasonString) ? e.Reason.ToString() : e.ReasonString);
            }
            return Task.CompletedTask;
        };

        // Last Will = connection CONNECTIONBROKEN (retained) — 비정상 두절 시 브로커가 대신 발행(§2.4).
        var willPayload = JsonSerializer.SerializeToUtf8Bytes(BuildConnection("CONNECTIONBROKEN"));

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_s.BrokerHost, _s.BrokerPort)
            .WithClientId($"hdamr-{_s.Manufacturer}-{_s.SerialNumber}")
            .WithCleanSession()
            .WithWillTopic(Vda5050Topics.Connection(_s))
            .WithWillPayload(willPayload)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(_s.Username))
            optionsBuilder = optionsBuilder.WithCredentials(_s.Username, _s.Password);

        await _client.ConnectAsync(optionsBuilder.Build(), ct);
        _brokerConnected = true;
        _logger.LogInformation("VDA5050 브로커 접속 완료.");

        // 수신 구독 (order/instantActions — QoS 1, §2.3)
        await _client.SubscribeAsync(Vda5050Topics.Order(_s), MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await _client.SubscribeAsync(Vda5050Topics.InstantActions(_s), MqttQualityOfServiceLevel.AtLeastOnce, ct);
        // ACS 생존 신호(§7.2 N12) — retained 라 구독 즉시 최신 상태 수신. ACS 미구현이면 아무것도 오지 않는다.
        await _client.SubscribeAsync(Vda5050Topics.AcsConnection(_s), MqttQualityOfServiceLevel.AtLeastOnce, ct);

        // connection ONLINE (retained, §7) + 재접속 시 최신 state 즉시 발행(§9.3)
        await PublishAsync(Vda5050Topics.Connection(_s), BuildConnection("ONLINE"), retain: true, ct);

        // state 발행 루프 — 2초 주기 + 이벤트 즉시(§6.1). retain=false(§2.3).
        try
        {
            while (!ct.IsCancellationRequested && _client.IsConnected)
            {
                await PublishAsync(Vda5050Topics.State(_s), BuildState(), retain: false, ct);

                var nudge = _stateNudge.Task;
                var fired = await Task.WhenAny(nudge, Task.Delay(_s.StatePeriodMs, ct));
                if (fired == nudge)
                    _stateNudge = NewNudge();   // 소비 후 재장전 — 직후 루프가 즉시 발행
            }
        }
        finally
        {
            // 정상 종료 — OFFLINE(retained) 명시 발행 후 정리(§7).
            _brokerConnected = false;
            SetAcsLiveness(AcsLiveness.Unknown);
            if (_client.IsConnected)
            {
                try { await PublishAsync(Vda5050Topics.Connection(_s), BuildConnection("OFFLINE"), retain: true, CancellationToken.None); }
                catch { /* ignore */ }
                try { await _client.DisconnectAsync(); } catch { /* ignore */ }
            }
            _client.Dispose();
            _client = null;
        }
    }

    // ── 수신 처리 ─────────────────────────────────────────────────────

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var json = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

        // ACS 생존 신호(§7.2)는 활동 판정에 넣지 않는다 — retained ONLINE 이 재접속 때마다 재수신되기 때문.
        // AMR connection 토픽도 "/connection" 으로 끝나므로 접미사가 아닌 전체 토픽으로 비교한다.
        if (topic == Vda5050Topics.AcsConnection(_s))
        {
            HandleAcsConnectionJson(json);
            return Task.CompletedTask;
        }

        // 구독 토픽(order/instantActions)은 전부 ACS 발신 — 수신 시각이 곧 ACS 활동 증거.
        Interlocked.Exchange(ref _lastAcsMsgTicks, DateTime.UtcNow.Ticks);

        // 임무 실행은 수 분 단위(주행) — MQTT 수신 펌프를 막지 않도록 백그라운드로 넘긴다.
        if (topic.EndsWith("/order"))
            _ = Task.Run(() => HandleOrderJsonAsync(json));
        else if (topic.EndsWith("/instantActions"))
            _ = Task.Run(() => HandleInstantActionsJsonAsync(json));
        return Task.CompletedTask;
    }

    private async Task HandleOrderJsonAsync(string json)
    {
        try
        {
            var order = JsonSerializer.Deserialize<Vda5050Order>(json);
            if (order is null)
            {
                _logger.LogWarning("VDA5050 order 역직렬화 실패(null): {Len}B", json.Length);
                return;
            }
            // 층 검증(§4.5.2 현재 층 불일치 포함)은 실행기 검증부가 수행 — 어댑터 보유 mapId 를 전달.
            await _executor.HandleOrderAsync(order, _mapId);
            NudgeState();   // order 수신 직후 즉시 발행(§6.1)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VDA5050 order 처리 실패");
        }
    }

    private async Task HandleInstantActionsJsonAsync(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<Vda5050InstantActions>(json);
            if (msg is null) return;

            foreach (var action in msg.Actions)
            {
                switch (action.ActionType)
                {
                    case "emergencyStop":
                        // §5.1: 주행·협동로봇·검사 즉시 정지. 주행은 REST(부록 D-5), 코봇은 온보드 즉시 정지.
                        await _executor.EmergencyStopAsync();
                        try
                        {
                            await _cobot.StopMotionImmediateAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "emergencyStop 코봇 정지 실패(미연결일 수 있음)");
                        }
                        NudgeState();
                        break;

                    case "initPosition":
                        // §5.2 + 부록 D-10: 이행 유보(tuneFlag 의미·수렴 판정 미확인). 파싱·로그만 남긴다.
                        // mapId 갱신도 재측위 검증 통과 시에만 하는 계약이라 여기서 바꾸지 않는다.
                        var p = ParseInitPosition(action);
                        _logger.LogWarning(
                            "VDA5050 initPosition 수신 — 이행 유보(D-10, 벤더 회신 대기): mapId={MapId}, x={X}, y={Y}, theta={Theta}. " +
                            "재측위·mapId 갱신 미수행.", p.MapId, p.X, p.Y, p.Theta);
                        break;

                    default:
                        _logger.LogInformation("VDA5050 미지원 instantAction 무시: {Type}", action.ActionType);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VDA5050 instantActions 처리 실패");
        }
    }

    /// <summary>ACS 생존 신호 수신(§7.2 N12) — connectionState 만 소비, UI 노출·로그 외 동작 변경 없음(안전 정지 근거 아님).</summary>
    private void HandleAcsConnectionJson(string json)
    {
        // 빈 payload = retained 메시지 삭제(브로커 규약) — 생존 신호 없음으로 되돌린다.
        if (string.IsNullOrWhiteSpace(json))
        {
            SetAcsLiveness(AcsLiveness.Unknown);
            return;
        }
        try
        {
            var state = JsonSerializer.Deserialize<Vda5050Connection>(json)?.ConnectionState;
            var next = state switch
            {
                "ONLINE" => AcsLiveness.Online,
                "OFFLINE" => AcsLiveness.Offline,
                "CONNECTIONBROKEN" => AcsLiveness.Broken,
                _ => AcsLiveness.Unknown,
            };
            if (next == AcsLiveness.Unknown)
            {
                _logger.LogWarning("VDA5050 ACS connection 미지 상태 무시: {State}", state);
                return;
            }
            if (next == _acsLiveness) return;

            SetAcsLiveness(next);
            if (next == AcsLiveness.Online)
                _logger.LogInformation("ACS 생존 신호: ONLINE");
            else
                _logger.LogWarning("ACS 생존 신호: {State} — ACS 두절. 신규 Order 기대 불가, 진행 중 Order 는 자율 계속(§9.3).", state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VDA5050 ACS connection 처리 실패");
        }
    }

    private void SetAcsLiveness(AcsLiveness next)
    {
        if (_acsLiveness == next) return;
        _acsLiveness = next;
        Interlocked.Exchange(ref _acsLivenessChangedTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>initPosition 파라미터 파싱 — 평면 key 4개(N5 확정) + pose 객체 형태도 방어적 수용.</summary>
    private static (string? MapId, double? X, double? Y, double? Theta) ParseInitPosition(VdaAction action)
    {
        string? mapId = null;
        double? x = null, y = null, theta = null;

        foreach (var p in action.ActionParameters)
        {
            var v = p.Value;
            var el = v is JsonElement je ? je : default;
            switch (p.Key)
            {
                case "mapId": mapId = AsString(v); break;
                case "x": x = AsDouble(v); break;
                case "y": y = AsDouble(v); break;
                case "theta": theta = AsDouble(v); break;
                case "pose" when el.ValueKind == JsonValueKind.Object:   // 표준 관례 pose 객체 폴백
                    if (el.TryGetProperty("mapId", out var pm)) mapId = pm.GetString();
                    if (el.TryGetProperty("x", out var px) && px.TryGetDouble(out var dx)) x = dx;
                    if (el.TryGetProperty("y", out var py) && py.TryGetDouble(out var dy)) y = dy;
                    if (el.TryGetProperty("theta", out var pt) && pt.TryGetDouble(out var dt)) theta = dt;
                    break;
            }
        }
        return (mapId, x, y, theta);

        static string? AsString(object? v) => v is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : v?.ToString();
        static double? AsDouble(object? v)
        {
            if (v is JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d)) return d;
                if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), out var ds)) return ds;
                return null;
            }
            return v is IConvertible c ? c.ToDouble(null) : null;
        }
    }

    // ── 발행 ──────────────────────────────────────────────────────────

    private async Task PublishAsync<T>(string topic, T payload, bool retain, CancellationToken ct)
    {
        if (_client is null) return;
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.SerializeToUtf8Bytes(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        await _client.PublishAsync(msg, ct);
    }

    private Vda5050Connection BuildConnection(string state) => new()
    {
        HeaderId = _connHeaderId++,
        Manufacturer = _s.Manufacturer,
        SerialNumber = _s.SerialNumber,
        ConnectionState = state,
    };

    /// <summary>AMRService(Modbus 폴링) + 실행기 스냅샷 → VDA5050 state(§6.2 최소 계약 + N4 표준 필드).</summary>
    private Vda5050State BuildState()
    {
        var st = _amr.LatestStatus;
        var (orderId, orderUpdateId, lastNodeId, lastNodeSeq, driving, actionStates, nodeStates, errors)
            = _executor.Snapshot();

        var state = new Vda5050State
        {
            HeaderId = _stateHeaderId++,
            Manufacturer = _s.Manufacturer,
            SerialNumber = _s.SerialNumber,
            OrderId = orderId,
            OrderUpdateId = orderUpdateId,
            LastNodeId = lastNodeId,
            LastNodeSequenceId = lastNodeSeq,
            Driving = driving,
            OperatingMode = st?.DrivingMode == DrivingMode.Cart ? "MANUAL" : "AUTOMATIC",
            Paused = st?.RobotState == RobotState.Paused,
            NewBaseRequest = false,
            ActionStates = actionStates,
            NodeStates = nodeStates,
            Errors = errors,
        };

        if (st is not null)
        {
            state.AgvPosition = new AgvPosition
            {
                X = st.Pose.X,
                Y = st.Pose.Y,
                Theta = st.Pose.Angle,
                MapId = _mapId, // 로봇이 맵ID 미노출 → 어댑터 보유값(§4.3)
                PositionInitialized = st.MapStatusPercent >= _s.LocalizedThresholdPercent,
            };
            state.BatteryState = new BatteryState
            {
                BatteryCharge = st.Battery.LevelPercent,
                Charging = st.Battery.ChargingState == ChargingState.Charging,
            };
            state.SafetyState = new SafetyState
            {
                EStop = st.RobotStopActive != 0 ? "MANUAL" : "NONE",
                FieldViolation = false, // 레지스터 미노출 → 합성
            };
            // 로봇 플랫폼 오류 코드 — errorType 7종 매핑은 코드 목록 회신(D-9) 전이라 information 으로만 노출.
            if (st.ErrorCode != 0)
                state.Information.Add(new VdaInformation
                {
                    InfoType = "robotErrorCode",
                    InfoLevel = "INFO",
                    InfoDescription = $"TARS-M error code {st.ErrorCode} (errorType 매핑은 벤더 회신 후)",
                });
        }
        else
        {
            state.Information.Add(new VdaInformation
            {
                InfoType = "amrOffline",
                InfoLevel = "INFO",
                InfoDescription = "AMR Modbus 미연결 — 위치/배터리 상태 없음",
            });
        }

        return state;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("VDA5050 어댑터 종료.");
        await base.StopAsync(cancellationToken);
    }
}
