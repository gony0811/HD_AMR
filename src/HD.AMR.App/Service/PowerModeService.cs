using HD.AMR.App.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace HD.AMR.App.Service;

/// <summary>
/// 배터리 저전력 상태기(사양 docs/HD_AMR_배터리관리_사양.md §2–3).
///
/// <see cref="AMRService.LatestStatus"/> 의 SoC 를 2 초마다 관측해 디바운스(연속 2 사이클 ≈ 4s)
/// 로 전환한다. 전환은 <see cref="Changed"/> 이벤트로 알리고, VDA5050 어댑터가 이를 구독해
/// state 를 즉시 재발행한다(§6.1).
///
/// 임계값(사양 §2):
/// <list type="bullet">
///   <item>LOW ≤ 20% (<see cref="LowThresholdPercent"/>)</item>
///   <item>CRITICAL ≤ 10% (<see cref="CriticalThresholdPercent"/>)</item>
///   <item>RESUME ≥ 80% (<see cref="ResumeThresholdPercent"/>) — Phase 3 의 명시적 "교체 완료 신호"
///     없이 BMS 가 보고하는 SoC 계단 상승(새 팩 인식, §7-A)만으로 자동 복귀한다.</item>
/// </list>
///
/// SoC 가 <c>null</c>(AMR 미연결/미수신)이면 디바운스 카운터를 리셋하고 모드는 유지한다 —
/// 통신두절로 인한 ghost 전이를 막는다.
///
/// <b>임시 해제</b>(<see cref="BypassKey"/> = true, 기본 false, 앱 재시작 후 적용): BMS 고장으로 SoC 를 믿을 수 없을 때만 쓴다.
/// 판정은 그대로 하되(<see cref="RawMode"/>) <see cref="CurrentMode"/> 를 항상 Normal 로 돌려준다 — Order 거부·CRITICAL 안전정지·
/// 저전력 잔여 액션 중단·재개 전 확인이 모두 통과한다. 해제 중에는 VDA <c>batteryGuardBypassed</c>(WARNING)를 상주 발행해 ACS 에 알린다.
/// 실제 잔량을 모르는 채 운용하는 것이므로 팩 전압을 따로 확인할 것.
/// </summary>
public sealed class PowerModeService : BackgroundService
{
    public const int LowThresholdPercent = 20;
    public const int CriticalThresholdPercent = 10;
    public const int ResumeThresholdPercent = 80;
    public const int DebounceCycles = 2;
    public const int PollIntervalMs = 2000; // 사양 §2: "2 state 주기 ≈ 4s" — state 발행 주기(2s) 와 맞춘다.

    private readonly AMRService? _amr;
    private readonly OperationLogService? _opLog;

    private int _belowLow;
    private int _belowCritical;
    private int _aboveResume;
    private PowerMode _mode;

    /// <summary>배터리 보호 임시 해제 설정 키(appsettings) — true 면 SoC 와 무관하게 Normal 로 취급한다.</summary>
    public const string BypassKey = "PowerMode:BypassBatteryGuard";

    /// <summary>배터리 보호 임시 해제 중인가(기동 때 설정에서 읽는다).</summary>
    public bool Bypassed { get; }

    /// <summary>SoC 로 판정한 실제 모드 — 해제 중에도 판정은 계속한다(경고 문구·로그용).</summary>
    public PowerMode RawMode => _mode;

    /// <summary>운영에 쓰는 모드 — 해제 중이면 항상 Normal.</summary>
    public PowerMode CurrentMode => Bypassed ? PowerMode.Normal : _mode;

    /// <summary>LOW 또는 CRITICAL 모드. VDA5050 <c>batteryLow</c> WARNING 상주 발행 조건.</summary>
    public bool BatteryLow => CurrentMode is PowerMode.Low or PowerMode.Critical;

    /// <summary>CRITICAL 모드. VDA5050 <c>batteryCritical</c> FATAL 상주 발행 조건.</summary>
    public bool BatteryCritical => CurrentMode == PowerMode.Critical;

    /// <summary>마지막 관측 SoC — 에러 설명 문자열(백분율) 용. 미관측/미연결 시 null.</summary>
    public float? LastObservedSoc { get; private set; }

