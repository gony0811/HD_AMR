using System.Globalization;
using HD.AMR.App.Communication.GasDetector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// 헬륨 가스 디텍터(INFICON XL3000flex) 서비스. 비전 인터페이스 대신 쓰는 검사 센서로,
/// AMR/Cobot/Telescopic 과 동일하게 싱글톤 + 호스티드로 등록되어 기동 시 자동 연결하고 실패 시 재시도한다.
///
/// · <see cref="InspectionSensorSettings.Mode"/> 가 <c>GasDetector</c> 일 때만 포트를 연다(비전 AMR 에서는 휴면).
/// · 통신 경로는 시리얼(RS485)/이더넷(TCP) 중 선택 — <see cref="ApplySettingsAsync"/> 로 실행 중 바꾸면
///   DB 파라미터(<c>GasDetector.*</c>)에 저장하고 즉시 다시 연결한다.
/// · 리크레이트·상태를 주기 폴링해 <see cref="Latest"/> 로 캐싱한다(화면 실시간 표시).
/// · 검사 시퀀스는 경유점마다 <see cref="MeasurePointAsync"/> 로 측정·판정(임계값 초과 NG, NG 면 최대 2회 재측정)하고
///   측정 1건(scan)씩 <see cref="Results"/> 에 쌓인다. 임계값은 SAIGE → ACS 로 받는다(<see cref="SetThresholdFromAcsAsync"/>).
/// </summary>
public sealed class GasDetectorService : BackgroundService
{
    private const int MaxResults = 1000;
    private const string KeyPrefix = "GasDetector.";

    private readonly InspectionSensorSettings _sensor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GasDetectorService> _logger;

    private readonly object _lock = new();
    private GasDetectorSettings _settings;
    private Xl3000FlexClient? _client;
    private GasDetectorReading? _latest;
    private DateTime? _lastOkUtc;
    private readonly List<GasMeasurementResult> _results = new();
    private int _resultSeq;
    private CancellationTokenSource _wakeCts = new();
    private volatile bool _reconnectRequested;
    private int _consecutiveTimeouts;
    private bool _retryWarned;
    // 장비 응답 확인 여부 — 시리얼은 장비가 없어도 포트가 열리므로 "연결/끊김" 로그는 응답 기준으로 끊김당 1회만 남긴다.
    private bool _linkUp;
    private bool _unitUnknownWarned;
    private GasThreshold _threshold;

    public GasDetectorService(IOptions<GasDetectorSettings> options, IOptions<InspectionSensorSettings> sensor,
        IServiceScopeFactory scopeFactory, ILogger<GasDetectorService> logger)
    {
        _settings = options.Value.Clone();
        _threshold = LocalThreshold(_settings);
        _sensor = sensor.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>검사 센서 모드가 가스 디텍터인지 — 아니면 서비스는 아무 것도 열지 않는다.</summary>
    public bool IsActive => _sensor.UsesGasDetector;

    /// <summary>현재 적용 중인 설정(복사본).</summary>
    public GasDetectorSettings Settings { get { lock (_lock) return _settings.Clone(); } }

    /// <summary>포트/소켓이 열려 있는지.</summary>
    public bool IsPortOpen => _client?.IsOpen == true;

    /// <summary>열려 있고 최근에 정상 응답이 있었는지 — 시리얼은 장비가 없어도 포트가 열리므로 응답으로 판단한다.</summary>
    public bool IsConnected => IsPortOpen && LastOkUtc is { } t && (DateTime.UtcNow - t) < ResponsiveWindow;

    private TimeSpan ResponsiveWindow
    {
        get
        {
            var s = Settings;
            return TimeSpan.FromMilliseconds(Math.Max(5000, s.PollIntervalMs * 3 + s.ResponseTimeoutMs * 2));
        }
    }

    /// <summary>마지막 정상 응답 시각(UTC) — 장치 HEAVY 알람 감시용.</summary>
    public DateTime? LastOkUtc { get { lock (_lock) return _lastOkUtc; } }

    public string? LastError { get; private set; }
    public string? LastTx => _client?.LastTx;
    public string? LastRx => _client?.LastRx;

    public GasDetectorReading? Latest { get { lock (_lock) return _latest; } }

    /// <summary>측정 결과(오래된 것부터).</summary>
    public IReadOnlyList<GasMeasurementResult> Results { get { lock (_lock) return _results.ToList(); } }

    /// <summary>측정 결과가 추가될 때(호출 스레드에서 발생).</summary>
    public event Action<GasMeasurementResult>? ResultAdded;

    private static GasThreshold LocalThreshold(GasDetectorSettings s)
        => new(s.ThresholdValue, s.ThresholdUnit, GasThresholdSource.Local, DateTime.UtcNow);

    public static string[] AvailablePorts() => SerialGasDetectorTransport.AvailablePorts();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsActive)
        {
            LastError = "검사 센서 모드가 Vision 입니다 (InspectionSensor:Mode) — 가스 디텍터를 사용하지 않습니다.";
            _logger.LogInformation("GasDetectorService 휴면 — 검사 센서 모드 {Mode}", _sensor.Mode);
            return;
        }

