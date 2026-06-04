using HD_AMR.Models;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Communication;

/// <summary>
/// 코봇(FAIRINO) Modbus TCP 클라이언트. 기존 <see cref="ModbusTcpClient"/>를 합성(composition)으로
/// 재사용하고, 그 위에 코봇 전용 명령(코일 토글)·상태 읽기 메서드와 <see cref="CobotRegisterMap"/>을 얹는다.
/// 연결/세마포어/타임아웃 로직은 내부 <see cref="ModbusTcpClient"/>가 담당. NAMUGA_AMR 구조를 이식.
/// </summary>
public class CobotModbusTcpClient : IDisposable
{
    private readonly ModbusTcpClient _client;

    public CobotModbusTcpClient(CobotModbusTcpSettings settings, ILoggerFactory loggerFactory)
    {
        _client = new ModbusTcpClient(settings, loggerFactory.CreateLogger<ModbusTcpClient>());
    }

    public bool IsConnected => _client.IsConnected;
    public string Name => _client.Name;

    // ── 연결 관리 (위임) ───────────────────────────────────────────
    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);
    public Task DisconnectAsync(CancellationToken ct = default) => _client.DisconnectAsync(ct);
    public void Disconnect() => _client.Disconnect();

    // ── 제어 명령 (코일 토글: ON → 200ms → OFF) ───────────────────
    public Task PauseAsync(CancellationToken ct = default) => ToggleCoilAsync(CobotRegisterMap.Coil.Pause, ct);
    public Task RecoveryAsync(CancellationToken ct = default) => ToggleCoilAsync(CobotRegisterMap.Coil.Recovery, ct);
    public Task StartAsync(CancellationToken ct = default) => ToggleCoilAsync(CobotRegisterMap.Coil.Start, ct);
    public Task StopAsync(CancellationToken ct = default) => ToggleCoilAsync(CobotRegisterMap.Coil.Stop, ct);
    public Task MoveToJobOriginAsync(CancellationToken ct = default) => ToggleCoilAsync(CobotRegisterMap.Coil.MoveToJobOrigin, ct);
    public Task ManualAutoSwitchAsync(CancellationToken ct = default) => ToggleCoilAsync(CobotRegisterMap.Coil.ManualAutoSwitch, ct);
    public Task StartMainProgramAsync(CancellationToken ct = default) => ToggleCoilAsync(CobotRegisterMap.Coil.StartMainProgram, ct);
    public Task ClearAllFaultsAsync(CancellationToken ct = default) => ToggleCoilAsync(CobotRegisterMap.Coil.ClearAllFaults, ct);

    /// <summary>DI 비트 개별 쓰기 (index: 0~127).</summary>
    public Task WriteDigitalInputAsync(ushort index, bool value, CancellationToken ct = default)
    {
        if (index > 127)
            throw new ArgumentOutOfRangeException(nameof(index), "DI 인덱스는 0~127 범위여야 합니다.");

        var address = (ushort)(CobotRegisterMap.Coil.DigitalInputStart + index);
        return _client.WriteSingleCoilAsync(address, value, ct);
    }

    /// <summary>AI 워드 개별 쓰기 (index: 0~31).</summary>
    public Task WriteAnalogInputAsync(ushort index, ushort value, CancellationToken ct = default)
    {
        if (index > 31)
            throw new ArgumentOutOfRangeException(nameof(index), "AI 인덱스는 0~31 범위여야 합니다.");

        var address = (ushort)(CobotRegisterMap.Holding.AnalogInputStart + index);
        return _client.WriteSingleRegisterAsync(address, value, ct);
    }

    // ── 상태 읽기 ──────────────────────────────────────────────────
    /// <summary>코봇 상태 읽기 (Input Register 310~322).</summary>
    public async Task<CobotStatus> ReadCobotStatusAsync(CancellationToken ct = default)
    {
        var r = await _client.ReadInputRegistersAsync(
            CobotRegisterMap.Input.StatusStart, CobotRegisterMap.Input.StatusCount, ct);

        return new CobotStatus
        {
            EnableState = r[0],
            RobotMode = r[1],
            OperationStatus = r[2],
            ToolNo = r[3],
            JobNumber = r[4],
            ScrumState = r[5],
            RobotStatusFault = r[6],
            MasterFaultCode = r[7],
            SubFaultCode = r[8],
            CollisionDetection = r[9],
            MotionInPlace = r[10],
            SafetyStopS0 = r[11],
            SafetyStopS1 = r[12],
            UpdatedAt = DateTime.UtcNow,
        };
    }

    // ── 진단용 raw 읽기 (위임) ─────────────────────────────────────
    public Task<bool[]> ReadRawCoilsAsync(ushort start, ushort count, CancellationToken ct = default)
        => _client.ReadCoilsAsync(start, count, ct);
    public Task<bool[]> ReadRawDiscreteInputsAsync(ushort start, ushort count, CancellationToken ct = default)
        => _client.ReadDiscreteInputsAsync(start, count, ct);
    public Task<ushort[]> ReadRawInputRegistersAsync(ushort start, ushort count, CancellationToken ct = default)
        => _client.ReadInputRegistersAsync(start, count, ct);
    public Task<ushort[]> ReadRawHoldingRegistersAsync(ushort start, ushort count, CancellationToken ct = default)
        => _client.ReadHoldingRegistersAsync(start, count, ct);

    private async Task ToggleCoilAsync(ushort address, CancellationToken ct)
    {
        await _client.WriteSingleCoilAsync(address, true, ct);
        await Task.Delay(200, ct);
        await _client.WriteSingleCoilAsync(address, false, ct);
    }

    public void Dispose() => _client.Dispose();
}
