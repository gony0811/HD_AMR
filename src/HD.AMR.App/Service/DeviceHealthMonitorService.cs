using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// 장치 통신 HEAVY 알람 감시 — 검사 비전카메라·Depth 카메라·3점 레이저·I/O 모듈·코봇·AMR.
///
/// 500 ms 주기로 각 장치의 연결·데이터 신선도를 관측해 <see cref="DeviceAlarmTracker"/> 를 돌린다.
/// 알람 발생 시: 운영 로그(ALARM) + <see cref="DeviceAlarmState"/> 갱신(→ state.errors 에 장치별 FATAL,
/// 타워램프 적색, order 거부) + 진행 중 임무 즉시 중단. 복구돼도 자동 해제하지 않고 RESET 으로만 해제한다.
/// </summary>
public sealed class DeviceHealthMonitorService : BackgroundService
{
    private const int TickMs = 500;

    private readonly DeviceHealthSettings _s;
    private readonly DeviceAlarmState _state;
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly IoModuleService _io;
    private readonly VisionInterfaceService _vision;
    private readonly LaserDisplacementSensorService _laser;
    private readonly Vda5050OrderExecutor _executor;
    private readonly OperationLogService _opLog;
    private readonly ILogger<DeviceHealthMonitorService> _logger;

    private readonly object _lock = new();
    private readonly DeviceAlarmTracker _tracker;

    public DeviceHealthMonitorService(IOptions<DeviceHealthSettings> options, DeviceAlarmState state,
        AMRService amr, CobotService cobot, CameraService camera, IoModuleService io,
        VisionInterfaceService vision, LaserDisplacementSensorService laser,
        Vda5050OrderExecutor executor, OperationLogService opLog, ILogger<DeviceHealthMonitorService> logger)
    {
        _s = options.Value;
        _state = state;
        _amr = amr;
        _cobot = cobot;
        _camera = camera;
        _io = io;
        _vision = vision;
        _laser = laser;
        _executor = executor;
        _opLog = opLog;
        _logger = logger;
        _tracker = new DeviceAlarmTracker(
            TimeSpan.FromSeconds(_s.LostAfterSec), TimeSpan.FromSeconds(_s.StartupGraceSec),
            DateTime.UtcNow, _s.Devices.EnabledKinds());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_s.Enabled)
        {
            _logger.LogWarning("장치 HEAVY 알람 감시 비활성(DeviceHealth:Enabled=false) — 현장 운영은 항상 활성이어야 함.");
            return;
        }
        _logger.LogInformation("장치 HEAVY 알람 감시 시작 — 대상 [{Devices}], 판정 {Lost}s, 기동 유예 {Grace}s",
            string.Join(", ", _tracker.Enabled.Select(DeviceAlarmTracker.DisplayName)), _s.LostAfterSec, _s.StartupGraceSec);

