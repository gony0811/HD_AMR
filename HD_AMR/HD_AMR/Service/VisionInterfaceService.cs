using HD_AMR.Communication.Vision;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Service;

/// <summary>
/// 비전 인터페이스(자동화 S/W = Client)를 호스팅하는 서비스. 원격 비전 서버로 TCP 접속해
/// 프로토콜 프레임을 주고받는다. <see cref="CobotService"/>·<see cref="CameraService"/> 와
/// 동일하게 싱글톤 + 호스티드로 등록되어 페이지 이동 간에도 연결/로그 상태가 유지된다.
///
/// AMR/Cobot 과 동일하게 <b>기동 시 설정값(ServerHost/Port)으로 상시 자동 접속</b>하고,
/// 실패 시 5초마다 재시도하는 <see cref="BackgroundService"/> 루프를 돈다. 엔진의 기본
/// 파라미터는 설정에서 주입한다.
/// </summary>
public class VisionInterfaceService : BackgroundService, IAsyncDisposable
{
    private readonly VisionInterfaceSettings _settings;
    private readonly ILogger<VisionInterfaceService> _logger;

    /// <summary>자동화 S/W 측(TCP 클라이언트, ID=0x01). 비전 서버로 접속한다.</summary>
    public VisionEngine Client { get; }

    public VisionInterfaceSettings Settings => _settings;

    public VisionInterfaceService(IOptions<VisionInterfaceSettings> options, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _logger = loggerFactory.CreateLogger<VisionInterfaceService>();

        Client = new VisionEngine(SideRole.Client) { HeartbeatPeriodMs = _settings.HeartbeatPeriodMs };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "VisionInterfaceService 시작 (server={Host}:{Port}) — 상시 자동 접속",
            _settings.ServerHost, _settings.Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!Client.IsConnected)
                {
                    _logger.LogWarning("비전 인터페이스 연결 시도");
                    // 전송 계층의 자체 재연결은 끄고(autoReconnect:false), 이 루프가 5초 주기 재시도를 담당.
                    await Client.ConnectClientAsync(_settings.ServerHost, _settings.Port, autoReconnect: false);
                    _logger.LogInformation("비전 인터페이스 연결 완료");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "비전 인터페이스 연결 실패 — 5초 후 재시도");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await Client.StopAsync().ConfigureAwait(false);
        _logger.LogInformation("VisionInterfaceService 종료");
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
    }
}
