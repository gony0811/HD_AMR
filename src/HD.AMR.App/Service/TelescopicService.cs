using HD.AMR.App.Communication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// Z축 텔레스코픽(EBLUM 3채널 홀 동기 컨트롤러) 서비스. AMR/Cobot/IO 와 동일하게 싱글톤 + 호스티드로
/// 등록되어 기동 시 자동 연결하고 실패 시 주기적으로 재시도한다. 컨트롤러가 상태를 자동 통보하지 않으므로
/// <c>Read</c> 를 주기 폴링해 <see cref="Latest"/> 로 캐싱한다.
///
/// <b>안전 설계 — 이 서비스는 1m 행정의 리프트를 실제로 움직인다.</b>
/// 프로토콜의 상승/하강은 "명령 유지" 방식이라 정지 명령을 보내지 않으면 리미트까지 계속 간다. 그래서:
///   · 조그는 <see cref="BeginJogAsync"/> ~ <see cref="EndJogAsync"/> 세션으로만 노출한다.
///   · 세션이 살아 있는 동안 <see cref="TelescopicSerialSettings.JogKeepAliveMs"/> 주기로 명령을 재전송하고
///     (사양서 FAQ 1: 상승 명령 반복 전송은 무해), UI 가 <see cref="KeepJogAlive"/> 를 끊으면
///     <see cref="TelescopicSerialSettings.JogDeadmanMs"/> 안에 <b>자동 정지</b>한다 — 화면이 멈추거나
///     페이지가 닫혀도 리프트가 계속 올라가지 않게 하는 데드맨이다.
///   · 1회 연속 조그는 <see cref="TelescopicSerialSettings.JogMaxDurationMs"/> 로 상한을 둔다.
///   · 서비스 종료 시 정지 명령을 보낸다.
/// </summary>
public class TelescopicService : BackgroundService
{
    private readonly TelescopicSerialSettings _settings;
    private readonly TelescopicClient _client;
    private readonly ILogger<TelescopicService> _logger;

    private readonly object _stateLock = new();
    private TelescopicStatus? _latest;
    private DateTime _latestUtc;

    // 조그 세션 상태
    private readonly object _jogLock = new();
    private CancellationTokenSource? _jogCts;
    private TelescopicProtocol.HandleBits _jogBits;
    private DateTime _jogKeepAliveUtc;
    private bool _retryWarned;

    public TelescopicService(IOptions<TelescopicSerialSettings> options, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _client = new TelescopicClient(_settings, loggerFactory.CreateLogger<TelescopicClient>());
        _logger = loggerFactory.CreateLogger<TelescopicService>();
    }

    public bool IsConnected => _client.IsOpen;
    public TelescopicSerialSettings Settings => _settings;

    /// <summary>마지막 연결/통신 오류(정상 시 null).</summary>
    public string? LastError { get; private set; }

    /// <summary>마지막 송신 명령 — 화면 진단용.</summary>
    public string? LastTx => _client.LastTx;

    /// <summary>마지막 수신 응답 — 화면 진단용.</summary>
    public string? LastRx => _client.LastRx;

    /// <summary>조그 세션이 살아 있는지.</summary>
    public bool IsJogging
    {
        get { lock (_jogLock) return _jogCts is { IsCancellationRequested: false }; }
    }

    /// <summary>가장 최근 상태 스냅샷(아직 못 읽었으면 null).</summary>
    public TelescopicStatus? Latest
    {
        get { lock (_stateLock) return _latest; }
    }

    /// <summary>마지막 상태 수신 시각(UTC).</summary>
    public DateTime? LatestUtc
    {
        get { lock (_stateLock) return _latest is null ? null : _latestUtc; }
    }

    /// <summary>이 환경에서 보이는 시리얼 포트 목록 — 화면의 포트 선택 도움용.</summary>
    public static string[] AvailablePorts() => TelescopicClient.AvailablePorts();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TelescopicService 시작 ({Port} @ {Baud})", _settings.PortName, _settings.BaudRate);

