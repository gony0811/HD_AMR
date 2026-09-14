using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// 레이저 변위 센서(EtherNet/IP) 연결을 유지하는 백그라운드 서비스. <see cref="CameraService"/>·
/// <see cref="AMRService"/> 와 동일하게 호스트 기동과 동시에 상시 자동 접속하며, 연결이 끊기면
/// <see cref="LaserDisplacementSensorSettings.ReconnectDelayMs"/> 간격으로 자동 재접속한다.
/// </summary>
public class LaserDisplacementSensorService : BackgroundService
{
    /// <summary>
    /// 평행 기준(센서 장착 틸트 바이어스, 도). 툴이 물리적으로 평행한 자세에서 캡처한 원시 Rx/Ry.
    /// null = 미설정(보정 없음).
    /// </summary>
    public sealed record TiltReference(double RxDeg, double RyDeg, DateTime CapturedAtUtc);

    private const string RefRxKey = "Laser.TiltRef.RxDeg";
    private const string RefRyKey = "Laser.TiltRef.RyDeg";
    private const string RefAtKey = "Laser.TiltRef.CapturedAt";

    private readonly LaserDisplacementSensorSettings _settings;
    private readonly LaserDisplacementSensorClient _client;
    private readonly ILogger<LaserDisplacementSensorService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // 참조 대입은 원자적이므로 폴링 루프(읽기)/UI(쓰기) 간 별도 락 불필요.
    private TiltReference? _tiltRef;

    public LaserDisplacementSensorService(
        IOptions<LaserDisplacementSensorSettings> options,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory)
    {
        _settings = options.Value;
        _logger = loggerFactory.CreateLogger<LaserDisplacementSensorService>();
        _scopeFactory = scopeFactory;
        _client = new LaserDisplacementSensorClient(
            _settings, loggerFactory.CreateLogger<LaserDisplacementSensorClient>());
    }

    public bool IsConnected => _client.IsConnected;

    public LaserDisplacementSensorSettings Settings => _settings;

    /// <summary>마지막 접속 실패 메시지. 성공 시 null. 페이지에 표시.</summary>
    public string? LastError { get; private set; }

    // 연결 실패 warn을 끊김당 1회만 남기기 위한 플래그(재연결 성공 시 리셋).
    private bool _retryWarned;

    /// <summary>저장된 평행 기준. UI 배지/진단 표시용.</summary>
    public TiltReference? TiltRef => _tiltRef;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LaserDisplacementSensorService 시작 (상시 자동 접속)");

        await LoadTiltReferenceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
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

    /// <summary>채널 1..ChannelCount 의 최신 측정 스냅샷. 미연결이면 빈 목록.</summary>
    public IReadOnlyList<LaserChannelReading> GetReadings()
        => _client.IsConnected ? _client.ReadChannels(_settings.ChannelCount) : Array.Empty<LaserChannelReading>();

    /// <summary>최신 Input Assembly 원본 바이트 스냅샷(진단용). 미연결이면 null.</summary>
    public byte[]? SnapshotInputAssembly() => _client.SnapshotInputAssembly();

    /// <summary>
    /// 최신 3채널 측정으로 삼각형 평면 중심의 <b>툴 좌표계</b> pose(x,y,z,rx,ry,rz)를 계산한다.
    /// 3채널이 모두 존재·유효(Enabled)할 때만 계산하며, 아니면 <see cref="PlanePose.Valid"/>=false.
    /// 헤드 기하·standoff·부호·법선방향은 <see cref="Settings"/> 값을 사용한다.
    /// 평행 기준 보정이 없는 <b>원시</b> pose — 기준 캡처·진단용.
    /// </summary>
    public PlanePose GetRawPlanePose()
    {
        var r = GetReadings();
        if (r.Count < 3)
            return PlanePose.Invalid("연결/측정 대기 중 — 3채널이 필요합니다.");
        if (!(r[0].Enabled && r[1].Enabled && r[2].Enabled))
            return PlanePose.Invalid("3채널 모두 측정중(범위 내)이어야 중심 pose를 계산할 수 있습니다.");

        var hx = new[] { _settings.Head1OffsetXmm, _settings.Head2OffsetXmm, _settings.Head3OffsetXmm };
        var hy = new[] { _settings.Head1OffsetYmm, _settings.Head2OffsetYmm, _settings.Head3OffsetYmm };
        var d = new[] { r[0].Value, r[1].Value, r[2].Value };
        return PlanePoseCalculator.ComputePose(
            hx, hy, d, _settings.TiltStandoffMm, _settings.TiltReadingSignForUp);
    }

