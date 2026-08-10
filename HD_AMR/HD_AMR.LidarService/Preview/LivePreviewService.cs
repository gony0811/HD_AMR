using System.Diagnostics;
using HD_AMR.LidarService.Detection;
using HD_AMR.LidarService.Device;
using HD_AMR.LidarService.Imaging;

namespace HD_AMR.LidarService.Preview;

/// <summary>
/// 모니터링 화면에 보여줄 프레임을 주기적으로 만들어 두는 백그라운드 서비스.
///
/// <b>브라우저 요청이 센서를 건드리지 않게 하는 것이 설계 목적이다.</b> 요청마다 캡처하면
/// 탭을 두 개 열었을 때 캡처 빈도가 두 배가 되고, 이미지 세 장을 각각 요청하는 구조에서는
/// 같은 화면 안에서도 서로 다른 프레임이 섞인다. 여기서는 루프가 프레임을 하나 만들어
/// 게시하고, 모든 요청은 그 결과를 읽기만 한다 — 관찰자 수와 무관하게 센서 부하가 일정하다.
///
/// 검출도 이 루프에서 돌린다. RANSAC 이 무거워서 루프가 설정 주기보다 느려질 수 있는데,
/// 밀린 만큼 따라잡으려 하지 않고 그냥 느리게 돈다. 미리보기는 실시간성이 목적이 아니라
/// <b>지금 센서가 무엇을 보고 있는지</b>를 확인하는 용도이기 때문이다.
/// </summary>
internal sealed class LivePreviewService : BackgroundService
{
    private readonly LidarSession _session;
    private readonly IRidgeDetector _detector;
    private readonly PreviewOptions _options;
    private readonly RidgeDetectorOptions _detectorOptions;
    private readonly ILogger<LivePreviewService> _log;

    private PreviewFrame? _latest;
    private long _seq;
    private long _lastRequestedTicks;
    private double _loopFps;

    public LivePreviewService(
        LidarSession session,
        IRidgeDetector detector,
        PreviewOptions options,
        RidgeDetectorOptions detectorOptions,
        ILogger<LivePreviewService> log)
    {
        _session = session;
        _detector = detector;
        _options = options;
        _detectorOptions = detectorOptions;
        _log = log;
    }

    /// <summary>가장 최근에 게시된 프레임. 아직 하나도 없으면 null.</summary>
    public PreviewFrame? Latest => Volatile.Read(ref _latest);

    /// <summary>화면이 보고 있다는 신호. 이게 없으면 루프가 잠들어 CPU 를 놓는다.</summary>
    public void Touch() => Volatile.Write(ref _lastRequestedTicks, DateTime.UtcNow.Ticks);

    /// <summary>현재 유효한 렌더 스케일. 검출기 깊이 게이트를 따라갈지 여부를 반영한다.</summary>
    public RenderScale CurrentScale()
    {
        var min = _options.MinRangeMm;
        var max = _options.MaxRangeMm;

        if (_options.FollowDetectorBand)
        {
            // 깊이 게이트 상한은 0 이 "제한 없음"이라 그대로 쓰면 컬러맵이 무너진다.
            // 그 경우에는 미리보기 자체 상한을 유지한다.
            min = _detectorOptions.MinDistanceMm > 0 ? _detectorOptions.MinDistanceMm : min;
            max = _detectorOptions.MaxDistanceMm > 0 ? _detectorOptions.MaxDistanceMm : max;
        }

        if (max <= min) max = min + 1;
        return new RenderScale(min, max, _options.AmplitudeMax);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // 직전 주기의 시작 시각. null 은 "직전 주기가 없다"는 뜻이고, 그때는 fps 표본을
        // 만들지 않는다.
        //
        // 이걸 0 으로 두면 첫 주기의 간격이 "서비스 기동 이후 경과 시간"이 되어버린다. 루프는
        // 보는 사람이 생길 때까지 잠들어 있으므로 그 값이 몇 분일 수도 있고, 그러면 화면을 처음
        // 연 순간 — 정확히 정상 동작을 확인하려는 순간 — fps 가 0 에 가깝게 뜬다.
        // 대기·재연결로 루프가 끊긴 뒤에도 같은 일이 생기므로 건너뛰는 경로마다 비운다.
        double? lastTickMs = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!IsWatched())
                {
                    lastTickMs = null;
                    await Task.Delay(250, stoppingToken);
                    continue;
                }

