using System.Collections.Concurrent;
using HD_AMR.Contracts.Lidar;
using HD_AMR.LidarService.Detection;
using HD_AMR.LidarService.Device;

namespace HD_AMR.LidarService;

/// <summary>
/// 센서 수명과 측정을 관리한다. 서비스 전체에서 싱글턴이다.
///
/// <b>모든 네이티브 호출을 전용 스레드 하나에 가둔다.</b> nanolib 의 스레드 안전성이
/// 확인되지 않았으므로(진단 보고서 5-4), ASP.NET 요청 스레드에서 직접 호출하지 않고
/// 작업 큐를 통해 직렬화한다. 스레드 안전이 확인되면 완화할 수 있지만, 지금은 보수적으로
/// 간다 — 네이티브 라이브러리의 경합은 재현이 어렵고 증상이 엉뚱하게 나타난다.
/// </summary>
internal sealed class LidarSession : IAsyncDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _worker;
    private readonly ILidarDevice _device;
    private readonly IRidgeDetector _detector;
    private readonly LidarSessionOptions _options;
    private readonly ILogger<LidarSession> _log;

    private long _seq;
    private DateTimeOffset? _lastFrameAt;
    private double _measuredFps;
    private volatile string? _lastError;

    /// <summary>
    /// 워치독이 워커 스레드 밖에서 읽는 연결 상태. 작업 큐를 거치지 않고 즉시 판단해야
    /// 하므로 별도 필드로 둔다 — 워치독이 매 순회마다 큐에 작업을 넣으면, 정작 연결이
    /// 끊겨 3초씩 블로킹되는 상황에서 큐가 밀린다.
    /// </summary>
    private volatile bool _connected;

    /// <summary>센서가 연결되어 사용 가능한 상태인지. 어느 스레드에서든 읽을 수 있다.</summary>
    public bool IsConnected => _connected;

    public LidarSession(
        ILidarDevice device,
        IRidgeDetector detector,
        LidarSessionOptions options,
        ILogger<LidarSession> log)
    {
        _device = device;
        _detector = detector;
        _options = options;
        _log = log;

        _worker = new Thread(WorkerLoop)
        {
            Name = "lidar-native",
            IsBackground = true,
        };
        _worker.Start();
    }

    /// <summary>
    /// 프레임 수신을 기록하고 실측 프레임 레이트를 갱신한다.
    ///
    /// <b>측정 사이의 공백은 프레임 레이트가 아니다.</b> 정지 측정이라 측정 요청 간격이 수 분씩
    /// 벌어지는 것이 정상인데, 그 간격을 그대로 넣으면 fps 가 0.003 같은 값으로 표시된다.
    /// 스트리밍 주기로 볼 수 없는 간격(1초 초과)은 표본에서 버린다 — 15fps 에서 프레임 주기는
    /// 64ms 이고, 손실이 있어도 667ms 를 넘은 관측이 없다.
    ///
    /// 워커 스레드에서만 호출되므로 동기화가 필요 없다. 상태 조회도 같은 스레드를 탄다.
    /// </summary>
    private void NoteFrame(DateTimeOffset at)
    {
        if (_lastFrameAt is { } previous)
        {
            var intervalMs = (at - previous).TotalMilliseconds;
            if (intervalMs is > 0 and < 1000)
            {
                var instant = 1000.0 / intervalMs;
                _measuredFps = _measuredFps <= 0 ? instant : _measuredFps * 0.8 + instant * 0.2;
            }
        }

        _lastFrameAt = at;
    }

    private void WorkerLoop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { _log.LogError(ex, "작업 스레드에서 처리되지 않은 예외."); }
        }
    }

    /// <summary>네이티브 호출을 전용 스레드에서 실행하고 결과를 기다린다.</summary>
    private Task<T> RunAsync<T>(Func<T> func, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, CancellationToken.None);

        return tcs.Task;
    }

    /// <summary>센서 연결. 실패하면 예외를 던지지 않고 상태에 기록한다 — 서비스는 계속 떠 있어야 한다.</summary>
    public Task<bool> TryOpenAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        try
        {
            // 직전 시도가 핸들을 남긴 채 실패했을 수 있다. 그대로 다시 열면 -4
            // (NSL_IP_DUPLICATED)가 나므로 먼저 정리한다.
            if (_device.IsOpen) _device.Close();

            _device.Open();
            _device.StartStreaming();
            _lastError = null;
            _connected = true;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _connected = false;

            // 연결 실패는 케이블이 빠져 있는 동안 계속 반복된다. 매번 스택 트레이스를
            // 남기면 저널이 뒤덮이므로 메시지만 남긴다.
            _log.LogWarning("센서 연결 실패: {Message}", ex.Message);
            return false;
        }
    }, ct);

    /// <summary>
    /// 프레임 하나만 받아온다. 모니터링 미리보기 전용이다.
    ///
    /// 측정 경로와 달리 프레임 손실을 재시도하지 않고 null 로 넘긴다 — 미리보기는 다음 주기에
    /// 다시 받으면 그만이고, 여기서 재시도하면 워커 스레드를 오래 잡아 실제 측정이 밀린다.
    /// 불완전 프레임도 그대로 준다. 미리보기의 목적은 <b>이상을 보이게 하는 것</b>이므로
    /// 조용히 버리면 안 된다.
    /// </summary>
    public Task<LidarCapture?> CaptureOneAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        if (!_device.IsOpen) return (LidarCapture?)null;

        try
        {
            var frame = _device.Capture(_options.PerFrameTimeoutMs);
            if (frame is not null) NoteFrame(frame.CapturedAt);
            return frame;
        }
        catch (LidarDeviceException ex)
        {
            _lastError = ex.Message;
            _connected = false;
            _log.LogError(ex, "미리보기 프레임 수신 중 장치 오류. 재연결이 필요하다.");
            return null;
        }
    }, ct);

    /// <summary>
    /// 1회 측정. 정지 상태를 전제로 N 프레임을 모아 평균한 뒤 능선을 검출한다.
    ///
    /// 프레임을 놓치는 것은 정상 범위이므로 즉시 실패하지 않고 여유분만큼 재시도한다.
    /// 다만 전체 시간은 요청의 타임아웃 안에서 끝낸다.
    /// </summary>
    public Task<MeasureResponse> MeasureAsync(MeasureRequest request, CancellationToken ct = default) =>
        RunAsync(() => Measure(request), ct);

    private MeasureResponse Measure(MeasureRequest request)
    {
        var seq = Interlocked.Increment(ref _seq);
        var startedAt = DateTimeOffset.UtcNow;

        if (!_device.IsOpen)
        {
            return Failed(seq, startedAt, MeasureFailure.NotConnected,
                _lastError ?? "센서가 열려 있지 않다.", null);
        }

        int wanted = Math.Clamp(request.Frames, 1, _options.MaxFramesPerMeasure);
        var deadline = startedAt.AddMilliseconds(Math.Clamp(request.TimeoutMs, 100, _options.MaxTimeoutMs));

        var frames = new List<LidarCapture>(wanted);
        int misses = 0;
        int incomplete = 0;

        while (frames.Count < wanted && DateTimeOffset.UtcNow < deadline)
        {
            var remaining = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
            if (remaining <= 0) break;

            LidarCapture? frame;
            try
            {
                frame = _device.Capture(Math.Min(remaining, _options.PerFrameTimeoutMs));
            }
            catch (LidarDeviceException ex)
            {
                // 장치 오류는 일시적 프레임 손실과 다르다. 연결이 깨진 것으로 보고
                // 워치독이 재연결하도록 표시한다.
                _lastError = ex.Message;
                _connected = false;
                _log.LogError(ex, "프레임 수신 중 장치 오류. 재연결이 필요하다.");
                return Failed(seq, startedAt, MeasureFailure.NotConnected, ex.Message, null);
            }

            if (frame is null)
            {
                if (++misses > _options.MaxFrameMisses) break;
                continue;
            }

            NoteFrame(frame.CapturedAt);

            // 불완전 프레임은 평균에 넣지 않는다. nanolib 은 손상을 알려주는 수단이 전혀
            // 없어서 데이터로 판정할 수밖에 없고, 섞이면 평균이 조용히 오염된다.
            if (!frame.IsComplete)
            {
                incomplete++;
                _log.LogWarning("불완전 프레임을 버렸다 (누적 {Count}회). 링크/센서 이상 신호일 수 있다.", incomplete);
                continue;
            }

            frames.Add(frame);
        }

        if (frames.Count == 0)
        {
            return Failed(seq, startedAt, MeasureFailure.Timeout,
                $"제한 시간 안에 유효한 프레임을 받지 못했다 " +
                $"(요청 {wanted}, 수신 실패 {misses}, 불완전 {incomplete}).", null);
        }

        var averaged = CaptureAverager.Average(frames, wanted, incomplete);
        var quality = averaged.ToQuality();

        // 유효 픽셀이 너무 적으면 검출을 시도하지 않는다 — 그런 상태에서 나온 피팅은
        // 우연히 맞은 것일 뿐이고, 신뢰도만 높게 나와 더 위험하다.
        if (quality.ValidPixels < _options.MinValidPixels)
        {
            return Failed(seq, averaged.CapturedAt, MeasureFailure.InsufficientValidPixels,
                $"유효 픽셀 {quality.ValidPixels}개로 최소 요구치 {_options.MinValidPixels}개에 못 미친다.",
                quality);
        }

        var result = _detector.Detect(averaged);

        if (!result.Success)
        {
            return Failed(seq, averaged.CapturedAt,
                result.Failure ?? MeasureFailure.RidgeFitFailed,
                result.FailureDetail ?? "능선 검출 실패.", quality);
        }

        return new MeasureResponse
        {
            Seq = seq,
            CapturedAt = averaged.CapturedAt,
            Valid = true,
            Frame = LidarFrame.SensorOptical,
            Ridge = result.Ridge,
            Confidence = result.Confidence,
            Quality = quality,
            Samples = request.IncludeSamples ? result.Inliers : null,
        };
    }

    private static MeasureResponse Failed(
        long seq, DateTimeOffset at, MeasureFailure reason, string detail, MeasurementQuality? quality) => new()
    {
        Seq = seq,
        CapturedAt = at,
        Valid = false,
        Frame = LidarFrame.SensorOptical,
        Failure = reason,
        FailureDetail = detail,
        Quality = quality,
    };

    public Task<LidarStatus> GetStatusAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        var status = _device.ReadStatus();

        // 프레임 레이트는 장치 구현이 아니라 세션이 채운다. 실장비 구현은 이 값을 알 방법이
        // 없어서 늘 0 이었고(nanolib 이 프레임 레이트를 보고하지 않는다), 재생/합성 구현은
        // 반대로 고정값 15 를 넣어 실제와 무관한 숫자를 보고했다. 어느 쪽이든 화면에 뜨는
        // 값이 거짓이라, 프레임 도착 간격을 직접 재는 이쪽에서 덮어쓴다.
        return status with
        {
            LastFrameAt = _lastFrameAt,
            MeasuredFps = Math.Round(_measuredFps, 2),
            LastError = status.LastError ?? _lastError,
        };
    }, ct);

    public Task<LidarConfig> GetConfigAsync(CancellationToken ct = default) =>
        RunAsync(_device.ReadConfig, ct);

    public Task<LidarConfig> PatchConfigAsync(LidarConfigPatch patch, CancellationToken ct = default) =>
        RunAsync(() => { _device.ApplyConfig(patch); return _device.ReadConfig(); }, ct);

    public Task PersistConfigAsync(CancellationToken ct = default) =>
        RunAsync<object?>(() => { _device.PersistConfig(); return null; }, ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RunAsync<object?>(() => { _device.Dispose(); return null; }, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "종료 중 센서 정리 실패.");
        }

        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}