        // host.Start() 는 첫 await 까지 동기 실행한다 — 아래 _client.Open()(SerialPort.Open 은 동기 블로킹)이
        // 기동 메인 스레드를 붙잡지 않도록 먼저 스레드풀로 양보한다.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsOpen)
                {
                    _client.Open();
                    LastError = null;
                    _retryWarned = false;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                if (!_retryWarned)
                {
                    _logger.LogWarning("텔레스코픽 연결 실패, 이후 자동 재시도 — {Err}", ex.Message);
                    _retryWarned = true;
                }
                await Task.Delay(_settings.ReconnectDelayMs, stoppingToken);
                continue;
            }

            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger.LogWarning("텔레스코픽 폴링 실패 — 재연결 시도: {Err}", ex.Message);
                _client.Close();
            }

            await Task.Delay(_settings.PollIntervalMs, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 종료 시 반드시 멈춘다 — 조그 중 앱이 내려가면 리프트가 계속 간다.
        try { await EndJogAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "종료 중 텔레스코픽 정지 실패"); }
        await base.StopAsync(cancellationToken);
        _client.Close();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var response = await _client.SendAsync(TelescopicProtocol.Read(), ct).ConfigureAwait(false);
        var status = TelescopicProtocol.ParseStatus(response);
        if (status is null)
        {
            // 조그 중에는 유지 명령의 응답과 겹칠 수 있어 한 번 놓치는 것은 정상이다.
            if (!IsJogging && response is not null)
                _logger.LogDebug("텔레스코픽 상태 파싱 실패 — RX={Rx}", response);
            return;
        }

        lock (_stateLock)
        {
            _latest = status;
            _latestUtc = DateTime.UtcNow;
        }
        LastError = null;
    }

    private void UpdateStatus(string? response)
    {
        var status = TelescopicProtocol.ParseStatus(response);
        if (status is null) return;

        lock (_stateLock)
        {
            _latest = status;
            _latestUtc = DateTime.UtcNow;
        }
        LastError = null;
    }

    // ── 조그(상승/하강) ─────────────────────────────────────────────
    /// <summary>
    /// 조그 세션 시작. <paramref name="up"/>=true 상승, false 하강.
    /// 세션이 끝나거나 데드맨이 만료되면 자동으로 정지 명령을 보낸다.
    /// UI 는 버튼을 누르고 있는 동안 <see cref="KeepJogAlive"/> 를 주기 호출해야 한다.
    /// </summary>
    public Task BeginJogAsync(bool up, CancellationToken ct = default)
    {
        var bits = up ? TelescopicProtocol.HandleBits.Up : TelescopicProtocol.HandleBits.Down;

        lock (_jogLock)
        {
            if (_jogCts is { IsCancellationRequested: false } && _jogBits == bits)
            {
                _jogKeepAliveUtc = DateTime.UtcNow;   // 이미 같은 방향으로 돌고 있다 — 갱신만.
                return Task.CompletedTask;
            }
            _jogCts?.Cancel();
            _jogCts = new CancellationTokenSource();
            _jogBits = bits;
            _jogKeepAliveUtc = DateTime.UtcNow;
            _ = JogLoopAsync(bits, _jogCts.Token);
        }
        return Task.CompletedTask;
    }

    /// <summary>조그 데드맨 갱신 — UI 가 버튼을 누르고 있는 동안 주기 호출.</summary>
    public void KeepJogAlive()
    {
        lock (_jogLock) _jogKeepAliveUtc = DateTime.UtcNow;
    }

    /// <summary>조그 세션 종료 + 정지 명령. 여러 번 불려도 안전하다.</summary>
    public async Task EndJogAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        lock (_jogLock)
        {
            cts = _jogCts;
            _jogCts = null;
            _jogBits = TelescopicProtocol.HandleBits.None;
        }
        cts?.Cancel();
        cts?.Dispose();
        await StopAsync_Internal(ct).ConfigureAwait(false);
    }

    private async Task JogLoopAsync(TelescopicProtocol.HandleBits bits, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var command = TelescopicProtocol.Handle(bits);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                DateTime keepAlive;
                lock (_jogLock) keepAlive = _jogKeepAliveUtc;

                if ((DateTime.UtcNow - keepAlive).TotalMilliseconds > _settings.JogDeadmanMs)
                {
                    _logger.LogInformation("텔레스코픽 조그 데드맨 만료 — 자동 정지");
                    break;
                }
                if ((DateTime.UtcNow - started).TotalMilliseconds > _settings.JogMaxDurationMs)
                {
                    _logger.LogInformation("텔레스코픽 조그 최대 시간 초과 — 자동 정지");
                    break;
                }

                await _client.SendNoWaitAsync(command, ct).ConfigureAwait(false);
                await Task.Delay(_settings.JogKeepAliveMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 정상 종료 */ }
        catch (Exception ex) { _logger.LogWarning(ex, "텔레스코픽 조그 루프 오류 — 정지합니다"); }
        finally
        {
            lock (_jogLock)
            {
                if (_jogBits == bits) { _jogCts = null; _jogBits = TelescopicProtocol.HandleBits.None; }
            }
            try { await StopAsync_Internal(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "텔레스코픽 정지 명령 실패 — 물리 정지 버튼을 사용하세요"); }
        }
    }

    // ── 단발 명령 ───────────────────────────────────────────────────
    /// <summary>
    /// 즉시 정지(Handle:000) + 조그 세션 종료. 화면의 정지 버튼이 부르는 단일 진입점.
    /// (호스티드 수명의 <see cref="BackgroundService.StopAsync"/> 와 구분하려고 이름을 분리했다.)
    /// </summary>
    public Task StopMotionAsync(CancellationToken ct = default) => EndJogAsync(ct);

    private Task StopAsync_Internal(CancellationToken ct)
        => _client.IsOpen ? _client.SendAsync(TelescopicProtocol.Stop(), ct) : Task.FromResult<string?>(null);

    /// <summary>
    /// 지정 높이(mm)로 이동. 반환 true=컨트롤러가 수락, false=거부(잠김·비활성·범위 등), null=응답 불명.
    /// 설정 범위(<see cref="TelescopicSerialSettings.MinHeightMm"/>~<c>MaxHeightMm</c>) 밖이면 예외.
    /// </summary>
    public async Task<bool?> MoveToHeightAsync(int heightMm, CancellationToken ct = default)
    {
        if (heightMm < _settings.MinHeightMm || heightMm > _settings.MaxHeightMm)
            throw new ArgumentOutOfRangeException(nameof(heightMm), heightMm,
                $"운영 범위는 {_settings.MinHeightMm}~{_settings.MaxHeightMm} mm 입니다.");

        await EndJogAsync(ct).ConfigureAwait(false);   // 조그와 절대 이동을 섞지 않는다.
        var response = await _client.SendAsync(
            TelescopicProtocol.Target(heightMm, _settings.TargetDigits), ct).ConfigureAwait(false);
        return TelescopicProtocol.ParseTargetAck(response);
    }

    /// <summary>메모리 슬롯(1~3)으로 이동 — 명령 50ms 유지 후 정지.</summary>
    public Task MoveToMemoryAsync(int slot, CancellationToken ct = default)
        => PulseAsync(slot, TelescopicProtocol.MemoryMoveHoldMs, ct);

    /// <summary>현재 위치를 메모리 슬롯(1~3)에 저장 — 명령 2s 유지 후 정지.</summary>
    public Task SaveMemoryAsync(int slot, CancellationToken ct = default)
        => PulseAsync(slot, TelescopicProtocol.MemorySaveHoldMs, ct);

    private async Task PulseAsync(int slot, int holdMs, CancellationToken ct)
    {
        await EndJogAsync(ct).ConfigureAwait(false);
        await _client.SendAsync(TelescopicProtocol.Memory(slot), ct).ConfigureAwait(false);
        try { await Task.Delay(holdMs, ct).ConfigureAwait(false); }
        finally { await StopAsync_Internal(CancellationToken.None).ConfigureAwait(false); }
    }

    private static readonly TimeSpan ResetMinimumHold =
        TimeSpan.FromMilliseconds(TelescopicProtocol.ResetHoldMs + 200);
    private static readonly TimeSpan ResetStartTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ResetCompletionTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 추진기 리셋 — <c>Handle:032</c> 을 2초 초과 유지하고 운행 모드 3(리셋 중) 진입을 확인한 뒤,
    /// 모드 0(정지)으로 완료되어야 <c>Handle:000</c> 을 보낸다. 완료 전 정지는 리셋과 알람 해제를
    /// 중단한다는 사양서 FAQ 5·7을 따른다.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await EndJogAsync(ct).ConfigureAwait(false);

        var startedUtc = DateTime.UtcNow;
        var resetSeen = false;
        try
        {
            // Handle 명령의 Length 응답도 상태 프레임이므로 버리지 않고 캐시에 반영한다.
            var resetResponse = await _client.SendAsync(TelescopicProtocol.Reset(), ct).ConfigureAwait(false);
            var initialStatus = TelescopicProtocol.ParseStatus(resetResponse);
            UpdateStatus(resetResponse);
            resetSeen = initialStatus?.Mode == TelescopicProtocol.RunMode.Resetting;

            while (DateTime.UtcNow - startedUtc < ResetCompletionTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                await PollAsync(ct).ConfigureAwait(false);

                var status = Latest;
                if (status?.Mode == TelescopicProtocol.RunMode.Resetting)
                    resetSeen = true;

                var elapsed = DateTime.UtcNow - startedUtc;
                if (!resetSeen && elapsed >= ResetStartTimeout)
                    throw new InvalidOperationException(
                        "리셋 명령 후 운행 모드가 '리셋 중(3)'으로 진입하지 않았습니다. " +
                        "컨트롤러 활성·잠금 상태와 TX/RX 결선을 확인하세요.");

                if (resetSeen && elapsed >= ResetMinimumHold &&
                    status?.Mode == TelescopicProtocol.RunMode.Stopped)
                {
                    _logger.LogInformation("텔레스코픽 리셋 완료 ({Elapsed:F1}s, 높이={Height})",
                        elapsed.TotalSeconds, status.HeightMm);
                    return;
                }
            }

            throw new TimeoutException("추진기 리셋이 2분 안에 완료되지 않았습니다.");
        }
        finally
        {
            // 정상 완료, 시간 초과, 취소 모두 명령 비트를 안전하게 해제한다.
            await StopAsync_Internal(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>에러 코드 강제 클리어. 반환 true = <c>ClearErr OK</c>.</summary>
    public async Task<bool> ClearErrorAsync(CancellationToken ct = default)
        => TelescopicProtocol.ParseClearErrAck(
            await _client.SendAsync(TelescopicProtocol.ClearErr(), ct).ConfigureAwait(false));

    /// <summary>즉시 상태를 한 번 읽어 캐시를 갱신한다.</summary>
    public async Task<TelescopicStatus?> RefreshAsync(CancellationToken ct = default)
    {
        await PollAsync(ct).ConfigureAwait(false);
        return Latest;
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
