using HD_AMR.Communication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Service;

public class AMRService : BackgroundService
{
    private readonly AmrModbusTcpSettings _settings;
    private readonly ModbusTcpClient _client;
    private readonly ILogger<AMRService> _logger;

    public AMRService(IOptions<AmrModbusTcpSettings> options, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _client = new ModbusTcpClient(_settings, loggerFactory.CreateLogger<ModbusTcpClient>());
        _logger = loggerFactory.CreateLogger<AMRService>();
    }

    public bool IsConnected => _client.IsConnected;

    public ModbusTcpClient Client => _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AMRService 시작 ({Ip}:{Port})", _settings.IpAddress, _settings.Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    _logger.LogWarning("AMR Modbus TCP 연결 시도");
                    await _client.ConnectAsync(stoppingToken);
                    _logger.LogInformation("AMR Modbus TCP 연결 완료");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AMR Modbus TCP 연결 실패 — 5초 후 재시도");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Disconnect();
        _logger.LogInformation("AMRService 종료");
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
