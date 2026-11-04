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
/// · 검사 시퀀스는 경유점마다 <see cref="MeasureAsync"/> 로 측정·판정(OK/NG)하고 결과는 <see cref="Results"/> 에 쌓인다.
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

    public GasDetectorService(IOptions<GasDetectorSettings> options, IOptions<InspectionSensorSettings> sensor,
        IServiceScopeFactory scopeFactory, ILogger<GasDetectorService> logger)
    {
        _settings = options.Value.Clone();
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
                    _retryWarned = false;
                    LastError = null;
                    _logger.LogInformation("가스 디텍터 연결 ({Endpoint})", s.EndpointText);
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (TimeoutException ex)
        {
            LastError = ex.Message;
            if (++_consecutiveTimeouts >= Math.Max(1, Settings.MaxConsecutiveTimeouts))
            {
                _logger.LogWarning("가스 디텍터 연속 무응답 {N}회 — 다시 연결합니다 ({Endpoint})", _consecutiveTimeouts, Settings.EndpointText);
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
            _logger.LogWarning("가스 디텍터 폴링 실패 — 다시 연결합니다: {Err}", ex.Message);
            _pollCount = 0;
            CloseClient();
        }
    }

    private static async Task<string?> TryQueryAsync(Xl3000FlexClient client, string command, CancellationToken ct)
    {
        try { return await client.QueryAsync(command, ct); }
        catch (GasDetectorException) { return null; }   // 펌웨어·모드에 따라 지원하지 않는 조회 — 표시만 생략.
    }

    /// <summary>
    /// 경유점 1곳 측정 — <see cref="GasDetectorSettings.MeasureDwellMs"/> 대기 후 샘플을 N회 읽어
    /// 최대값을 판정 기준과 비교한다. 통신 실패는 예외 대신 <see cref="GasJudgement.Error"/> 결과로 돌려준다.
    /// </summary>
    public async Task<GasMeasurementResult> MeasureAsync(string label, CancellationToken ct = default)
    {
        var s = Settings;
        var started = DateTime.UtcNow;
        var samples = new List<double>();
        string? error = null;

        if (_client is not { IsOpen: true } client)
        {
            error = $"가스 디텍터 미연결 ({s.EndpointText})";
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
                    samples.Add(await client.ReadLeakRateMbarLsAsync(ct));
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

        var judgement = GasMeasurementResult.Judge(samples, s.LeakRateLimitMbarLs);
        GasMeasurementResult result;
        lock (_lock)
        {
            result = new GasMeasurementResult(++_resultSeq, label, started, DateTime.UtcNow, samples.Count,
                samples.Count > 0 ? samples.Max() : null, samples.Count > 0 ? samples.Average() : null,
                s.LeakRateLimitMbarLs, judgement, judgement == GasJudgement.Error ? error ?? "유효 샘플 없음" : null);
            _results.Add(result);
            if (_results.Count > MaxResults)
                _results.RemoveRange(0, _results.Count - MaxResults);
        }

        if (judgement == GasJudgement.Error)
            _logger.LogWarning("[가스 측정] {Label} 측정 실패: {Err}", label, result.Error);
        else
            _logger.LogInformation("[가스 측정] {Label} {Judge} — max {Max:0.00E+0} / avg {Avg:0.00E+0} mbar·l/s (기준 {Limit:0.00E+0}, {N}샘플)",
                label, judgement, result.MaxMbarLs, result.AvgMbarLs, s.LeakRateLimitMbarLs, samples.Count);

        ResultAdded?.Invoke(result);
        return result;
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
        bool reconnect;
        lock (_lock)
        {
            reconnect = next.EndpointText != _settings.EndpointText || next.ResponseTimeoutMs != _settings.ResponseTimeoutMs;
            _settings = next.Clone();
        }
        await SavePersistedAsync(next);
        _logger.LogInformation("가스 디텍터 설정 변경 ({Endpoint}, 기준 {Limit:0.00E+0} mbar·l/s){Re}",
            next.EndpointText, next.LeakRateLimitMbarLs, reconnect ? " — 재연결" : "");
        if (reconnect)
            RequestReconnect();
    }

    /// <summary>현재 설정으로 즉시 다시 연결.</summary>
    public void RequestReconnect()
    {
        _reconnectRequested = true;
        _retryWarned = false;
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
            if (await p.GetDoubleAsync(KeyPrefix + "LeakRateLimitMbarLs") is { } lim) s.LeakRateLimitMbarLs = lim;
            if (await p.GetIntAsync(KeyPrefix + "MeasureDwellMs") is { } dwell) s.MeasureDwellMs = dwell;
            if (await p.GetIntAsync(KeyPrefix + "MeasureSampleCount") is { } n) s.MeasureSampleCount = n;
            if (await p.GetIntAsync(KeyPrefix + "MeasureSampleIntervalMs") is { } iv) s.MeasureSampleIntervalMs = iv;
            lock (_lock) _settings = s;
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
            await p.SetDoubleAsync(KeyPrefix + "LeakRateLimitMbarLs", s.LeakRateLimitMbarLs, "리크 판정 기준 [mbar·l/s]");
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
