using HD_AMR.LidarService.Device;

namespace HD_AMR.LidarService;

/// <summary>
/// 센서 연결을 지속적으로 유지한다.
///
/// <b>OS 네트워크 링크 복구는 이 서비스의 책임이 아니다.</b> NetworkManager 프로파일에
/// <c>connection.autoconnect=yes</c> 가 설정되어 있어, 케이블을 뽑았다 꽂으면 IP 와 경로가
/// 수동 개입 없이 자동 복구되는 것이 실측으로 확인됐다(2026-08-07). 워치독은
/// <c>nsl_open</c> 재시도만 담당한다.
///
/// ⚠ 이 전제는 젯슨의 <c>nsl-lidar</c> 프로파일 설정에 의존한다. 만약 USB 연결로 되돌리게
///   되면 그 설정을 <c>no</c> 로 꺼야 하고(LAN 이 자동 연결되면 USB 열기가 죽는다), 그때는
///   링크 복구를 누군가 대신 해줘야 한다.
/// </summary>
internal sealed class LidarWatchdog : BackgroundService
{
    private readonly LidarSession _session;
    private readonly NslDeviceOptions _deviceOptions;
    private readonly LidarWatchdogOptions _options;
    private readonly ILogger<LidarWatchdog> _log;

    private bool _loggedLinkDown;

    public LidarWatchdog(
        LidarSession session,
        NslDeviceOptions deviceOptions,
        LidarWatchdogOptions options,
        ILogger<LidarWatchdog> log)
    {
        _session = session;
        _deviceOptions = deviceOptions;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_session.IsConnected)
                    await TryReconnectAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 워치독 자체가 죽으면 서비스가 영영 재연결하지 못한다. 무슨 일이 있어도 계속 돈다.
                _log.LogError(ex, "워치독 순회 중 예외. 계속 진행한다.");
            }

            try { await Task.Delay(_options.RetryInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        var link = NetworkLink.IsUp(_deviceOptions.InterfaceName);

        // 링크가 확실히 없으면 nsl_open 을 호출하지 않는다. 단선 상태에서는 매 호출이
        // 3초를 꽉 채우고 실패하므로(캐시되지 않음) 헛되이 워커 스레드를 붙잡을 뿐이다.
        // null(모름)은 시도한다 — 모른다는 이유로 막으면 영영 붙지 못한다.
        if (link == false)
        {
            if (!_loggedLinkDown)
            {
                _log.LogWarning(
                    "이더넷 링크가 내려가 있다 ({Iface}). 케이블을 확인할 것. 링크가 살아날 때까지 연결을 시도하지 않는다.",
                    _deviceOptions.InterfaceName);
                _loggedLinkDown = true;
            }
            return;
        }

        if (_loggedLinkDown)
        {
            _log.LogInformation("이더넷 링크가 복구됐다. 센서 연결을 재시도한다.");
            _loggedLinkDown = false;
        }

        if (await _session.TryOpenAsync(ct))
            _log.LogInformation("센서 재연결 성공.");
    }
}

internal sealed class LidarWatchdogOptions
{
    /// <summary>
    /// 재연결 시도 간격.
    ///
    /// 실패한 <c>nsl_open</c> 이 TCP 타임아웃으로 3초를 소모하므로, 이보다 짧게 잡아도
    /// 실질적인 시도 빈도는 늘지 않는다. 5초면 복구 지연과 로그 소음 사이의 균형이 맞는다.
    /// </summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
}