internal sealed class LidarSessionOptions
{
    /// <summary>한 번의 측정에 허용하는 최대 프레임 수. 요청이 이보다 크면 잘린다.</summary>
    public int MaxFramesPerMeasure { get; set; } = 50;

    /// <summary>측정 전체 타임아웃 상한(ms).</summary>
    public int MaxTimeoutMs { get; set; } = 15000;

    /// <summary>
    /// 프레임 하나를 기다리는 시간(ms). 실측상 15fps 라 프레임 주기가 64ms 이고,
    /// waitTimeMs 는 100ms 미만이면 데이터가 오기 전에 타임아웃된다.
    /// </summary>
    public int PerFrameTimeoutMs { get; set; } = 1000;

    /// <summary>연속 프레임 손실 허용 횟수. 초과하면 모은 것까지만 쓴다.</summary>
    public int MaxFrameMisses { get; set; } = 10;

    /// <summary>
    /// 검출을 시도하기 위한 최소 유효 픽셀 수.
    ///
    /// 전체 화면(320x240 = 76,800px) 기준 실내 벽면에서 유효율이 94.9% 나오므로 평상시엔
    /// 7만 개 이상이다. ROI 를 좁히면 그만큼 줄어든다. 2,000 은 "명백히 뭔가 잘못됐다"를
    /// 거르는 하한이고, 실제 운용 ROI 가 정해지면 그에 맞춰 올려야 한다.
    ///
    /// ⚠ 금속 코러게이션에서는 마루의 정반사 포화와 골의 신호 부족이 함께 나타날 수 있어
    ///   유효율이 크게 떨어질 수 있다. 실제 대상(scene_05) 확보 후 재조정 필요.
    /// </summary>
    public int MinValidPixels { get; set; } = 2000;
}
