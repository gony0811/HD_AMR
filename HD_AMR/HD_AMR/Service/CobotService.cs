using HD_AMR.Communication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Service;

/// <summary>
/// 코봇(FAIRINO) XML-RPC 연결을 유지하는 백그라운드 서비스. 코봇은 RPC 전용이다
/// (AMR/IO는 별도 Modbus 사용). <see cref="AMRService"/>와 동일하게 5초 재연결 루프.
/// </summary>
public class CobotService : BackgroundService
{
    private readonly FairinoRpcSettings _settings;
    private readonly ILogger<CobotService> _logger;

    private readonly FairinoRpcClient _rpc;
    private readonly FairinoStateClient _state;

    public CobotService(IOptions<FairinoRpcSettings> options, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _logger = loggerFactory.CreateLogger<CobotService>();
        _rpc = new FairinoRpcClient(_settings, loggerFactory.CreateLogger<FairinoRpcClient>());
        _state = new FairinoStateClient(_settings, loggerFactory.CreateLogger<FairinoStateClient>());
    }

    /// <summary>RPC 명령 클라이언트.</summary>
    public FairinoRpcClient Rpc => _rpc;

    /// <summary>긴급 정지: 진행 중인 블로킹 이동을 가로채 즉시 StopMotion 전송(세마포어 우회).</summary>
    public Task<int> StopMotionImmediateAsync(CancellationToken ct = default)
        => _rpc.StopMotionImmediateAsync(ct);

    /// <summary>최신 RPC 실시간 상태(포즈/조인트). 미수신 시 null.</summary>
    public FairinoState? State => _state.Latest;

    /// <summary>XML-RPC 명령 연결 상태.</summary>
    public bool IsConnected => _rpc.IsConnected;

    /// <summary>상태 피드백 소켓 연결 여부.</summary>
    public bool IsStateConnected => _state.IsConnected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CobotService 시작 (RPC, {Ip}:{Cmd}/{State})",
            _settings.IpAddress, _settings.CommandPort, _settings.StatePort);

        var stateTask = _state.RunAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_rpc.IsConnected)
                {
                    _logger.LogWarning("코봇 XML-RPC 연결 시도");
                    await _rpc.ConnectAsync(stoppingToken);
                    await _rpc.RobotEnableAsync(true, stoppingToken);
                    _logger.LogInformation("코봇 XML-RPC 연결 완료");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "코봇 XML-RPC 연결 실패 — 5초 후 재시도");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        await stateTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // StopMotion 시도에 짧은 타임아웃을 건다. XML-RPC 호출은 blocking이라, 연결돼 있는데
        // 컨트롤러가 응답하지 않으면 CommandTimeoutMs(최대 수 분)까지 매달려 종료가 지연된다.
        // 여기서 짧게 끊어 호스트 종료가 신속히 진행되게 한다(잔여 blocking 호출은 백그라운드
        // 스레드라 프로세스 종료를 막지 않는다).
        try
        {
            if (_rpc.IsConnected)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                await _rpc.StopMotionAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("종료 중 StopMotion 타임아웃 — 건너뜀");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "종료 중 StopMotion 실패");
        }

        _rpc.Disconnect();
        _logger.LogInformation("CobotService 종료");
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _rpc.Dispose();
        base.Dispose();
    }
}