                if (!_session.IsConnected)
                {
                    // 센서가 없는 동안에도 화면은 상태를 계속 보여줘야 하므로 서비스는 살려 둔다.
                    // 마지막 프레임은 지우지 않는다 — 끊기기 직전 화면이 원인 파악에 쓸모가 있다.
                    lastTickMs = null;
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                var started = stopwatch.Elapsed.TotalMilliseconds;
                var frame = await _session.CaptureOneAsync(stoppingToken);

                if (frame is null)
                {
                    lastTickMs = null;
                    await Task.Delay(200, stoppingToken);
                    continue;
                }

                Publish(frame, lastTickMs is { } previous ? started - previous : 0);
                lastTickMs = started;

                var elapsed = stopwatch.Elapsed.TotalMilliseconds - started;
                var remaining = _options.IntervalMs - elapsed;
                if (remaining > 0) await Task.Delay((int)remaining, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 미리보기 실패로 서비스가 죽으면 원격 진단 수단이 통째로 사라진다.
                _log.LogError(ex, "미리보기 프레임 생성 실패. 다음 주기에 재시도한다.");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private bool IsWatched()
    {
        var last = Volatile.Read(ref _lastRequestedTicks);
        if (last == 0) return false;

        var idle = DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc);
        return idle.TotalMilliseconds < _options.IdleTimeoutMs;
    }

    private void Publish(LidarCapture frame, double sinceLastMs)
    {
        var seq = Interlocked.Increment(ref _seq);
        var scale = CurrentScale();

        var distancePng = FrameRenderer.RenderDistance(frame, scale);
        var amplitudePng = FrameRenderer.RenderAmplitude(frame, scale);

        var stats = CaptureAverager.Classify(frame);
        var (min, median, max, amplitudeP99) = Summarize(frame);

        // 지수 이동평균으로 완만하게. 매 프레임 순간값을 그대로 띄우면 숫자가 튀어서
        // 실제로 느려졌는지 판단할 수 없다.
        if (sinceLastMs > 0)
        {
            var instant = 1000.0 / sinceLastMs;
            _loopFps = _loopFps <= 0 ? instant : _loopFps * 0.7 + instant * 0.3;
        }

        byte[]? overlayPng = null;
        RidgeDetectionResult? detection = null;
        double detectMs = 0;

        if (_options.DetectionEnabled)
        {
            // 검출기는 다중 프레임 평균을 받도록 되어 있다. 미리보기는 단일 프레임이므로
            // 프레임 하나짜리 평균으로 감싼다 — 실제 측정과 같은 코드 경로를 타야 화면에서
            // 본 결과와 측정 결과가 어긋나지 않는다.
            var averaged = CaptureAverager.Average([frame], framesRequested: 1);

            var watch = Stopwatch.StartNew();
            detection = _detector.Detect(averaged);
            detectMs = watch.Elapsed.TotalMilliseconds;

            overlayPng = FrameRenderer.RenderOverlay(
                averaged,
                detection.PlaneAInliers,
                detection.PlaneBInliers,
                detection.Ridge,
                _options.RidgeBandMm);
        }

        var info = new PreviewInfo
        {
            Seq = seq,
            CapturedAt = frame.CapturedAt,
            Width = frame.Width,
            Height = frame.Height,
            TemperatureC = frame.TemperatureC,
            LoopFps = Math.Round(_loopFps, 2),
            Complete = frame.IsComplete,
            Pixels = new PreviewPixelCounts
            {
                Valid = stats.Valid,
                LowAmplitude = stats.LowAmplitude,
                AdcOverflow = stats.AdcOverflow,
                Saturation = stats.Saturation,
                Unfilled = stats.Unfilled,
                Other = stats.Other,
                Total = stats.Total,
            },
            DistanceMinMm = min,
            DistanceMedianMm = median,
            DistanceMaxMm = max,
            AmplitudeP99 = amplitudeP99,
            Scale = scale,
            DetectionEnabled = _options.DetectionEnabled,
            DetectMs = Math.Round(detectMs, 1),
            DetectionSucceeded = detection?.Success ?? false,
            Ridge = detection?.Ridge,
            Confidence = detection?.Confidence ?? 0,
            Failure = detection?.Failure,
            FailureDetail = detection?.FailureDetail,
            CandidateCount = detection?.CandidateCount ?? 0,
            PlaneAInlierCount = detection?.PlaneAInliers?.Length ?? 0,
            PlaneBInlierCount = detection?.PlaneBInliers?.Length ?? 0,
            PlaneAngleDeg = Math.Round(detection?.PlaneAngleDeg ?? 0, 2),
        };

        Volatile.Write(ref _latest, new PreviewFrame
        {
            Seq = seq,
            DistancePng = distancePng,
            AmplitudePng = amplitudePng,
            OverlayPng = overlayPng,
            Info = info,
        });
    }

    /// <summary>
    /// 유효 픽셀의 거리 분포와 진폭 상위값. 깊이 구간과 진폭 스케일을 어디에 둘지
    /// 사람이 정하려면 이 숫자가 있어야 한다 — 없으면 슬라이더를 눈감고 돌리게 된다.
    /// </summary>
    private static (double? Min, double? Median, double? Max, int? AmplitudeP99) Summarize(LidarCapture frame)
    {
        var distances = new List<int>(frame.PixelCount / 2);
        var amplitudes = new List<int>(frame.PixelCount / 2);

        for (int i = 0; i < frame.PixelCount; i++)
        {
            if (!LidarCapture.IsValid(frame.Distance2D[i])) continue;
            distances.Add(frame.Distance2D[i]);
            amplitudes.Add(frame.Amplitude[i]);
        }

        if (distances.Count == 0) return (null, null, null, null);

        distances.Sort();
        amplitudes.Sort();

        return (
            distances[0],
            distances[distances.Count / 2],
            distances[^1],
            amplitudes[(int)(amplitudes.Count * 0.99)]);
    }
}