        _state.ResetRequested += OnResetRequested;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "장치 알람 감시 처리 실패"); }

                try { await Task.Delay(TickMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _state.ResetRequested -= OnResetRequested;
        }
    }

    private async Task TickAsync()
    {
        var now = DateTime.UtcNow;
        var samples = Sample(now);

        IReadOnlyList<DeviceAlarmEvent> events;
        IReadOnlyList<DeviceAlarmEntry> snapshot;
        lock (_lock)
        {
            events = _tracker.Evaluate(now, samples);
            snapshot = _tracker.Snapshot();
        }

        foreach (var ev in events)
        {
            var name = DeviceAlarmTracker.DisplayName(ev.Kind);
            if (ev.Event == DeviceAlarmEventKind.Raised)
            {
                _logger.LogError("HEAVY 알람 발생 — {Device}: {Detail}", name, ev.Detail);
                _opLog.Log(OperationLogService.SourceDevice, OpCategory.Alarm, $"HEAVY 알람 발생 — {name}", false,
                    $"{ev.Detail} — ACS order 수신 불가, 복구 후 RESET 필요");
            }
            else
            {
                _logger.LogWarning("장치 통신 복구 — {Device} (알람 유지, RESET 대기)", name);
                _opLog.Log(OperationLogService.SourceDevice, OpCategory.Alarm, $"통신 복구 — {name}", null,
                    "알람 유지 — 조작반 RESET 또는 화면 알람 리셋으로 해제");
            }
        }

        _state.Update(snapshot);

        foreach (var ev in events.Where(e => e.Event == DeviceAlarmEventKind.Raised))
            await _executor.AbortForDeviceFaultAsync($"{DeviceAlarmTracker.DisplayName(ev.Kind)} 통신 두절({ev.Detail})");
    }

    private void OnResetRequested(string source)
    {
        IReadOnlyList<DeviceKind> cleared;
        IReadOnlyList<DeviceAlarmEntry> remaining;
        lock (_lock)
        {
            cleared = _tracker.Reset();
            remaining = _tracker.Snapshot();
        }
        _state.Update(remaining);

        if (cleared.Count == 0 && remaining.Count == 0) return;   // 알람 없음 — RESET 은 EMO 등 다른 용도
        var detail = (cleared.Count > 0 ? $"해제: {string.Join(", ", cleared.Select(DeviceAlarmTracker.DisplayName))}" : "해제 대상 없음")
                     + (remaining.Count > 0 ? $" / 유지(아직 끊김): {DeviceAlarmState.Describe(remaining)}" : " — order 수신 가능");
        _logger.LogInformation("장치 알람 RESET({Source}) — {Detail}", source, detail);
        _opLog.Log(OperationLogService.SourceDevice, OpCategory.Alarm, "알람 RESET", remaining.Count == 0,
            $"{source}: {detail}");
    }

    /// <summary>장치별 관측 — 사유 문자열은 값이 바뀌지 않게(경과 시간 등 미포함) 둬 상태 변경 이벤트가 매 주기 나지 않게 한다.</summary>
    private Dictionary<DeviceKind, DeviceHealthSample> Sample(DateTime now)
    {
        bool Fresh(DateTime? at, double sec) => at is { } t && (now - t).TotalSeconds <= sec;

        var r = new Dictionary<DeviceKind, DeviceHealthSample>();

        var st = _amr.LatestStatus;
        r[DeviceKind.Amr] = st is null ? new(false, "Modbus 미연결/상태 읽기 실패")
            : !Fresh(st.ReadAtUtc, _s.AmrStaleSec) ? new(false, "상태 갱신 중단")
            : new(true, "");

        var cs = _cobot.State;
        r[DeviceKind.Cobot] = !_cobot.IsConnected ? new(false, "XML-RPC 미연결")
            : !_cobot.IsStateConnected ? new(false, "상태 소켓(20004) 끊김")
            : cs is null ? new(false, "상태 미수신")
            : !Fresh(cs.UpdatedAt, _s.CobotStaleSec) ? new(false, "상태 패킷 중단")
            : new(true, "");

        r[DeviceKind.IoModule] = !_io.IsConnected ? new(false, "Modbus 미연결")
            : !Fresh(_io.LastInputsOkUtc, _s.IoStaleSec) ? new(false, "입력 읽기 실패")
            : new(true, "");

        r[DeviceKind.DepthCamera] = !_camera.IsConnected ? new(false, "미연결")
            : !_camera.IsStreaming ? new(false, "스트림 중지")
            : !Fresh(_camera.LastFrameAt, _s.DepthCameraStaleSec) ? new(false, "프레임 수신 중단")
            : new(true, "");

        var vc = _vision.Client;
        r[DeviceKind.Vision] = !vc.IsConnected ? new(false, "TCP 미연결")
            : _s.VisionRxStaleSec > 0 && !Fresh(vc.LastRxUtc, _s.VisionRxStaleSec) ? new(false, "HEARTBEAT 무응답")
            : new(true, "");

        r[DeviceKind.Laser] = !_laser.IsConnected ? new(false, "EtherNet/IP 미연결")
            : !Fresh(_laser.LastAliveUtc, _s.LaserStaleSec) ? new(false, "생존 확인 실패")
            : new(true, "");

        return r;
    }
}
