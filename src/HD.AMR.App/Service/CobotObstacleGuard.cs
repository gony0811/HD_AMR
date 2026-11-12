using System.Text.Json;
using HD.AMR.App.Communication;
using HD.AMR.App.Service.Motion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 주행 중 코봇 반경 장애물 감시 — ACS 지령 주행(<see cref="Watch"/> 구간) 동안 AMR 라이다 점을 읽어, 코봇 반경 안에
/// 장애물이 들어오면 <b>주행을 세우고</b> 경고(<see cref="Active"/>)를 올린다. 화면 배너·운영 로그·타워램프·부저·
/// ACS(state.errors <see cref="CobotObstacleZone.ErrorType"/>)가 이 상태를 본다.
/// <para>
/// 재출발은 주행을 건 쪽(<c>Vda5050OrderExecutor</c>)이 <see cref="WaitClearAsync"/> 로 해소를 기다린 뒤 한다.
/// 파라미터 <c>cobotGuard.enabled</c> 로 끄고 켠다(기본 끔, 실행 중에도 2초 안에 반영).
/// 판정 규칙과 한계는 <see cref="CobotObstacleZone"/>.
/// </para>
/// </summary>
public sealed class CobotObstacleGuard : BackgroundService
{
    /// <summary>연속 이 횟수만큼 잡혀야 장애물로 본다(한 번 튄 값 제거).</summary>
    public const int ConfirmScans = 2;

    /// <summary>연속 이 횟수만큼 비어 있어야 해소로 본다.</summary>
    public const int ClearScans = 5;

    private const string BannerPrefix = "코봇 반경 장애물";

    private readonly AmrRestClient _rest;
    private readonly CobotService _cobot;
    private readonly IServiceScopeFactory _scopes;
    private readonly OperatorAlarmState _banner;
    private readonly OperationLogService _opLog;
    private readonly ILogger<CobotObstacleGuard> _logger;

    private int _watchers;
    private volatile CobotObstacleAlert? _active;
    private CobotObstacleZone.Options _options = CobotObstacleZone.Options.Off;
    private DateTime _optionsReadUtc = DateTime.MinValue;
    /// <summary>장착 yaw(T_A_B rz) [도] — 장착 보정이 없으면 null(폴백 180).</summary>
    private double? _mountYawDeg;

    public CobotObstacleGuard(AmrRestClient rest, CobotService cobot, IServiceScopeFactory scopes,
        OperatorAlarmState banner, OperationLogService opLog, ILogger<CobotObstacleGuard> logger)
    {
        _rest = rest;
        _cobot = cobot;
        _scopes = scopes;
        _banner = banner;
        _opLog = opLog;
        _logger = logger;
    }

    /// <summary>반경 안에 장애물이 있어 주행을 세운 상태면 그 내용, 아니면 null.</summary>
    public CobotObstacleAlert? Active => _active;

    /// <summary>감지·해소 때 발생(임의 스레드).</summary>
    public event Action? Changed;

    /// <summary>이 구간 동안 감시한다(주행 대기 구간을 감싼다). 끝나면 남은 경고를 지운다.</summary>
    public IDisposable Watch()
    {
        Interlocked.Increment(ref _watchers);
        return new Watcher(this);
    }