        // host.Start() 는 첫 await 까지 동기 실행 — DB 조회·포트 열기가 기동 스레드를 붙잡지 않게 먼저 양보한다.
        await Task.Yield();
        await LoadPersistedAsync();
        _logger.LogInformation("GasDetectorService 시작 ({Model}, {Endpoint})", Settings.Model, Settings.EndpointText);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_reconnectRequested)
            {
                _reconnectRequested = false;
                CloseClient();
            }

            try
            {
                if (_client is not { IsOpen: true })
                {
                    CloseClient();
                    var s = Settings;
                    var client = new Xl3000FlexClient(GasDetectorTransportFactory.Create(s), s.ResponseTimeoutMs);
                    await client.OpenAsync(stoppingToken);
                    _client = client;
                    _consecutiveTimeouts = 0;
                    LastError = null;
                    _logger.LogDebug("가스 디텍터 포트 열림 ({Endpoint})", s.EndpointText);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = $"연결 실패: {ex.Message}";
                if (!_retryWarned)
                {
                    _logger.LogWarning("가스 디텍터 연결 실패 ({Endpoint}), 이후 자동 재시도 — {Err}", Settings.EndpointText, ex.Message);
                    _retryWarned = true;
                }
                CloseClient();
                await DelayAsync(Settings.ReconnectDelayMs, stoppingToken);
                continue;
            }

            await PollAsync(stoppingToken);
            await DelayAsync(Math.Max(Xl3000AsciiProtocol.MinSampleIntervalMs, Settings.PollIntervalMs), stoppingToken);
        }

        CloseClient();
    }

    private int _pollCount;

    private async Task PollAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null) return;
        try
        {
            var prev = Latest;
            var leak = await client.ReadLeakRateAsync(ct);
            var leakMbar = await client.ReadLeakRateMbarLsAsync(ct);
            var modeRaw = await client.QueryAsync(Xl3000AsciiProtocol.ReadStatus, ct);
            var triggers = Xl3000AsciiProtocol.ParseTriggers(await client.QueryAsync(Xl3000AsciiProtocol.ReadTrigger, ct));

            // 단위·에러 상태는 자주 바뀌지 않으므로 10회에 1번만 읽는다.
            string? unit = prev?.Unit, error = prev?.ErrorStatus;
            if (_pollCount++ % 10 == 0)
            {
                unit = await TryQueryAsync(client, Xl3000AsciiProtocol.ReadUnitSniff, ct) is { } u
                    ? Xl3000AsciiProtocol.UnitText(u) : unit;
                error = await TryQueryAsync(client, Xl3000AsciiProtocol.ReadError, ct) ?? error;
            }

            var reading = new GasDetectorReading(leak, leakMbar, unit, Xl3000AsciiProtocol.ParseMode(modeRaw), modeRaw,
                triggers, error, DateTime.UtcNow);
            lock (_lock)
            {
                _latest = reading;
                _lastOkUtc = reading.ReadAtUtc;
            }
            _consecutiveTimeouts = 0;
            LastError = null;
            if (!_linkUp)
            {
                _linkUp = true;
                _retryWarned = false;
                _logger.LogInformation("가스 디텍터 응답 확인 — 연결 ({Endpoint})", Settings.EndpointText);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (TimeoutException ex)
        {
            LastError = ex.Message;
            if (++_consecutiveTimeouts >= Math.Max(1, Settings.MaxConsecutiveTimeouts))
            {
                if (_linkUp || !_retryWarned)
                    _logger.LogWarning("가스 디텍터 연속 무응답 {N}회 — 다시 연결합니다 ({Endpoint}). 이후 응답이 올 때까지 경고 생략",
                        _consecutiveTimeouts, Settings.EndpointText);
                _linkUp = false;
                _retryWarned = true;
                _consecutiveTimeouts = 0;
                _pollCount = 0;
                CloseClient();
            }
        }
        catch (GasDetectorException ex)
        {
            // 장비가 응답은 했다 — 링크는 살아 있으므로 연결을 끊지 않는다.
            LastError = ex.Message;
            lock (_lock) _lastOkUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (_linkUp || !_retryWarned)
                _logger.LogWarning("가스 디텍터 폴링 실패 — 다시 연결합니다: {Err}", ex.Message);
            _linkUp = false;
            _retryWarned = true;
            _pollCount = 0;
            CloseClient();
        }
    }

    private static async Task<string?> TryQueryAsync(Xl3000FlexClient client, string command, CancellationToken ct)
    {
        try { return await client.QueryAsync(command, ct); }
        catch (GasDetectorException) { return null; }   // 펌웨어·모드에 따라 지원하지 않는 조회 — 표시만 생략.
    }

    /// <summary>현재 판정 임계값(값·단위·출처). 검사 Task 는 시작할 때 이 값을 잡아 끝까지 쓴다.</summary>
    public GasThreshold CurrentThreshold { get { lock (_lock) return _threshold; } }

    /// <summary>임계값이 바뀔 때(호출 스레드에서 발생).</summary>
    public event Action<GasThreshold>? ThresholdChanged;

    /// <summary>
    /// SAIGE → ACS → AMR 임계값 적용(<c>setGasThreshold</c> instantAction, SAIGE 규격 §3).
    /// 값은 양수여야 하고, 단위는 장비 인터페이스 단위와 같아야 한다(장비 단위를 아직 모르면 받아 두고 측정할 때 확인).
    /// 적용하면 DB 에도 저장해 재시작 후에도 마지막 값을 쓴다. 실패 사유를 돌려준다(null = 적용).
    /// </summary>
    public async Task<string?> SetThresholdFromAcsAsync(string? tankId, double value, string? unit,
        string? requestedBy, long? requestedAtMs)
    {
        if (!IsActive)
            return "검사 센서 모드가 가스 디텍터가 아닙니다";
        if (!double.IsFinite(value) || value <= 0)
            return $"임계값은 양수여야 합니다 (수신 {value})";
        if (string.IsNullOrWhiteSpace(unit))
            return "임계값 단위가 없습니다";
        if (Latest?.Unit is { } deviceUnit && !Xl3000AsciiProtocol.SameUnit(deviceUnit, unit))
            return $"임계값 단위 {unit} ≠ 장비 단위 {deviceUnit} — 장비 인터페이스 단위(*CONFig:UNIT:LRSniff)를 맞추세요";

        var th = new GasThreshold(value, unit.Trim(), GasThresholdSource.Acs, DateTime.UtcNow, tankId, requestedBy, requestedAtMs);
        GasDetectorSettings next;
        lock (_lock)
        {
            _threshold = th;
            _settings.ThresholdValue = th.Value;
            _settings.ThresholdUnit = th.Unit;
            next = _settings.Clone();
        }
        await SavePersistedAsync(next);
        _logger.LogInformation("[가스 임계값] SAIGE 임계값 적용 — {Th}, tank={Tank}, 요청자={By}, 요청시각={At}",
            th.Describe(), tankId ?? "—", requestedBy ?? "—", requestedAtMs?.ToString(CultureInfo.InvariantCulture) ?? "—");
        ThresholdChanged?.Invoke(th);
        return null;
    }

    /// <summary>SAIGE 상태 전송(§4) <c>sensor</c> 블록용 상태 — 최근 폴링값 기준. 가스 모드가 아니면 null.</summary>
    public GasSensorStatus? GetSensorStatus()
    {
        if (!IsActive) return null;
        var r = Latest;
        if (r is null || !IsConnected)
            return new GasSensorStatus("DISCONNECTED", null, null);
        return new GasSensorStatus(
            Xl3000AsciiProtocol.SensorStateCode(r.Mode),
            Xl3000AsciiProtocol.ParseErrorCode(r.ErrorStatus),
            // 교정 완료 여부를 직접 주는 ASCII 조회는 확인되지 않았다 — 장비 확인 후 채운다(SAIGE 규격상 선택 항목).
            null);
    }

    /// <summary>
    /// 측정 1건(scan) — <see cref="GasDetectorSettings.MeasureDwellMs"/> 대기 후 장비 설정 단위의 리크레이트
    /// (<c>*READ?</c>)를 N회 읽어 최대값을 임계값과 비교한다(초과 NG). 장비 단위가 임계값 단위와 다르면 판정하지 않고
    /// Error. 통신 실패도 예외 대신 <see cref="GasJudgement.Error"/> 결과로 돌려준다.
    /// </summary>
    /// <param name="threshold">판정 임계값. null 이면 <see cref="CurrentThreshold"/>.</param>
    /// <param name="retry">0 = 최초 측정, 1·2 = 재측정(SAIGE <c>scans[].retry</c>).</param>
    public async Task<GasMeasurementResult> MeasureAsync(string label, GasThreshold? threshold = null, int retry = 0,
        CancellationToken ct = default)
    {
        var s = Settings;
        var th = threshold ?? CurrentThreshold;
        var started = DateTime.UtcNow;
        var samples = new List<double>();
        string? error = null;

        if (_client is not { IsOpen: true } client)
        {
            error = $"가스 디텍터 미연결 ({s.EndpointText})";
        }
        else if (await CheckUnitAsync(client, th, ct) is { } unitError)
        {
            error = unitError;
        }
        else
        {
            if (s.MeasureDwellMs > 0)
                await Task.Delay(s.MeasureDwellMs, ct);
            var count = Math.Max(1, s.MeasureSampleCount);
            var interval = Math.Max(Xl3000AsciiProtocol.MinSampleIntervalMs, s.MeasureSampleIntervalMs);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    samples.Add(await client.ReadLeakRateAsync(ct));
                    lock (_lock) _lastOkUtc = DateTime.UtcNow;
                }
                // InvalidOperation/ObjectDisposed — 측정 중 재연결로 포트가 닫힌 경우.
                catch (Exception ex) when (ex is TimeoutException or GasDetectorException or IOException
                                               or InvalidOperationException or ObjectDisposedException)
                {
                    error = ex.Message;
                }
                if (i < count - 1)
                    await Task.Delay(interval, ct);
            }
        }

        var judgement = GasMeasurementResult.Judge(samples, th.Value);
        GasMeasurementResult result;
        lock (_lock)
        {
            result = new GasMeasurementResult(++_resultSeq, label, retry, started, DateTime.UtcNow, samples.Count,
                samples.Count > 0 ? samples.Max() : null, samples.Count > 0 ? samples.Average() : null,
                th.Unit, th.Value, judgement, judgement == GasJudgement.Error ? error ?? "유효 샘플 없음" : null);
            _results.Add(result);
            if (_results.Count > MaxResults)
                _results.RemoveRange(0, _results.Count - MaxResults);
        }

        var tag = retry > 0 ? $" (재측정 {retry})" : "";
        if (judgement == GasJudgement.Error)
            _logger.LogWarning("[가스 측정] {Label}{Tag} 측정 실패: {Err}", label, tag, result.Error);
        else
            _logger.LogInformation("[가스 측정] {Label}{Tag} {Judge} — max {Max:0.###} / avg {Avg:0.###} {Unit} (임계 {Limit:0.###}, {N}샘플)",
                label, tag, judgement, result.Value, result.Avg, th.Unit, th.Value, samples.Count);

        ResultAdded?.Invoke(result);
        return result;
    }

    /// <summary>
    /// 경유점 1곳 측정 — 최초 측정 후 OK 가 아니면(NG·실패) 같은 자리에서
    /// <see cref="GasDetectorSettings.MaxRetries"/> 회(0~2)까지 다시 잰다(SAIGE 규격 §2 retry).
    /// </summary>
    public async Task<IReadOnlyList<GasMeasurementResult>> MeasurePointAsync(string label, GasThreshold threshold,
        CancellationToken ct = default)
    {
        var maxRetries = Math.Clamp(Settings.MaxRetries, 0, 2);
        var scans = new List<GasMeasurementResult> { await MeasureAsync(label, threshold, 0, ct) };
        while (GasPointResult.NeedsRetry(scans[^1].Judgement, scans.Count - 1, maxRetries))
            scans.Add(await MeasureAsync(label, threshold, scans.Count, ct));
        return scans;
    }

    /// <summary>
    /// 장비 인터페이스 단위가 임계값 단위와 같은지 확인. 다르면 실패 사유를 돌려준다.
    /// 단위 조회를 지원하지 않는 펌웨어면(Exx) 확인을 건너뛴다 — 경고만 한 번 남긴다.
    /// </summary>
    private async Task<string?> CheckUnitAsync(Xl3000FlexClient client, GasThreshold th, CancellationToken ct)
    {
        string? unit;
        try
        {
            unit = await TryQueryAsync(client, Xl3000AsciiProtocol.ReadUnitSniff, ct) is { } u
                ? Xl3000AsciiProtocol.UnitText(u) : Latest?.Unit;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            return ex.Message;
        }
        if (unit is null)
        {
            if (!_unitUnknownWarned)
            {
                _logger.LogWarning("가스 디텍터 단위를 확인할 수 없습니다 — 장비 설정 단위가 {Unit} 라고 보고 판정합니다", th.Unit);
                _unitUnknownWarned = true;
            }
            return null;
        }
        return Xl3000AsciiProtocol.SameUnit(unit, th.Unit)
            ? null
            : $"단위 불일치 — 장비 {unit}, 임계값 {th.Unit}. 장비 인터페이스 단위(*CONFig:UNIT:LRSniff)를 맞추세요";
    }

    public void ClearResults()
    {
        lock (_lock) _results.Clear();
    }

    /// <summary>동작 명령(*STArt / *STOp / *ZERO / *CLS) 전송 — 화면 수동 조작용.</summary>
    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        var client = _client is { IsOpen: true } c ? c : throw new InvalidOperationException("가스 디텍터가 연결되어 있지 않습니다.");
        await client.CommandAsync(command, ct);
    }

    /// <summary>설정 변경 — DB 에 저장하고 통신 경로가 바뀌었으면 다시 연결한다.</summary>
    public async Task ApplySettingsAsync(GasDetectorSettings next)
    {
        bool reconnect, thresholdChanged;
        lock (_lock)
        {
            reconnect = next.EndpointText != _settings.EndpointText || next.ResponseTimeoutMs != _settings.ResponseTimeoutMs;
            thresholdChanged = next.ThresholdValue != _threshold.Value || !Xl3000AsciiProtocol.SameUnit(next.ThresholdUnit, _threshold.Unit);
            _settings = next.Clone();
            if (thresholdChanged)
                _threshold = LocalThreshold(_settings);
        }
        await SavePersistedAsync(next);
        _logger.LogInformation("가스 디텍터 설정 변경 ({Endpoint}, 임계 {Limit:0.###} {Unit}{Src}){Re}",
            next.EndpointText, next.ThresholdValue, next.ThresholdUnit, thresholdChanged ? " — 화면 설정으로 변경" : "",
            reconnect ? " — 재연결" : "");
        if (thresholdChanged)
            ThresholdChanged?.Invoke(CurrentThreshold);
        if (reconnect)
            RequestReconnect();
    }

    /// <summary>현재 설정으로 즉시 다시 연결.</summary>
    public void RequestReconnect()
    {
        _reconnectRequested = true;
        _retryWarned = false;
        _linkUp = false;
        _wakeCts.Cancel();
    }

    private async Task DelayAsync(int ms, CancellationToken stoppingToken)
    {
        CancellationTokenSource wake;
        lock (_lock)
        {
            if (_wakeCts.IsCancellationRequested)
            {
                _wakeCts.Dispose();
                _wakeCts = new CancellationTokenSource();
            }
            wake = _wakeCts;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, wake.Token);
        try { await Task.Delay(ms, linked.Token); }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* 재연결 요청으로 깨어남 */ }
        catch (OperationCanceledException) { }
    }

    private void CloseClient()
    {
        var c = _client;
        _client = null;
        c?.Dispose();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        CloseClient();
    }

    // ── DB 영속화(GasDetector.*) ─────────────────────────────────────
    private async Task LoadPersistedAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var p = scope.ServiceProvider.GetRequiredService<ParameterService>();
            var s = Settings;
            if (Enum.TryParse<GasDetectorTransportKind>(await p.GetAsync(KeyPrefix + "Transport"), out var t)) s.Transport = t;
            if (await p.GetAsync(KeyPrefix + "PortName") is { Length: > 0 } port) s.PortName = port;
            if (await p.GetIntAsync(KeyPrefix + "BaudRate") is { } baud) s.BaudRate = baud;
            if (await p.GetAsync(KeyPrefix + "Host") is { Length: > 0 } host) s.Host = host;
            if (await p.GetIntAsync(KeyPrefix + "TcpPort") is { } tcp) s.TcpPort = tcp;
            if (await p.GetDoubleAsync(KeyPrefix + "ThresholdValue") is { } lim) s.ThresholdValue = lim;
            if (await p.GetAsync(KeyPrefix + "ThresholdUnit") is { Length: > 0 } unit) s.ThresholdUnit = unit;
            if (await p.GetIntAsync(KeyPrefix + "MaxRetries") is { } retries) s.MaxRetries = retries;
            if (await p.GetIntAsync(KeyPrefix + "MeasureDwellMs") is { } dwell) s.MeasureDwellMs = dwell;
            if (await p.GetIntAsync(KeyPrefix + "MeasureSampleCount") is { } n) s.MeasureSampleCount = n;
            if (await p.GetIntAsync(KeyPrefix + "MeasureSampleIntervalMs") is { } iv) s.MeasureSampleIntervalMs = iv;
            lock (_lock)
            {
                _settings = s;
                _threshold = LocalThreshold(s);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("가스 디텍터 저장 설정 읽기 실패 — appsettings 기본값 사용: {Err}", ex.Message);
        }
    }

    private async Task SavePersistedAsync(GasDetectorSettings s)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var p = scope.ServiceProvider.GetRequiredService<ParameterService>();
            var inv = CultureInfo.InvariantCulture;
            await p.SetAsync(KeyPrefix + "Transport", s.Transport.ToString(), "가스 디텍터 통신 경로 (Serial/Tcp)");
            await p.SetAsync(KeyPrefix + "PortName", s.PortName, "가스 디텍터 시리얼 포트");
            await p.SetAsync(KeyPrefix + "BaudRate", s.BaudRate.ToString(inv), "가스 디텍터 보율");
            await p.SetAsync(KeyPrefix + "Host", s.Host, "가스 디텍터 TCP 호스트");
            await p.SetAsync(KeyPrefix + "TcpPort", s.TcpPort.ToString(inv), "가스 디텍터 TCP 포트");
            await p.SetDoubleAsync(KeyPrefix + "ThresholdValue", s.ThresholdValue, "가스 판정 임계값 (초과 NG) — SAIGE 수신값이 덮어씀");
            await p.SetAsync(KeyPrefix + "ThresholdUnit", s.ThresholdUnit, "가스 판정 임계값 단위 (장비 인터페이스 단위와 같아야 함)");
            await p.SetAsync(KeyPrefix + "MaxRetries", s.MaxRetries.ToString(inv), "NG 시 재측정 최대 횟수 (0~2)");
            await p.SetAsync(KeyPrefix + "MeasureDwellMs", s.MeasureDwellMs.ToString(inv), "경유점 측정 전 대기 [ms]");
            await p.SetAsync(KeyPrefix + "MeasureSampleCount", s.MeasureSampleCount.ToString(inv), "경유점 측정 샘플 수");
            await p.SetAsync(KeyPrefix + "MeasureSampleIntervalMs", s.MeasureSampleIntervalMs.ToString(inv), "경유점 측정 샘플 간격 [ms]");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("가스 디텍터 설정 저장 실패(이번 실행에만 적용): {Err}", ex.Message);
        }
    }
}

/// <summary>SAIGE 상태 전송(§4) <c>sensor</c> 블록 — state / errorCode / calibrated(미확인이면 null).</summary>
public sealed record GasSensorStatus(string State, int? ErrorCode, bool? Calibrated);