    /// <summary>모드 전환 시 발행. 어댑터가 구독해 state 즉시 재발행(nudge).</summary>
    public event Action? Changed;

    public PowerModeService(AMRService amr, OperationLogService opLog, IConfiguration config)
        : this(amr, opLog, config.GetValue(BypassKey, false)) { }

    internal PowerModeService(AMRService amr, OperationLogService opLog, bool bypass)
    {
        _amr = amr;
        _opLog = opLog;
        Bypassed = bypass;
        if (bypass)
            _opLog?.Log(OperationLogService.SourceAmr, OpCategory.AmrState,
                "배터리 보호 임시 해제 중 — SoC 와 무관하게 정상 모드로 운용 (PowerMode:BypassBatteryGuard)", false,
                "BMS 고장 임시 조치 — 팩 전압을 따로 확인할 것");
    }

    /// <summary>
    /// 테스트/수동 호출용 생성자 — <see cref="Observe"/> 만 호출할 때 사용.
    /// 실제 서비스로 쓰려면 DI 의 2-인자 생성자가 선택된다.
    /// </summary>
    internal PowerModeService(bool bypass = false) : this(null!, null!, bypass) { }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Observe(_amr?.LatestStatus?.Battery?.LevelPercent); }
            catch { /* 폴링 실패는 다음 주기에 재시도 */ }

            try { await Task.Delay(PollIntervalMs, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// SoC 1회 관측. 디바운스 카운터를 갱신하고 전이 조건 충족 시 모드를 바꾼다.
    /// 테스트는 이 메서드를 직접 호출(BackgroundService 수명 무시) — 전이 로직만 검증한다.
    /// </summary>
    internal void Observe(float? socPercent)
    {
        LastObservedSoc = socPercent;

        if (socPercent is null)
        {
            // 통신두절 — 디바운스 카운터만 리셋. 마지막으로 확정된 모드는 유지한다(전이에 지연 추가).
            _belowLow = _belowCritical = _aboveResume = 0;
            return;
        }

        var soc = socPercent.Value;
        if (soc <= CriticalThresholdPercent)
        {
            _belowCritical++;
            _belowLow++;
            _aboveResume = 0;
        }
        else if (soc <= LowThresholdPercent)
        {
            // LOW 밴드(10~20%): CRITICAL 카운터는 끊어 "SoC 가 CRITICAL 근방에서 올라오는 중"에
            // CRITICAL 로 못 들어가게 하면서, LOW 카운터는 유지한다.
            _belowCritical = 0;
            _belowLow++;
            _aboveResume = 0;
        }
        else
        {
            _belowCritical = _belowLow = 0;
            _aboveResume = (soc >= ResumeThresholdPercent) ? _aboveResume + 1 : 0;
        }

        var prev = _mode;
        if (_belowCritical >= DebounceCycles)
        {
            _mode = PowerMode.Critical;
        }
        else if (_belowLow >= DebounceCycles && _mode != PowerMode.Critical)
        {
            // CRITICAL 에서 LOW 로의 복귀는 명시적 RESUME 경로(≥80%) 로만 거친다.
            // SoC 가 11% 로 떠서 CRITICAL 밴드를 벗어나도 그대로 CRITICAL 로 둔다 — Phase 2 Order 게이트의
            // 섯부른 완화를 막는다.
            _mode = PowerMode.Low;
        }
        else if (_aboveResume >= DebounceCycles && _mode != PowerMode.Normal)
        {
            _mode = PowerMode.Normal;
        }

        if (_mode != prev)
        {
            _opLog?.Log(OperationLogService.SourceAmr, OpCategory.AmrState,
                Bypassed ? $"전력 모드 {prev} → {_mode} (배터리 보호 해제 중 — 운영은 정상 모드)" : $"전력 모드 {prev} → {_mode}",
                _mode == PowerMode.Normal ? true
                    : _mode == PowerMode.Critical ? false
                    : (bool?)null,
                $"SoC {soc:0.0}%");
            Changed?.Invoke();
        }
    }
}
