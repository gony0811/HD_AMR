using HD_AMR.Communication;
using HD_AMR.Enums;
using HD_AMR.Models;
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

    /// <summary>백그라운드 폴링으로 캐싱된 최신 로봇 상태 (미연결/읽기 실패 시 null)</summary>
    public RobotStatus? LatestStatus { get; private set; }

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

                LatestStatus = await ReadStatusAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LatestStatus = null;
                _logger.LogWarning(ex, "AMR Modbus TCP 연결/상태 읽기 실패 — 5초 후 재시도");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
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

    #region AMR 상태 읽기 (Input Register)

    /// <summary>전체 로봇 상태 읽기 (Input Register 0~64 벌크)</summary>
    public async Task<RobotStatus> ReadStatusAsync(CancellationToken ct = default)
    {
        var r = await _client.ReadInputRegistersAsync(0, 65, ct);

        return new RobotStatus
        {
            PowerState = (PowerState)r[AmrRegisterMap.Input.PowerStatus],
            RobotState = (RobotState)r[AmrRegisterMap.Input.RobotStatus],
            ErrorCode = r[AmrRegisterMap.Input.RobotError],
            RobotStopActive = r[AmrRegisterMap.Input.RobotStop],
            WiFi = (WiFiState)r[AmrRegisterMap.Input.WiFi],
            WorkStatus = (WorkStatus)r[AmrRegisterMap.Input.WorkStatus],
            Pose = new RobotPose(
                RegistersToFloat(r[AmrRegisterMap.Input.PoseX], r[AmrRegisterMap.Input.PoseX + 1]),
                RegistersToFloat(r[AmrRegisterMap.Input.PoseY], r[AmrRegisterMap.Input.PoseY + 1]),
                RegistersToFloat(r[AmrRegisterMap.Input.PoseAngle], r[AmrRegisterMap.Input.PoseAngle + 1])
            ),
            MapStatusPercent = r[AmrRegisterMap.Input.MapStatus] / 10000f * 100f,
            DrivingMode = (DrivingMode)r[AmrRegisterMap.Input.DrivingMode],
            Battery = new BatteryStatus
            {
                LevelPercent = r[AmrRegisterMap.Input.BatteryLevel] / 10000f * 100f,
                Voltage = r[AmrRegisterMap.Input.BatteryVoltage] / 100f,
                Current = r[AmrRegisterMap.Input.BatteryCurrent] / 100f,
                TemperatureCelsius = r[AmrRegisterMap.Input.BatteryTemp] / 100f,
                ChargingState = (ChargingState)r[AmrRegisterMap.Input.ChargingState]
            },
            TaskProgress = new TaskProgress
            {
                TotalTaskCount = r[AmrRegisterMap.Input.TotalTaskCount],
                CurrentTaskNumber = r[AmrRegisterMap.Input.CurrentTaskNumber],
                TotalJobCount = r[AmrRegisterMap.Input.TotalJobCount],
                CurrentJobNumber = r[AmrRegisterMap.Input.CurrentJobNumber]
            }
        };
    }

    #endregion

    #region AMR 제어 쓰기 (Holding Register)

    /// <summary>전원 제어</summary>
    public Task SetPowerAsync(PowerCommand command, CancellationToken ct = default)
        => _client.WriteSingleRegisterAsync(AmrRegisterMap.Holding.Power, (ushort)command, ct);

    /// <summary>주행 모드 설정 — 드라이브(1), 카트(2)</summary>
    public Task SetDrivingModeAsync(DrivingMode mode, CancellationToken ct = default)
        => _client.WriteSingleRegisterAsync(AmrRegisterMap.Holding.DrivingMode, (ushort)mode, ct);

    /// <summary>상태 제어 — 정지(1), 시작(2), 일시정지(3)</summary>
    public Task SetExecutionControlAsync(ExecutionControl control, CancellationToken ct = default)
        => _client.WriteSingleRegisterAsync(AmrRegisterMap.Holding.ExecutionControl, (ushort)control, ct);

    /// <summary>로봇 주행 정지 — 활성화(1), 비활성화(2)</summary>
    public Task SetRobotStopAsync(ushort value, CancellationToken ct = default)
        => _client.WriteSingleRegisterAsync(AmrRegisterMap.Holding.RobotStop, value, ct);

    /// <summary>Error Reset — 활성화(1)</summary>
    public Task AirInitializeAsync(CancellationToken ct = default)
        => _client.WriteSingleRegisterAsync(AmrRegisterMap.Holding.AirInitialize, 1, ct);

    /// <summary>Task Index 설정</summary>
    public Task SetTaskIndexAsync(ushort index, CancellationToken ct = default)
        => _client.WriteSingleRegisterAsync(AmrRegisterMap.Holding.TaskIndex, index, ct);

    /// <summary>Job Index 설정</summary>
    public Task SetJobIndexAsync(ushort index, CancellationToken ct = default)
        => _client.WriteSingleRegisterAsync(AmrRegisterMap.Holding.JobIndex, index, ct);

    /// <summary>로봇 포즈 탐색 활성화</summary>
    public Task SetPoseSearchAsync(ushort value, CancellationToken ct = default)
        => _client.WriteSingleRegisterAsync(AmrRegisterMap.Holding.PoseSearch, value, ct);

    /// <summary>포즈 탐색 좌표 설정 (X, Y: meters, Angle: radian)</summary>
    public Task SetPoseTargetAsync(float x, float y, float angle, CancellationToken ct = default)
    {
        var registers = new ushort[6];
        FloatToRegisters(x).CopyTo(registers, 0);
        FloatToRegisters(y).CopyTo(registers, 2);
        FloatToRegisters(angle).CopyTo(registers, 4);
        return _client.WriteMultipleRegistersAsync(AmrRegisterMap.Holding.PoseTargetX, registers, ct);
    }

    #endregion

    /// <summary>2개의 UInt16 레지스터를 Float32로 변환 (Little-Endian word order: 첫 번째=Lo, 두 번째=Hi)</summary>
    private static float RegistersToFloat(ushort first, ushort second)
    {
        var combined = ((uint)second << 16) | first;
        return BitConverter.Int32BitsToSingle((int)combined);
    }

    /// <summary>Float32를 2개의 UInt16 레지스터로 변환 (Little-Endian word order: [0]=Lo, [1]=Hi)</summary>
    private static ushort[] FloatToRegisters(float value)
    {
        var bits = (uint)BitConverter.SingleToInt32Bits(value);
        return new[]
        {
            (ushort)(bits & 0xFFFF),
            (ushort)(bits >> 16)
        };
    }
}