    /// <summary>장애물이 사라질 때까지 기다린다. <paramref name="deadlineUtc"/> 까지 안 사라지면 false.</summary>
    public async Task<bool> WaitClearAsync(DateTime deadlineUtc, CancellationToken ct)
    {
        while (_active is not null)
        {
            if (DateTime.UtcNow > deadlineUtc) return false;
            await Task.Delay(200, ct);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int hitStreak = 0, clearStreak = 0, failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Volatile.Read(ref _watchers) <= 0)
                {
                    hitStreak = clearStreak = 0;
                    Clear("주행 종료");
                    await Task.Delay(300, stoppingToken);
                    continue;
                }

                var options = await LoadOptionsAsync();
                if (!options.Enabled)
                {
                    hitStreak = clearStreak = 0;
                    Clear("감시 꺼짐");
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                var pose = await _rest.GetPoseAsync(stoppingToken);
                if (!pose.Ok || pose.Data is not { } data
                    || !CobotObstacleZone.TryParsePose(data, out var x, out var y, out var rz, out var lidar))
                {
                    // 라이다를 못 읽으면 판정할 수 없다 — 세우지 않고 한 번만 알린다(이미 세운 상태는 유지).
                    if (++failures == 5)
                        _logger.LogWarning("코봇 반경 장애물 감시: 라이다 조회 실패가 이어집니다 — 감시가 동작하지 않습니다 ({Msg})",
                            pose.Message ?? "응답 형식 오류");
                    await Task.Delay(options.PollMs, stoppingToken);
                    continue;
                }
                if (failures >= 5) _logger.LogInformation("코봇 반경 장애물 감시: 라이다 조회 복구");
                failures = 0;

                var joints = _cobot.State?.JointPos;
                var zone = CobotObstacleZone.ResolveZone(options, joints, _mountYawDeg, CobotEnvelope.Default, Fr5Kinematics.Fr5Wml);
                var hit = CobotObstacleZone.Detect(x, y, rz, lidar, zone, CobotEnvelope.Default, options.MinPoints);

                if (hit is not null)
                {
                    clearStreak = 0;
                    if (_active is null && ++hitStreak >= ConfirmScans)
                        await RaiseAsync(new CobotObstacleAlert(hit, zone, DateTime.UtcNow), stoppingToken);
                }
                else
                {
                    hitStreak = 0;
                    if (_active is not null && ++clearStreak >= ClearScans)
                    {
                        clearStreak = 0;
                        Clear("장애물 사라짐");
                    }
                }
                await Task.Delay(options.PollMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "코봇 반경 장애물 감시 오류 — 1초 뒤 다시 시도");
                try { await Task.Delay(1000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RaiseAsync(CobotObstacleAlert alert, CancellationToken ct)
    {
        var stop = await _rest.StopAsync(ct);
        _active = alert;
        var text = $"{BannerPrefix} 감지 — 주행 정지: {alert.Hit.Describe()}, 감시 영역 {alert.Zone.Describe()}";
        _logger.LogWarning("{Text}{Stop}", text, stop.Ok ? "" : $" (정지 명령 실패 code={stop.Code}: {stop.Message})");
        _opLog.Log(OperationLogService.SourceAmr, OpCategory.Drive, "코봇 반경 장애물 — 주행 정지", stop.Ok ? null : false,
            text + (stop.Ok ? " · 장애물이 사라지면 다시 출발" : $" · 정지 명령 실패(code={stop.Code}: {stop.Message})"));
        _banner.Raise(text + " — 장애물을 치우면 다시 출발합니다", isError: false);
        Changed?.Invoke();
    }

    private void Clear(string why)
    {
        var was = _active;
        if (was is null) return;
        _active = null;
        var held = (DateTime.UtcNow - was.SinceUtc).TotalSeconds;
        _logger.LogInformation("코봇 반경 장애물 해소 — {Why} (정지 {Sec:0.0}초)", why, held);
        _opLog.Log(OperationLogService.SourceAmr, OpCategory.Drive, "코봇 반경 장애물 해소", true, $"{why}, 정지 {held:0.0}초");
        if (_banner.Current?.Text.StartsWith(BannerPrefix, StringComparison.Ordinal) == true)
            _banner.Clear();
        Changed?.Invoke();
    }

    /// <summary>파라미터는 2초마다 다시 읽는다 — 파라미터 화면에서 바꾸면 재시작 없이 반영된다.</summary>
    private async Task<CobotObstacleZone.Options> LoadOptionsAsync()
    {
        if ((DateTime.UtcNow - _optionsReadUtc).TotalSeconds < 2) return _options;
        try
        {
            using var scope = _scopes.CreateScope();
            var p = scope.ServiceProvider.GetRequiredService<ParameterService>();
            var next = CobotObstacleZone.Options.From(
                await p.GetBoolAsync(CobotObstacleZone.EnabledKey),
                await p.GetDoubleAsync(CobotObstacleZone.RadiusKey),
                await p.GetDoubleAsync(CobotObstacleZone.MarginKey),
                await p.GetIntAsync(CobotObstacleZone.MinPointsKey),
                await p.GetIntAsync(CobotObstacleZone.PollKey));
            if (next.Enabled != _options.Enabled)
                _logger.LogInformation("코봇 반경 장애물 감시 {State} (반경 {Radius}, 여유 {Margin:0}mm, 점 {Min}개 이상, {Poll}ms)",
                    next.Enabled ? "켜짐" : "꺼짐", next.RadiusMm > 0 ? $"{next.RadiusMm:0}mm" : "자동",
                    next.MarginMm, next.MinPoints, next.PollMs);
            _options = next;
            if (next.Enabled)
            {
                var mount = await scope.ServiceProvider.GetRequiredService<CalibrationService>().GetMountAsync();
                _mountYawDeg = mount is { Length: 6 } && mount.Any(v => Math.Abs(v) > 1e-9) ? mount[5] : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("코봇 반경 장애물 감시 파라미터 조회 실패 — 직전 값 유지: {Msg}", ex.Message);
        }
        _optionsReadUtc = DateTime.UtcNow;
        return _options;
    }

    private sealed class Watcher : IDisposable
    {
        private CobotObstacleGuard? _owner;
        public Watcher(CobotObstacleGuard owner) => _owner = owner;

        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            if (o is not null) Interlocked.Decrement(ref o._watchers);
        }
    }
}
