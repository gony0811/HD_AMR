using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NModbus;

namespace HD.AMR.App.Communication;

public class ModbusTcpClient : IDisposable
{
    private readonly ModbusTcpSettings _settings;
    private readonly ILogger<ModbusTcpClient> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;
    private bool _disposed;

    public ModbusTcpClient(ModbusTcpSettings settings, ILogger<ModbusTcpClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _tcpClient?.Connected ?? false;

    public string Name => _settings.Name;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;

        // zombie 리소스 정리: master가 있으면 master가 tcpClient 포함 정리
        if (_master != null)
        {
            try { _master.Dispose(); } catch { }
            _master = null;
            _tcpClient = null;
        }
        else if (_tcpClient != null)
        {
            try { _tcpClient.Dispose(); } catch { }
            _tcpClient = null;
        }

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(_settings.IpAddress, _settings.Port, ct);

            var factory = new ModbusFactory();
            var master = factory.CreateMaster(tcpClient);
            master.Transport.ReadTimeout = _settings.ReadTimeoutMs;
            master.Transport.WriteTimeout = _settings.WriteTimeoutMs;

            _tcpClient = tcpClient;
            _master = master;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        // 진행 중인 Modbus 작업이 완료될 때까지 대기 (최대 5초)
        var acquired = await _semaphore.WaitAsync(TimeSpan.FromSeconds(5), ct);
        try
        {
            // NModbus dispose 체인: Master → TcpClientAdapter → TcpClient → Socket.Close
            try { _master?.Dispose(); }
            catch { }
            _master = null;
            _tcpClient = null;
        }
        finally
        {
            if (acquired) _semaphore.Release();
        }
    }

    public void Disconnect()
    {
        try { _master?.Dispose(); }
        catch { }
        _master = null;
        _tcpClient = null;
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("{Name} ReadHoldingRegisters: address={Address}, count={Count}", _settings.Name, startAddress, count);
            var result = await Master.ReadHoldingRegistersAsync(_settings.SlaveId, startAddress, count);
            _logger.LogDebug("{Name} ReadHoldingRegisters 성공: {Count}개", _settings.Name, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            OnOperationFailed(ex, $"ReadHoldingRegisters(address={startAddress}, count={count})");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("{Name} ReadInputRegisters: address={Address}, count={Count}", _settings.Name, startAddress, count);
            var result = await Master.ReadInputRegistersAsync(_settings.SlaveId, startAddress, count);
            _logger.LogDebug("{Name} ReadInputRegisters 성공: {Count}개", _settings.Name, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            OnOperationFailed(ex, $"ReadInputRegisters(address={startAddress}, count={count})");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("{Name} ReadCoils: address={Address}, count={Count}", _settings.Name, startAddress, count);
            var result = await Master.ReadCoilsAsync(_settings.SlaveId, startAddress, count);
            _logger.LogDebug("{Name} ReadCoils 성공: {Count}개", _settings.Name, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            OnOperationFailed(ex, $"ReadCoils(address={startAddress}, count={count})");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("{Name} ReadDiscreteInputs: address={Address}, count={Count}", _settings.Name, startAddress, count);
            var result = await Master.ReadInputsAsync(_settings.SlaveId, startAddress, count);
            _logger.LogDebug("{Name} ReadDiscreteInputs 성공: {Count}개", _settings.Name, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            OnOperationFailed(ex, $"ReadDiscreteInputs(address={startAddress}, count={count})");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("{Name} WriteSingleRegister: address={Address}, value={Value}", _settings.Name, address, value);
            await Master.WriteSingleRegisterAsync(_settings.SlaveId, address, value);
        }
        catch (Exception ex)
        {
            OnOperationFailed(ex, $"WriteSingleRegister(address={address}, value={value})");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
             _logger.LogDebug("{Name} WriteMultipleRegisters: address={Address}, count={Count}", _settings.Name, startAddress, values.Length);
            await Master.WriteMultipleRegistersAsync(_settings.SlaveId, startAddress, values);
        }
        catch (Exception ex)
        {
            OnOperationFailed(ex, $"WriteMultipleRegisters(address={startAddress}, count={values.Length})");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken ct = default)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("{Name} WriteSingleCoil: address={Address}, value={Value}", _settings.Name, address, value);
            await Master.WriteSingleCoilAsync(_settings.SlaveId, address, value);
        }
        catch (Exception ex)
        {
            OnOperationFailed(ex, $"WriteSingleCoil(address={address}, value={value})");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected || _master == null)
            throw new InvalidOperationException($"{_settings.Name} Modbus TCP가 연결되지 않았습니다.");
    }

    // 세마포어 획득 전 EnsureConnected 를 통과했어도, 대기 중 다른 연산의 실패 처리로
    // 연결이 끊겼을 수 있다(_master=null) — 획득 후에는 이 접근자로 다시 확인한다.
    private IModbusMaster Master =>
        _master ?? throw new InvalidOperationException($"{_settings.Name} Modbus TCP가 연결되지 않았습니다.");

    /// <summary>
    /// 개별 연산 실패 공통 처리. 상세(스택 포함)는 Debug 로만 남긴다 — 사용자용 요약 경고는
    /// 각 서비스가 끊김당 1회 남기므로 여기서 Warning 을 찍으면 끊김 동안 스택트레이스가 반복된다.
    /// 소켓이 죽은 오류면 즉시 연결을 끊는다: 오류 직후에도 <see cref="TcpClient.Connected"/> 가
    /// 잠시 true 로 남는 반쪽 연결 상태에서 서비스 루프가 재연결을 건너뛰지 않게 한다.
    /// (TimeoutException 은 장비가 느린 것일 수 있어 연결을 유지한다. 호출자는 세마포어를 쥔 상태다.)
    /// </summary>
    private void OnOperationFailed(Exception ex, string op)
    {
        _logger.LogDebug(ex, "{Name} {Op} 실패", _settings.Name, op);
        if (ex is IOException or SocketException or ObjectDisposedException)
            Disconnect();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _master?.Dispose(); } catch { }
        _master = null;
        _tcpClient = null;
        _semaphore.Dispose();
    }
}
