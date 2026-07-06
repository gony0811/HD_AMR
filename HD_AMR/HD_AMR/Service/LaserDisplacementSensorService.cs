using HD_AMR.Communication;
using HD_AMR.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Service;

/// <summary>
/// 레이저 변위 센서(EtherNet/IP) 연결을 유지하는 백그라운드 서비스. <see cref="CameraService"/>·
/// <see cref="AMRService"/> 와 동일한 싱글톤 + 호스티드 패턴이지만, 상시 자동 접속이 아니라
/// 페이지의 [연결]/[해제] 버튼이 토글하는 <c>_enabled</c> 플래그로 게이팅된다. 활성 상태이고
/// 아직 접속 전이면 재접속 루프가 <see cref="LaserDisplacementSensorSettings.ReconnectDelayMs"/> 간격으로
/// 접속을 유지한다. 비활성 상태면 연결하지 않는다.
/// </summary>
public class LaserDisplacementSensorService : BackgroundService
{
    private readonly LaserDisplacementSensorSettings _settings;
    private readonly LaserDisplacementSensorClient _client;
    private readonly ILogger<LaserDisplacementSensorService> _logger;

    private volatile bool _enabled;

    public LaserDisplacementSensorService(
        IOptions<LaserDisplacementSensorSettings> options,
        ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _logger = loggerFactory.CreateLogger<LaserDisplacementSensorService>();
        _client = new LaserDisplacementSensorClient(
            _settings, loggerFactory.CreateLogger<LaserDisplacementSensorClient>());

        // AutoConnect=true 면 기동과 동시에 재접속 루프가 접속을 시작한다.
        _enabled = _settings.AutoConnect;
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>재접속 루프가 활성(접속 유지 대상)인지. 페이지의 상태 표시에 사용.</summary>
    public bool IsEnabled => _enabled;

    public LaserDisplacementSensorSettings Settings => _settings;

    /// <summary>마지막 접속 실패 메시지. 성공 시 null. 페이지에 표시.</summary>
    public string? LastError { get; private set; }

    // 연결 실패 warn을 끊김당 1회만 남기기 위한 플래그(재연결 성공 시 리셋).
    private bool _retryWarned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LaserDisplacementSensorService 시작 (autoConnect={Auto})", _settings.AutoConnect);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_enabled && !_client.IsConnected)
                {
                    await _client.ConnectAsync(stoppingToken);
                    LastError = null;
                    _retryWarned = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                if (!_retryWarned)
                {
                    _logger.LogWarning("레이저 변위 센서 연결 실패, 이후 자동 재시도 — {Err}", ex.Message);
                    _retryWarned = true;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(_settings.ReconnectDelayMs), stoppingToken);
        }
    }

    /// <summary>
    /// [연결] — 재접속 루프를 활성화하고, 버튼 반응성을 위해 즉시 한 번 접속을 시도한다.
    /// 실패하면 <see cref="LastError"/> 에 기록하고 예외를 다시 던진다(페이지에서 표시).
    /// 이후 루프가 계속 재시도한다.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _enabled = true;
        LastError = null;
        try
        {
            await _client.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    /// <summary>[해제] — 재접속 루프를 비활성화하고 세션을 즉시 끊는다.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _enabled = false;
        await _client.DisconnectAsync(ct);
    }

    /// <summary>채널 1..ChannelCount 의 최신 측정 스냅샷. 미연결이면 빈 목록.</summary>
    public IReadOnlyList<LaserChannelReading> GetReadings()
        => _client.IsConnected ? _client.ReadChannels(_settings.ChannelCount) : Array.Empty<LaserChannelReading>();

    /// <summary>최신 Input Assembly 원본 바이트 스냅샷(진단용). 미연결이면 null.</summary>
    public byte[]? SnapshotInputAssembly() => _client.SnapshotInputAssembly();

    /// <summary>
    /// 최신 3채널 측정으로 평면 기울기(cobot base 기준 rx,ry,rz)를 계산한다. 3채널이 모두 존재하고
    /// 유효(Enabled)할 때만 계산하며, 아니면 <see cref="PlaneTilt.Valid"/>=false 로 사유를 담는다.
    /// 헤드 기하·부호는 <see cref="Settings"/> 값을 사용한다.
    /// </summary>
    public PlaneTilt GetPlaneTilt()
    {
        var r = GetReadings();
        if (r.Count < 3)
            return PlaneTilt.Invalid("연결/측정 대기 중 — 3채널이 필요합니다.");
        if (!(r[0].Enabled && r[1].Enabled && r[2].Enabled))
            return PlaneTilt.Invalid("3채널 모두 측정중(범위 내)이어야 기울기를 계산할 수 있습니다.");

        var hx = new[] { _settings.Head1OffsetXmm, _settings.Head2OffsetXmm, _settings.Head3OffsetXmm };
        var hy = new[] { _settings.Head1OffsetYmm, _settings.Head2OffsetYmm, _settings.Head3OffsetYmm };
        var d = new[] { r[0].Value, r[1].Value, r[2].Value };
        return PlaneTiltCalculator.Compute(hx, hy, d, _settings.TiltReadingSignForUp);
    }

    /// <summary>
    /// 최신 3채널 측정으로 삼각형 평면 중심의 <b>툴 좌표계</b> pose(x,y,z,rx,ry,rz)를 계산한다.
    /// 3채널이 모두 존재·유효(Enabled)할 때만 계산하며, 아니면 <see cref="PlanePose.Valid"/>=false.
    /// 헤드 기하·standoff·부호·법선방향은 <see cref="Settings"/> 값을 사용한다.
    /// </summary>
    public PlanePose GetPlanePose()
    {
        var r = GetReadings();
        if (r.Count < 3)
            return PlanePose.Invalid("연결/측정 대기 중 — 3채널이 필요합니다.");
        if (!(r[0].Enabled && r[1].Enabled && r[2].Enabled))
            return PlanePose.Invalid("3채널 모두 측정중(범위 내)이어야 중심 pose를 계산할 수 있습니다.");

        var hx = new[] { _settings.Head1OffsetXmm, _settings.Head2OffsetXmm, _settings.Head3OffsetXmm };
        var hy = new[] { _settings.Head1OffsetYmm, _settings.Head2OffsetYmm, _settings.Head3OffsetYmm };
        var d = new[] { r[0].Value, r[1].Value, r[2].Value };
        return PlaneTiltCalculator.ComputePose(
            hx, hy, d, _settings.TiltStandoffMm, _settings.TiltReadingSignForUp, _settings.TiltNormalTowardSensor);
    }

    /// <summary>채널 영점 설정(현재값을 0으로).</summary>
    public void ZeroSet(int channel) => _client.SetZero(channel, true);

    /// <summary>채널 영점 해제(실제값 복원).</summary>
    public void ZeroReset(int channel) => _client.SetZero(channel, false);

    /// <summary>전체 채널 영점 설정.</summary>
    public void ZeroSetAll()
    {
        for (int ch = 1; ch <= _settings.ChannelCount; ch++) _client.SetZero(ch, true);
    }

    /// <summary>전체 채널 영점 해제.</summary>
    public void ZeroResetAll()
    {
        for (int ch = 1; ch <= _settings.ChannelCount; ch++) _client.SetZero(ch, false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Disconnect();
        _logger.LogInformation("LaserDisplacementSensorService 종료");
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
