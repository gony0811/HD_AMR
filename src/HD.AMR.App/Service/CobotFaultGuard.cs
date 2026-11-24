using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 코봇 결함 감시 — 20004 상태 패킷의 main 결함 코드가 서면 <b>즉시</b>:
/// <list type="number">
/// <item>StopMotion — 컨트롤러가 멈춘 이동의 남은 부분을 버린다. 결함 해제(펜던트·웹 리셋) 뒤 팔이 혼자 이어 가지 않게 한다.</item>
/// <item>진행 중 ACS 임무 중단 — 시퀀스 취소·AMR 정지·잔여 액션 FAILED(취소 경로라 실패 후 홈 복귀 이동도 하지 않는다).</item>
/// <item>화면 배너(적색)·운영 로그.</item>
/// </list>
/// 그리고 <see cref="DeviceHealthMonitorService"/> 가 <see cref="Unhealthy"/> 를 코봇 관측값으로 써서 HEAVY 알람을 낸다
/// (타워램프·ACS state.errors·order 거부, 화면 RESET 으로만 해제).
/// <para>
/// 배경(2026-11-22 11:35): 피치 이동 중 결함(main=4, sub=3)으로 팔이 멈췄는데 앱은 로그만 남기고 MoveL 응답을 기다렸다.
/// 6분 뒤 작업자가 웹에서 결함을 끄고 J3 를 조그하자 컨트롤러가 남은 이동을 이어 갔고 시퀀스도 그대로 진행됐다.
/// </para>
/// </summary>
public sealed class CobotFaultGuard : BackgroundService
{
    private const int PollMs = 50;

    /// <summary>HEAVY 알람 사유 앞머리 — 감시 서비스가 "통신 두절" 대신 "결함"으로 적는 데 쓴다.</summary>
    public const string DetailPrefix = "결함 코드";

    private readonly CobotService _cobot;
    private readonly Vda5050OrderExecutor _executor;
    private readonly OperatorAlarmState _banner;
    private readonly OperationLogService _opLog;
    private readonly ILogger<CobotFaultGuard> _logger;
    private readonly CobotFaultLatch _latch = new(TimeSpan.FromSeconds(5));
    private readonly object _lock = new();

    public CobotFaultGuard(CobotService cobot, Vda5050OrderExecutor executor, OperatorAlarmState banner,
        OperationLogService opLog, ILogger<CobotFaultGuard> logger)
    {
        _cobot = cobot;
        _executor = executor;
        _banner = banner;
        _opLog = opLog;
        _logger = logger;
    }

    /// <summary>결함 중이거나 결함 직후 유지 시간 안이면 사유, 아니면 null — HEAVY 알람 관측값.</summary>
    public string? Unhealthy
    {
        get { lock (_lock) return _latch.Detail; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s = _cobot.IsStateConnected ? _cobot.State : null;
                CobotFaultLatch.Edge edge;
                lock (_lock) edge = _latch.Update(DateTime.UtcNow, s?.ErrorCode ?? 0, s?.SubCode ?? 0);

                if (edge == CobotFaultLatch.Edge.Tripped)
                    await OnTrippedAsync(s!);
                else if (edge == CobotFaultLatch.Edge.Cleared)
                    await OnClearedAsync();

                await Task.Delay(PollMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "코봇 결함 감시 오류 — 1초 뒤 다시 시도");
                try { await Task.Delay(1000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task OnTrippedAsync(Communication.FairinoState s)
    {
        var code = $"main={s.ErrorCode}, sub={s.SubCode}";
        var stop = await TryStopMotionAsync();
        _logger.LogError("코봇 결함({Code}) — 이동 정지(StopMotion {Stop}), 진행 중 임무 중단", code, stop);

        var aborted = false;
        try { aborted = await _executor.AbortForDeviceFaultAsync($"코봇 결함({code})"); }
        catch (Exception ex) { _logger.LogError(ex, "코봇 결함: 임무 중단 실패"); }

        var text = $"코봇 결함({code}) — 이동을 멈추고 {(aborted ? "진행 중 임무를 중단했습니다" : "남은 이동을 취소했습니다")}. " +
                   "원인 확인 → 코봇 결함 해제 → 화면 알람 RESET 순서로 복구하세요";
        _opLog.Log(OperationLogService.SourceDevice, OpCategory.Alarm, "코봇 결함 — 이동 정지", false,
            $"{text} (StopMotion {stop})");
        _banner.Raise(text, isError: true);
    }

    /// <summary>결함이 풀린 직후 — 컨트롤러가 멈춘 이동을 이어 가려 해도 바로 세운다(결함 중 StopMotion 이 안 먹힌 경우 대비).</summary>
    private async Task OnClearedAsync()
    {
        var stop = await TryStopMotionAsync();
        _logger.LogWarning("코봇 결함 해제 감지 — 남은 이동 재개 방지용 StopMotion {Stop}. 화면 알람 RESET 전까지 ACS order 를 받지 않습니다", stop);
    }

    private async Task<string> TryStopMotionAsync()
    {
        if (!_cobot.IsConnected) return "생략(RPC 미연결)";
        try
        {
            var rc = await _cobot.StopMotionImmediateAsync();
            return rc == 0 ? "완료" : $"rc={rc}";
        }
        catch (Exception ex)
        {
            return $"실패({ex.Message})";
        }
    }
}

/// <summary>
/// 결함 판정(순수 로직). main 코드가 연속 <see cref="ConfirmSamples"/> 번 0 이 아니면 Tripped.
/// 결함이 풀려도 Tripped 뒤 <c>hold</c> 동안은 <see cref="Detail"/> 을 유지한다 — HEAVY 판정(3초)보다 짧게
/// 풀리면(자동 ResetAllError·빠른 수동 리셋) 알람이 안 떠 order 가 그대로 이어지는 것을 막는다.
/// </summary>
public sealed class CobotFaultLatch
{
    public const int ConfirmSamples = 2;

    public enum Edge { None, Tripped, Cleared }

    private readonly TimeSpan _hold;
    private int _streak;
    private bool _faulted;
    private DateTime _trippedAt;
    private string? _detail;

    public CobotFaultLatch(TimeSpan hold) => _hold = hold;

    /// <summary>결함 중이거나 유지 시간 안이면 사유.</summary>
    public string? Detail { get; private set; }

    public Edge Update(DateTime now, int main, int sub)
    {
        var edge = Edge.None;
        if (main != 0)
        {
            if (!_faulted && ++_streak >= ConfirmSamples)
            {
                _faulted = true;
                _trippedAt = now;
                _detail = $"{CobotFaultGuard.DetailPrefix} main={main}, sub={sub}";
                edge = Edge.Tripped;
            }
        }
        else
        {
            _streak = 0;
            if (_faulted)
            {
                _faulted = false;
                edge = Edge.Cleared;
            }
        }

        Detail = _faulted || (_detail is not null && now - _trippedAt < _hold) ? _detail : null;
        if (Detail is null) _detail = null;
        return edge;
    }
}
