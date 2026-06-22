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
/// AMR/Cobot/Camera 와 달리 <b>기동 시 자동 접속하지 않는다</b> — 연결은 Vision Interface 페이지에서
/// 수동으로 시작/중지한다(불필요한 재시도 로그 방지). 엔진의 기본 파라미터만 설정에서 주입한다.
/// </summary>
public class VisionInterfaceService : IHostedService, IAsyncDisposable
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "VisionInterfaceService 시작 (server={Host}:{Port}) — 연결은 UI 에서 수동",
            _settings.ServerHost, _settings.Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Client.StopAsync().ConfigureAwait(false);
        _logger.LogInformation("VisionInterfaceService 종료");
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
    }
}
