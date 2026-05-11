using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NModbus;

namespace HD_AMR.Communication;

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
            var result = await _master!.ReadHoldingRegistersAsync(_settings.SlaveId, startAddress, count);
            _logger.LogDebug("{Name} ReadHoldingRegisters 성공: {Count}개", _settings.Name, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} ReadHoldingRegisters 실패: address={Address}, count={Count}", _settings.Name, startAddress, count);
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
            var result = await _master!.ReadInputRegistersAsync(_settings.SlaveId, startAddress, count);
            _logger.LogDebug("{Name} ReadInputRegisters 성공: {Count}개", _settings.Name, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} ReadInputRegisters 실패: address={Address}, count={Count}", _settings.Name, startAddress, count);
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
            var result = await _master!.ReadCoilsAsync(_settings.SlaveId, startAddress, count);
            _logger.LogDebug("{Name} ReadCoils 성공: {Count}개", _settings.Name, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} ReadCoils 실패: address={Address}, count={Count}", _settings.Name, startAddress, count);
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
            var result = await _master!.ReadInputsAsync(_settings.SlaveId, startAddress, count);
            _logger.LogDebug("{Name} ReadDiscreteInputs 성공: {Count}개", _settings.Name, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} ReadDiscreteInputs 실패: address={Address}, count={Count}", _settings.Name, startAddress, count);
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
            await _master!.WriteSingleRegisterAsync(_settings.SlaveId, address, value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} WriteSingleRegister 실패: address={Address}, value={Value}", _settings.Name, address, value);
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
            await _master!.WriteMultipleRegistersAsync(_settings.SlaveId, startAddress, values);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} WriteMultipleRegisters 실패: address={Address}, count={Count}", _settings.Name, startAddress, values.Length);
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
            await _master!.WriteSingleCoilAsync(_settings.SlaveId, address, value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} WriteSingleCoil 실패: address={Address}, value={Value}", _settings.Name, address, value);
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
