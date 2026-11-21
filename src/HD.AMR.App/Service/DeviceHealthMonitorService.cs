using Microsoft.Extensions.DependencyInjection;
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
    private readonly GasDetectorService _gas;
    private readonly LaserDisplacementSensorService _laser;
    private readonly Vda5050OrderExecutor _executor;
    private readonly OperationLogService _opLog;
    private readonly ILogger<DeviceHealthMonitorService> _logger;

    private readonly object _lock = new();
    private readonly DeviceAlarmTracker _tracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeviceAlarmStore _store;
    /// <summary>마지막으로 파일에 저장한 알람 목록(_lock 안에서만 접근) — 바뀔 때만 쓴다.</summary>
    private IReadOnlyList<DeviceAlarmEntry>? _persisted;

    /// <summary>드라이런 레시피 확인 주기(초).</summary>
    private const double DryRunRefreshSec = 2;
    private DateTime _dryRunCheckedUtc = DateTime.MinValue;
    /// <summary>사용 중(Enabled) 레시피 중 하나라도 DryRun 이면 true — 가스 디텍터 HEAVY 감시를 생략한다.
    /// 드라이런 검사 순회는 디텍터 없이 이동만 하므로 디텍터 미연결로 order 거부·임무 중단이 나면 안 된다.
    /// 일반(비드라이런) 레시피는 오케스트레이터·검사 수행 스텝이 여전히 디텍터 링크를 확인해 막는다.</summary>
    private volatile bool _gasDryRunBypass;

    public DeviceHealthMonitorService(IOptions<DeviceHealthSettings> options, DeviceAlarmState state,
        AMRService amr, CobotService cobot, CameraService camera, IoModuleService io,
        VisionInterfaceService vision, GasDetectorService gas, IOptions<InspectionSensorSettings> sensor,
        LaserDisplacementSensorService laser,
        Vda5050OrderExecutor executor, OperationLogService opLog, IServiceScopeFactory scopeFactory,
        DeviceAlarmStore store, ILogger<DeviceHealthMonitorService> logger)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _s = options.Value;
        _state = state;
        _amr = amr;
        _cobot = cobot;
        _camera = camera;
        _io = io;
        _vision = vision;
        _gas = gas;
        _laser = laser;
        _executor = executor;
        _opLog = opLog;
        _logger = logger;
        _tracker = new DeviceAlarmTracker(
            TimeSpan.FromSeconds(_s.LostAfterSec), TimeSpan.FromSeconds(_s.StartupGraceSec),
            DateTime.UtcNow, _s.Devices.EnabledKinds(sensor.Value.Mode));
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

        RestorePersisted();
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
        await RefreshDryRunBypassAsync(now);
        var samples = Sample(now);

        IReadOnlyList<DeviceAlarmEvent> events;
        IReadOnlyList<DeviceAlarmEntry> snapshot;
        lock (_lock)
        {
            events = _tracker.Evaluate(now, samples);
            snapshot = _tracker.Snapshot();
            PersistLocked(snapshot);
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
            PersistLocked(remaining);
        }
        _state.Update(remaining);

        if (cleared.Count == 0 && remaining.Count == 0) return;   // 알람 없음 — RESET 은 EMO 등 다른 용도
        var detail = (cleared.Count > 0 ? $"해제: {string.Join(", ", cleared.Select(DeviceAlarmTracker.DisplayName))}" : "해제 대상 없음")
                     + (remaining.Count > 0 ? $" / 유지(아직 끊김): {DeviceAlarmState.Describe(remaining)}" : " — order 수신 가능");
        _logger.LogInformation("장치 알람 RESET({Source}) — {Detail}", source, detail);
        _opLog.Log(OperationLogService.SourceDevice, OpCategory.Alarm, "알람 RESET", remaining.Count == 0,
            $"{source}: {detail}");
    }

    /// <summary>
    /// 재시작 전 RESET 안 된 알람 복원 — 기동 유예와 무관하게 첫 state 부터 state.errors 에 싣는다.
    /// 복원도 운영 로그에 남겨 이벤트 기록상 "발생 → (재시작) → 유지" 가 이어지게 한다. 감시 대상에서 빠진 장치의
    /// 알람은 복원하지 않고 해제로 기록한다(기록에 "발생"만 남고 현재 상태에서 조용히 사라지지 않게).
    /// </summary>
    private void RestorePersisted()
    {
        var saved = _store.Load();
        IReadOnlyList<DeviceAlarmEntry> restored, dropped, snapshot;
        lock (_lock)
        {
            (restored, dropped) = _tracker.Restore(saved);
            snapshot = _tracker.Snapshot();
            PersistLocked(snapshot);
        }
        _state.Update(snapshot);

        if (restored.Count > 0)
        {
            var detail = DeviceAlarmState.Describe(restored);
            _logger.LogWarning("장치 HEAVY 알람 복원(재시작 전 미해제) — {Detail}", detail);
            _opLog.Log(OperationLogService.SourceDevice, OpCategory.Alarm, "HEAVY 알람 복원", false,
                $"재시작 전 미해제 알람 유지: {detail} — ACS order 수신 불가, 복구 후 RESET 필요");
        }
        if (dropped.Count > 0)
        {
            var names = string.Join(", ", dropped.Select(e => DeviceAlarmTracker.DisplayName(e.Kind)));
            _logger.LogInformation("장치 알람 해제(감시 대상 아님) — {Devices}", names);
            _opLog.Log(OperationLogService.SourceDevice, OpCategory.Alarm, "알람 해제(감시 대상 아님)", true,
                $"재시작 후 감시 대상에서 빠진 장치: {names}");
        }
    }

    /// <summary>알람 목록이 바뀌었으면 파일에 저장(_lock 안에서 호출 — 저장 순서가 상태 변경 순서와 같게).</summary>
    private void PersistLocked(IReadOnlyList<DeviceAlarmEntry> snapshot)
    {
        if (_persisted is not null && _persisted.SequenceEqual(snapshot)) return;
        try
        {
            _store.Save(snapshot);
            _persisted = snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("장치 알람 저장 실패({Path}) — 재시작 시 복원 안 될 수 있음: {Err}", _store.FilePath, ex.Message);
        }
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

        r[DeviceKind.GasDetector] = _gasDryRunBypass ? new(true, "")
            : !_gas.IsPortOpen ? new(false, _gas.Settings.Transport == HD.AMR.App.Communication.GasDetector.GasDetectorTransportKind.Tcp ? "TCP 미연결" : "시리얼 포트 미연결")
            : !Fresh(_gas.LastOkUtc, _s.GasDetectorStaleSec) ? new(false, "응답 없음")
            : new(true, "");

        r[DeviceKind.Laser] = !_laser.IsConnected ? new(false, "EtherNet/IP 미연결")
            : !Fresh(_laser.LastAliveUtc, _s.LaserStaleSec) ? new(false, "생존 확인 실패")
            : new(true, "");

        return r;
    }

    /// <summary>레시피 DryRun 상태를 주기적으로 읽어 가스 디텍터 감시 생략 여부를 갱신한다.</summary>
    private async Task RefreshDryRunBypassAsync(DateTime now)
    {
        if (!_tracker.Enabled.Contains(DeviceKind.GasDetector)) return;   // 디텍터 감시 대상이 아니면(비전 AMR 등) DB 조회 불필요
        if ((now - _dryRunCheckedUtc).TotalSeconds < DryRunRefreshSec) return;
        _dryRunCheckedUtc = now;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var recipes = await scope.ServiceProvider.GetRequiredService<InspectionRecipeService>().ListAsync();
            // 가스 측정 없는 드라이런 파라미터가 켜져 있어도 디텍터 없이 미션을 돌리므로 감시를 생략한다.
            var gasDryRun = Sequence.Steps.InspectionRunStep.ResolveGasDryRun(
                await scope.ServiceProvider.GetRequiredService<ParameterService>().GetAsync(Sequence.Steps.InspectionRunStep.GasDryRunKey));
            var bypass = gasDryRun || recipes.Any(r => r.Enabled && r.DryRun);
            if (bypass == _gasDryRunBypass) return;
            _gasDryRunBypass = bypass;
            if (bypass)
                _logger.LogWarning("드라이런({Why}) 활성 — 가스 디텍터 HEAVY 감시 생략(디텍터 없이 검사 순회 허용). 이미 뜬 알람은 RESET 으로 해제",
                    gasDryRun ? $"'{Sequence.Steps.InspectionRunStep.GasDryRunKey}' 파라미터" : "레시피");
            else
                _logger.LogInformation("드라이런 없음 — 가스 디텍터 HEAVY 감시 재개");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "드라이런 레시피 확인 실패 — 가스 디텍터 감시 상태 유지({Bypass})", _gasDryRunBypass);
        }
    }
}
