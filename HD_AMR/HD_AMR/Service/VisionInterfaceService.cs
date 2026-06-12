using HD_AMR.Communication.Vision;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Service;

/// <summary>
/// 비전 인터페이스 시뮬레이터/테스터를 호스팅하는 서비스. 원본 WinForms 도구처럼 한 프로세스에서
/// 양측(자동화 S/W = Client, 비전 S/W = Server)을 모두 띄워 TCP 로 프로토콜을 수동 점검할 수 있다.
/// <see cref="CobotService"/>·<see cref="CameraService"/> 와 동일하게 싱글톤 + 호스티드로 등록되어
/// 페이지 이동 간에도 연결/로그 상태가 유지된다.
///
/// AMR/Cobot/Camera 와 달리 <b>기동 시 자동 접속하지 않는다</b> — 연결은 Vision Interface 페이지에서
/// 수동으로 시작/중지한다(불필요한 재시도 로그 방지). 두 엔진의 기본 파라미터만 설정에서 주입한다.
/// </summary>
public class VisionInterfaceService : IHostedService, IAsyncDisposable
{
    private readonly VisionInterfaceSettings _settings;
    private readonly ILogger<VisionInterfaceService> _logger;

    /// <summary>자동화 S/W 측(TCP 클라이언트, ID=0x01). 비전 서버로 접속한다.</summary>
    public VisionEngine Client { get; }

    /// <summary>비전 S/W 측(TCP 서버, ID=0x02). 내장 시뮬레이터 서버.</summary>
    public VisionEngine Server { get; }

    public VisionInterfaceSettings Settings => _settings;

    public VisionInterfaceService(IOptions<VisionInterfaceSettings> options, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _logger = loggerFactory.CreateLogger<VisionInterfaceService>();

        Client = new VisionEngine(SideRole.Client) { HeartbeatPeriodMs = _settings.HeartbeatPeriodMs };
        Server = new VisionEngine(SideRole.Server) { HeartbeatPeriodMs = _settings.HeartbeatPeriodMs };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "VisionInterfaceService 시작 (server={Host}:{Port}, bind={Bind}) — 연결은 UI 에서 수동",
            _settings.ServerHost, _settings.Port, _settings.ServerBindIp);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Client.StopAsync().ConfigureAwait(false);
        await Server.StopAsync().ConfigureAwait(false);
        _logger.LogInformation("VisionInterfaceService 종료");
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
    }
}
