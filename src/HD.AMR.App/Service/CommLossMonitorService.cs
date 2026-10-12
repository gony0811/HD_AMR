using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// 통신 단절 Fail-Safe 감시(docs/HD_AMR_통신단절_FailSafe_사양.md) — AMR 온보드 로컬 기능.
///
/// 500 ms 주기로 브로커 연결(조건 A)·ACS connection 토픽(조건 B)을 관측해 <see cref="CommLossFailSafe"/> 를 돌리고,
/// 단계 전이를 실행기에 반영한다: GRACE = 로그만, PAUSE = <see cref="Vda5050OrderExecutor.PauseAsync"/>,
/// HOLD = 에러 설명 갱신(부저는 <see cref="IoModuleService"/> 가 <see cref="Stage"/> 를 보고 울린다).
/// 정지는 AUTOMATIC + 수행 중 Order 일 때만 — 그 외에는 기록·표시만(§1).
/// 통신이 복구돼도 자동 재개하지 않는다(§4.1) — 재개·취소로 실행기가 일시정지를 풀면 <see cref="Vda5050OrderExecutor.PauseCleared"/>
/// 를 받아 단계를 리셋한다.
/// </summary>
public sealed class CommLossMonitorService : BackgroundService
{
    private const int TickMs = 500;

    private readonly CommLossSettings _s;
    private readonly bool _adapterEnabled;
    private readonly Vda5050AdapterService _vda;
    private readonly Vda5050OrderExecutor _executor;
    private readonly AMRService _amr;
    private readonly OperationLogService _opLog;
    private readonly ILogger<CommLossMonitorService> _logger;

    private readonly object _lock = new();
    private readonly CommLossFailSafe _fsm;

    /// <summary>현재 단계 — IO(타워램프·부저)·UI 표시용.</summary>
    public CommLossStage Stage
    {
        get { lock (_lock) return _fsm.Stage; }
    }

    /// <summary>단절 중 여부(복구 판정 전까지 true).</summary>
    public bool IsLost
    {
        get { lock (_lock) return _fsm.IsLost; }
    }

    /// <summary>단계·단절 상태 변화.</summary>
    public event Action? Changed;

    public CommLossMonitorService(IOptions<Vda5050AdapterSettings> options, Vda5050AdapterService vda,
        Vda5050OrderExecutor executor, AMRService amr, OperationLogService opLog, ILogger<CommLossMonitorService> logger)
    {
        _s = options.Value.CommLoss;
        _adapterEnabled = options.Value.Enabled;
        _vda = vda;
        _executor = executor;
        _amr = amr;
        _opLog = opLog;
        _logger = logger;
        _fsm = new CommLossFailSafe(TimeSpan.FromSeconds(_s.PauseAfterSec), TimeSpan.FromSeconds(_s.HoldAfterSec));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_adapterEnabled || !_s.Enabled)
        {
            _logger.LogWarning("통신 단절 Fail-Safe 비활성(Vda5050:Enabled={Adapter}, CommLoss:Enabled={Enabled}) — 현장 운영은 항상 활성이어야 함.",
                _adapterEnabled, _s.Enabled);
            return;
        }
        if (_s.HoldAfterSec <= _s.PauseAfterSec)
            _logger.LogWarning("CommLoss:HoldAfterSec({Hold}) ≤ PauseAfterSec({Pause}) — HOLD 를 {Fixed}s 로 보정.",
                _s.HoldAfterSec, _s.PauseAfterSec, _fsm.HoldAfter.TotalSeconds);
        _logger.LogInformation("통신 단절 Fail-Safe 시작 — T1(PAUSE)={T1}s, T2(HOLD)={T2}s",
            _fsm.PauseAfter.TotalSeconds, _fsm.HoldAfter.TotalSeconds);

        _executor.PauseCleared += OnPauseCleared;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "통신 단절 감시 처리 실패"); }

                try { await Task.Delay(TickMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _executor.PauseCleared -= OnPauseCleared;
        }
    }

    private async Task TickAsync()
    {
        var now = DateTime.UtcNow;
        var broker = _vda.IsBrokerConnected;
        var acs = _vda.AcsConnectionLiveness;
        var automatic = _amr.LatestStatus?.DrivingMode != DrivingMode.Cart;
        var applicable = automatic && _executor.HasActiveMission;

        IReadOnlyList<CommLossEvent> events;
        string desc;
        lock (_lock)
        {
            events = _fsm.Evaluate(now, broker, acs, applicable);
            desc = Describe(now);
        }
        if (events.Count == 0) return;

        foreach (var ev in events)
        {
            switch (ev)
            {
                case CommLossEvent.Lost:
                    _logger.LogWarning("통신 단절 판정 — {Desc}{Scope}", desc, applicable ? "" : " (MANUAL/대기 — 기록만)");
                    _opLog.Log(OperationLogService.SourceAcs, OpCategory.Link, "통신 단절 판정", false,
                        desc + (applicable ? $" — {_fsm.PauseAfter.TotalSeconds:0}s 후 PAUSE" : " — MANUAL/대기 상태라 기록만"));
                    break;

                case CommLossEvent.Recovered:
                    var stage = Stage;
                    _logger.LogInformation("통신 복구 — 단계 {Stage}", stage);
                    _opLog.Log(OperationLogService.SourceAcs, OpCategory.Link, "통신 복구", true,
                        stage is CommLossStage.Pause or CommLossStage.Hold
                            ? "일시정지 유지 — 관제 재개(stopPause) 또는 START 버튼으로 재개"
                            : "정지 없이 계속");
                    break;

                case CommLossEvent.EnterPause:
                    var paused = await _executor.PauseAsync(PauseReason.CommLoss, desc);
                    if (!paused)
                    {
                        // 판정과 실행 사이에 임무가 끝났다 — 멈출 대상이 없으므로 단계를 되돌린다.
                        lock (_lock) _fsm.Reset(DateTime.UtcNow);
                        _logger.LogInformation("통신 단절 PAUSE 생략 — 수행 중 임무 없음");
                    }
                    break;

                case CommLossEvent.EnterHold:
                    _logger.LogWarning("통신 단절 HOLD — {Desc}", desc);
                    _opLog.Log(OperationLogService.SourceAcs, OpCategory.Link, "통신 단절 HOLD", false,
                        desc + " — 정지 유지·부저 경보");
                    _executor.UpdateCommLossError(desc);
                    break;
            }
        }
        Changed?.Invoke();
    }

    /// <summary>에러·로그 설명 — 단절 시각·경과·조건(§5).</summary>
    private string Describe(DateTime now)
    {
        var t0 = _fsm.LostSinceUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "-";
        var cond = _fsm.Condition switch
        {
            "A" => "A(브로커 연결 두절)",
            "B" => "B(ACS OFFLINE/CONNECTIONBROKEN)",
            _ => "-",
        };
        var stage = _fsm.Stage switch
        {
            CommLossStage.Pause => " [PAUSE]",
            CommLossStage.Hold => " [HOLD]",
            _ => "",
        };
        return $"통신 단절 t0={t0}, 경과 {_fsm.Elapsed(now).TotalSeconds:0}s, 조건 {cond}{stage}";
    }

    private void OnPauseCleared()
    {
        lock (_lock) _fsm.Reset(DateTime.UtcNow);
        Changed?.Invoke();
    }
}