    /// <summary>
    /// <see cref="GetRawPlanePose"/> 에 평행 기준(<see cref="TiltRef"/>)이 있으면 Rx/Ry 에서
    /// 바이어스를 빼서 반환한다. 위치 X/Y/Z(절대 거리)는 불변 — working distance 로직 안전.
    /// </summary>
    public PlanePose GetPlanePose()
    {
        var p = GetRawPlanePose();
        var r = _tiltRef;
        if (!p.Valid || r is null) return p;

        double rx = p.Rx - r.RxDeg;
        double ry = p.Ry - r.RyDeg;
        // 보정 각으로부터 법선 재구성 — PlanePoseCalculator 분해(Rx=atan2(ny,nz), Ry=atan2(nx,nz))의 역.
        double nx = Math.Tan(ry * Math.PI / 180.0);
        double ny = Math.Tan(rx * Math.PI / 180.0);
        double m = Math.Sqrt(nx * nx + ny * ny + 1.0);
        return p with { Rx = rx, Ry = ry, Normal = new[] { nx / m, ny / m, 1.0 / m } };
    }

    /// <summary>
    /// 현재(물리적으로 평행하게 맞춘) 자세의 원시 틸트 평균을 평행 기준으로 저장한다.
    /// 헤드 지오메트리(Head*Offset*) 를 재캘리브레이션한 뒤에는 기준도 재캡처해야 한다.
    /// </summary>
    /// <returns>Ok=성공 여부, Message=결과 메시지, Warning=성공했지만 주의가 필요한 경우 경고문.</returns>
    public async Task<(bool Ok, string Message, string? Warning)> CaptureTiltReferenceAsync(
        int samples = 5, CancellationToken ct = default)
    {
        double sumRx = 0, sumRy = 0;
        int valid = 0;
        string? lastNote = null;

        for (int i = 0; i < samples; i++)
        {
            if (i > 0) await Task.Delay(100, ct);
            var p = GetRawPlanePose();
            if (p.Valid) { sumRx += p.Rx; sumRy += p.Ry; valid++; }
            else lastNote = p.Note;
        }

        if (valid < 3)
            return (false, $"유효 샘플 부족({valid}/{samples}) — {lastNote ?? "센서 상태를 확인하세요."}", null);

        double rx = sumRx / valid;
        double ry = sumRy / valid;
        double mag = Math.Max(Math.Abs(rx), Math.Abs(ry));

        if (mag >= 10.0)
            return (false, $"틸트가 너무 큽니다(Rx={rx:F2}°, Ry={ry:F2}°) — 센서 장착/헤드 지오메트리를 먼저 점검하세요.", null);

        var refAt = DateTime.UtcNow;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await param.SetDoubleAsync(RefRxKey, rx, "레이저 평행 기준 — 센서 장착 틸트 바이어스 Rx(도)");
            await param.SetDoubleAsync(RefRyKey, ry, "레이저 평행 기준 — 센서 장착 틸트 바이어스 Ry(도)");
            await param.SetAsync(RefAtKey, refAt.ToString("o"), "레이저 평행 기준 저장 시각(UTC)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "평행 기준 저장 실패");
            return (false, $"저장 실패: {ex.Message}", null);
        }

        _tiltRef = new TiltReference(rx, ry, refAt);
        _logger.LogInformation("평행 기준 저장: Rx={Rx:F3}°, Ry={Ry:F3}° (샘플 {Valid}/{Samples})", rx, ry, valid, samples);

        string? warning = mag >= 3.0
            ? "장착 틸트가 큽니다 — 센서 기구 장착 상태 확인 권장"
            : null;
        return (true, $"평행 기준 저장됨: Rx={rx:F3}°, Ry={ry:F3}°", warning);
    }

    /// <summary>평행 기준을 해제한다(보정 없는 원시 틸트로 복귀).</summary>
    public async Task ClearTiltReferenceAsync(CancellationToken ct = default)
    {
        _tiltRef = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            // 빈 문자열은 GetDoubleAsync 파싱 실패 → 로드 시 미설정으로 처리된다.
            await param.SetAsync(RefRxKey, "");
            await param.SetAsync(RefRyKey, "");
            await param.SetAsync(RefAtKey, "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "평행 기준 해제 저장 실패");
        }
        _logger.LogInformation("평행 기준 해제");
    }

    private async Task LoadTiltReferenceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            var rx = await param.GetDoubleAsync(RefRxKey);
            var ry = await param.GetDoubleAsync(RefRyKey);
            if (rx is null || ry is null) return;

            var at = DateTime.TryParse(
                await param.GetAsync(RefAtKey), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : DateTime.UtcNow;
            _tiltRef = new TiltReference(rx.Value, ry.Value, at);
            _logger.LogInformation("평행 기준 로드: Rx={Rx:F3}°, Ry={Ry:F3}°", rx.Value, ry.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("평행 기준 로드 실패(보정 없이 계속) — {Err}", ex.Message);
        }
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
