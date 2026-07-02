using HD_AMR.Communication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Service;

/// <summary>
/// LS산전 IO Module ModbusTCP 서비스. AMR/Cobot 과 동일하게 싱글톤 + 호스티드로 등록되어
/// 기동 시 자동 접속하고 실패 시 5초마다 재접속한다. IO list(주소 매핑)가 아직 미정이라
/// 고정 폴링 없이 연결만 유지하고, 페이지에서 임의 주소를 읽고/쓸 수 있도록 범용 pass-through
/// 메서드를 노출한다. (추후 IO list 확정 시 여기에 typed 메서드 추가)
/// </summary>
public class IoModuleService : BackgroundService
{
    private readonly IoModuleModbusTcpSettings _settings;
    private readonly ModbusTcpClient _client;
    private readonly ILogger<IoModuleService> _logger;

    private readonly object _stateLock = new();
    private IoModuleState? _state;

    public IoModuleService(IOptions<IoModuleModbusTcpSettings> options, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _client = new ModbusTcpClient(_settings, loggerFactory.CreateLogger<ModbusTcpClient>());
        _logger = loggerFactory.CreateLogger<IoModuleService>();
    }

    public bool IsConnected => _client.IsConnected;

    public ModbusTcpClient Client => _client;

    public IoModuleModbusTcpSettings Settings => _settings;

    /// <summary>마지막 연결/통신 오류 메시지 (정상 시 null)</summary>
    public string? LastError { get; private set; }

    /// <summary>가장 최근 캐싱된 입출력 상태 스냅샷 (아직 한 번도 못 읽었으면 null)</summary>
    public IoModuleState? GetState()
    {
        lock (_stateLock)
        {
            return _state;
        }
    }

    // 연결 실패 warn을 끊김당 1회만 남기기 위한 플래그(재연결 성공 시 리셋).
    private bool _retryWarned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IoModuleService 시작 ({Ip}:{Port}, SlaveId={SlaveId})",
            _settings.IpAddress, _settings.Port, _settings.SlaveId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    _logger.LogDebug("IO Module Modbus TCP 연결 시도");
                    await _client.ConnectAsync(stoppingToken);
                    LastError = null;
                    _retryWarned = false;
                    _logger.LogInformation("IO Module Modbus TCP 연결 완료");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                if (!_retryWarned)
                {
                    _logger.LogWarning("IO Module Modbus TCP 연결 실패, 이후 자동 재시도 — {Err}", ex.Message);
                    _retryWarned = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            // 입력(FC02 ×16)·출력(FC01 ×8)을 읽어 스냅샷 캐싱. 읽기 실패는 LastError에 기록하고 계속(연결 유지 로직은 위에서 처리).
            try
            {
                await PollStateAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger.LogDebug(ex, "IO Module 상태 폴링 실패");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Disconnect();
        _logger.LogInformation("IoModuleService 종료");
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    #region XBE-DC16A(입력) / XBE-TN08A(출력) typed 접근

    /// <summary>입력 접점(XBE-DC16A, 16점) 읽기 — Discrete Input(FC02)</summary>
    public Task<bool[]> ReadInputsAsync(CancellationToken ct = default)
        => RunAsync(() => _client.ReadDiscreteInputsAsync(
            IoModuleRegisterMap.Input.Start, IoModuleRegisterMap.Input.Count, ct));

    /// <summary>출력 상태(XBE-TN08A, 8점) 읽기 — Coil(FC01)</summary>
    public Task<bool[]> ReadOutputsAsync(CancellationToken ct = default)
        => RunAsync(() => _client.ReadCoilsAsync(
            IoModuleRegisterMap.Output.Start, IoModuleRegisterMap.Output.Count, ct));

    /// <summary>출력 접점(XBE-TN08A) 한 점을 ON/OFF — Coil(FC05). 쓰기 직후 되읽어 스냅샷 갱신.</summary>
    public async Task WriteOutputAsync(int index, bool value, CancellationToken ct = default)
    {
        if (index < 0 || index >= IoModuleRegisterMap.Output.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"출력 인덱스는 0~{IoModuleRegisterMap.Output.Count - 1} 범위여야 합니다.");

        var address = (ushort)(IoModuleRegisterMap.Output.Start + index);
        await RunAsync(() => _client.WriteSingleCoilAsync(address, value, ct));

        // 명령 반영을 즉시 UI에 보여주기 위해 곧바로 되읽어 스냅샷 갱신.
        try
        {
            await PollStateAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "출력 쓰기 후 상태 재읽기 실패");
        }
    }

    /// <summary>입력·출력을 한 번 읽어 <see cref="IoModuleState"/> 스냅샷으로 캐싱.</summary>
    private async Task PollStateAsync(CancellationToken ct)
    {
        var inputs = await _client.ReadDiscreteInputsAsync(
            IoModuleRegisterMap.Input.Start, IoModuleRegisterMap.Input.Count, ct);
        var outputs = await _client.ReadCoilsAsync(
            IoModuleRegisterMap.Output.Start, IoModuleRegisterMap.Output.Count, ct);

        var snapshot = new IoModuleState(inputs, outputs, DateTime.UtcNow);
        lock (_stateLock)
        {
            _state = snapshot;
        }
        LastError = null;
    }

    #endregion

    #region 범용 Modbus 읽기/쓰기 (IO list 확정 전 테스트용)

    /// <summary>Coil(출력, FC01) 읽기</summary>
    public Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => RunAsync(() => _client.ReadCoilsAsync(startAddress, count, ct));

    /// <summary>Discrete Input(입력, FC02) 읽기</summary>
    public Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => RunAsync(() => _client.ReadDiscreteInputsAsync(startAddress, count, ct));

    /// <summary>Holding Register(FC03) 읽기</summary>
    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => RunAsync(() => _client.ReadHoldingRegistersAsync(startAddress, count, ct));

    /// <summary>Input Register(FC04) 읽기</summary>
    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => RunAsync(() => _client.ReadInputRegistersAsync(startAddress, count, ct));

    /// <summary>Coil(출력, FC05) 단일 쓰기</summary>
    public Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken ct = default)
        => RunAsync(() => _client.WriteSingleCoilAsync(address, value, ct));

    /// <summary>Holding Register(FC06) 단일 쓰기</summary>
    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
        => RunAsync(() => _client.WriteSingleRegisterAsync(address, value, ct));

    /// <summary>오류를 LastError 로 기록하며 실행 (값 반환용)</summary>
    private async Task<T> RunAsync<T>(Func<Task<T>> op)
    {
        try
        {
            var result = await op();
            LastError = null;
            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    /// <summary>오류를 LastError 로 기록하며 실행 (반환값 없음)</summary>
    private async Task RunAsync(Func<Task> op)
    {
        try
        {
            await op();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    #endregion
}
