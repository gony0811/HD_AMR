using System.Text.Json;
using HD_AMR.Communication.Vda5050;
using HD_AMR.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace HD_AMR.Service;

/// <summary>
/// HD_AMR VDA 5050 어댑터(스켈레톤) — ACS(마스터)에 대한 AGV 에이전트.
/// 현재 범위: <b>connection(ONLINE/OFFLINE/Last Will) + state 2초 발행</b>, order/instantActions <b>수신 스텁</b>.
/// 상태는 <see cref="AMRService"/> 의 Modbus 폴링 결과(LatestStatus)를 VDA5050 state로 매핑한다.
/// 이동 실현(node→Job/Task Index→TARS-M)·코봇 인터록은 후속 단계(TODO).
/// 사양: HD_ACS docs/VDA5050_SPEC_PLAN, VDA5050_NODE_INDEX_TRANSMISSION, VDA5050_ACTION_CATALOG.
/// </summary>
public sealed class Vda5050AdapterService : BackgroundService
{
    private readonly Vda5050AdapterSettings _s;
    private readonly AMRService _amr;
    private readonly ILogger<Vda5050AdapterService> _logger;

    private IMqttClient? _client;
    private int _stateHeaderId;
    private int _connHeaderId;

    public Vda5050AdapterService(IOptions<Vda5050AdapterSettings> options, AMRService amr, ILoggerFactory loggerFactory)
    {
        _s = options.Value;
        _amr = amr;
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

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;

        // Last Will = connection CONNECTIONBROKEN (retained) — 비정상 두절 시 브로커가 대신 발행.
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
        _logger.LogInformation("VDA5050 브로커 접속 완료.");

        // 수신 구독 (order/instantActions)
        await _client.SubscribeAsync(Vda5050Topics.Order(_s), MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await _client.SubscribeAsync(Vda5050Topics.InstantActions(_s), MqttQualityOfServiceLevel.AtLeastOnce, ct);

        // connection ONLINE (retained)
        await PublishAsync(Vda5050Topics.Connection(_s), BuildConnection("ONLINE"), retain: true, ct);

        // state 주기 발행 루프
        try
        {
            while (!ct.IsCancellationRequested && _client.IsConnected)
            {
                await PublishAsync(Vda5050Topics.State(_s), BuildState(), retain: true, ct);
                await Task.Delay(_s.StatePeriodMs, ct);
            }
        }
        finally
        {
            // 정상 종료 — OFFLINE(retained) 명시 발행 후 정리.
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

    // ── 수신 핸들러(스텁) ─────────────────────────────────────────────
    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var json = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
        if (topic.EndsWith("/order"))
        {
            // TODO: order 파싱 → 노드 순회(ensureCobotSafe → node Job/Task Index 이동 → 액션) + actionState 보고.
            _logger.LogInformation("VDA5050 order 수신(미처리 스텁): {Len}B", json.Length);
        }
        else if (topic.EndsWith("/instantActions"))
        {
            // TODO: cancelOrder/startPause/stopPause/initPosition/emergencyStop → TARS-M 상태제어·포즈탐색 매핑.
            _logger.LogInformation("VDA5050 instantActions 수신(미처리 스텁): {Len}B", json.Length);
        }
        return Task.CompletedTask;
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

    /// <summary>AMRService.LatestStatus(Modbus 폴링) → VDA5050 state 매핑(운영 서브셋).</summary>
    private Vda5050State BuildState()
    {
        var st = _amr.LatestStatus;
        var state = new Vda5050State
        {
            HeaderId = _stateHeaderId++,
            Manufacturer = _s.Manufacturer,
            SerialNumber = _s.SerialNumber,
            OperatingMode = st?.DrivingMode == DrivingMode.Cart ? "MANUAL" : "AUTOMATIC",
            Driving = st?.RobotState == RobotState.Started,
            Paused = st?.RobotState == RobotState.Paused,
        };

        if (st is not null)
        {
            state.AgvPosition = new AgvPosition
            {
                X = st.Pose.X,
                Y = st.Pose.Y,
                Theta = st.Pose.Angle,
                MapId = _s.MapId, // AMR이 맵ID 미노출 → 어댑터 보유값
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
            if (st.ErrorCode != 0)
                state.Errors.Add(new VdaError
                {
                    ErrorType = "robotError",
                    ErrorLevel = "WARNING",
                    ErrorDescription = $"TARS-M error code {st.ErrorCode}",
                });
        }
        else
        {
            state.Information.Add(new VdaInformation
            {
                InfoType = "amrOffline",
                InfoLevel = "INFO",
                InfoDescription = "AMR Modbus 미연결 — 상태 없음",
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
