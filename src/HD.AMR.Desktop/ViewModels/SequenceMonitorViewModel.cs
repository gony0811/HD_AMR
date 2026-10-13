using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>시퀀스 모니터 카메라 칸의 표시 종류 — 단계 키로 정한다.</summary>
public enum MonitorCameraKind { None, Flat, LaserWd, Peak, Bead }

/// <summary>
/// 시퀀스 모니터 창 뷰모델. 싱글톤 <see cref="SequenceMonitorService"/> 를 구독해 현재 단계·상태·로그를
/// 실시간 표시한다. 카메라를 쓰는 단계(평탄면 센터링·레이저 WD·Peak/Bead 찾기·센터링)에서는 아래 카메라 칸에
/// 라이브 영상(Depth/IR + ROI)과 그 단계가 판단에 쓴 정지 화면을 함께 보여준다.
/// </summary>
public sealed partial class SequenceMonitorViewModel : ObservableObject, IDisposable
{
    private readonly SequenceMonitorService _monitor;
    private readonly CameraService _cam;
    private readonly WeldTrackingService _weld;
    private readonly FlatDetectionMonitor _flat;
    private readonly LaserDisplacementSensorService _laser;
    private readonly DispatcherTimer _timer;
    private bool _polling;
    private byte[]? _lastOverlayRef;
    private CameraFrame? _flatFrameRef, _preLaserFrameRef;

    /// <summary>마지막 스텝(모니터 닫기)이 닫기를 요청하면 발생 — 창이 구독해 self-close.</summary>
    public event Action? CloseRequested;

    public SequenceMonitorViewModel(SequenceMonitorService monitor, CameraService cam, WeldTrackingService weld,
        FlatDetectionMonitor flat, LaserDisplacementSensorService laser)
    {
        _monitor = monitor;
        _cam = cam;
        _weld = weld;
        _flat = flat;
        _laser = laser;
        _monitor.Changed += OnChanged;
        _flat.Changed += OnFlatChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        Refresh();
        _ = ApplyFlatAsync(_flat.Latest);
    }

    [ObservableProperty] private bool _hasStep;
    [ObservableProperty] private string _stepHeader = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _statusKind;   // 0 대기,1 실행,3 실패,2 완료
    [ObservableProperty] private string _targetDistanceText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _idleText = "시퀀스 대기 중… 시퀀스 페이지에서 실행하면 진행 상황이 여기에 표시됩니다.";

    // ── 카메라 칸 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCamera), nameof(ShowTwoPane), nameof(ShowFlatPane),
        nameof(ShowResultImage), nameof(ShowLaserPane), nameof(CameraHint), nameof(LiveTitle))]
    private MonitorCameraKind _cameraKind;

    [ObservableProperty] private Bitmap? _liveImage;
    [ObservableProperty] private Bitmap? _resultImage;
    [ObservableProperty] private string _resultTitle = "최근 검출 결과";
    [ObservableProperty] private Bitmap? _flatImage;
    [ObservableProperty] private string _flatTitle = "평탄 셀 검출";
    [ObservableProperty] private FlatDetectionSnapshot? _flatSnapshot;
    [ObservableProperty] private Bitmap? _preLaserImage;
    [ObservableProperty] private string _preLaserTitle = "레이저 측정 직전";
    [ObservableProperty] private string _laserText = "";
    [ObservableProperty] private string _flatLaserText = "레이저 측정 전";

    /// <summary>라이브 영상 위에 그릴 ROI(검출 프레임 픽셀) — Peak/Bead 단계만.</summary>
    [ObservableProperty] private RoiRect? _liveRoi;
    public int LiveFrameW => _cam.LatestIr?.Width ?? _cam.Settings.IrWidth;
    public int LiveFrameH => _cam.LatestIr?.Height ?? _cam.Settings.IrHeight;

    public bool ShowCamera => CameraKind != MonitorCameraKind.None;
    public bool ShowTwoPane => CameraKind is MonitorCameraKind.LaserWd or MonitorCameraKind.Peak or MonitorCameraKind.Bead;
    public bool ShowFlatPane => CameraKind == MonitorCameraKind.Flat;
    public bool ShowResultImage => CameraKind is MonitorCameraKind.Peak or MonitorCameraKind.Bead;
    public bool ShowLaserPane => CameraKind == MonitorCameraKind.LaserWd;
    public string LiveTitle => CameraKind == MonitorCameraKind.Bead ? "라이브 (IR)" : "라이브 (Depth)";

    public string CameraHint => CameraKind switch
    {
        MonitorCameraKind.Flat => "Depth 라이브 + 평탄 셀 검출 + 레이저 측정 직전",
        MonitorCameraKind.LaserWd => "Depth 라이브 + 레이저 3점 값",
        MonitorCameraKind.Peak => "Depth 라이브 + Peak 검출 결과",
        MonitorCameraKind.Bead => "IR 라이브 + 비드 검출 결과",
        _ => "",
    };

    // ── 마우스 hover 거리(mm) — 정규화 좌표. IR 은 Depth 와 같은 센서·해상도라 같은 위치의 깊이를 읽는다. ──
    public int? ProbeLive(double u, double v) => _cam.GetLatestDepthMmAt(u, v);
    public int? ProbeFlat(double u, double v) => CameraService.DepthMmAt(FlatSnapshot?.Analysis.Frame, u, v);
    public int? ProbePreLaser(double u, double v) => CameraService.DepthMmAt(FlatSnapshot?.PreLaserFrame, u, v);

    private static MonitorCameraKind KindOf(string? key) => key switch
    {
        "flatSurfaceAlign" => MonitorCameraKind.Flat,
        "laserWorkingDistance" => MonitorCameraKind.LaserWd,
        "peak1Find" or "peak2Find" or "peak1Center" or "peak2Center" => MonitorCameraKind.Peak,
        "bead1Find" or "bead2Find" or "bead1Center" or "bead2Center" => MonitorCameraKind.Bead,
        _ => MonitorCameraKind.None,
    };

    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        Refresh();
        if (_monitor.CloseRequested) CloseRequested?.Invoke();
    });

    private void Refresh()
    {
        HasStep = _monitor.StepKey is not null;
        StepHeader = $"{_monitor.StepNo} {_monitor.StepName}".Trim();
        TargetDistanceText = $"{_monitor.CameraTargetDistanceMm:0} mm";
        LogText = string.Join("\n", _monitor.SnapshotLines());
        (StatusKind, StatusText) = _monitor switch
        {
            { RunActive: true } => (1, "실행 중"),
            { LastEndedInFailure: true } => (3, "실패로 종료"),
            { StepKey: not null } => (2, "완료"),
            _ => (0, "대기"),
        };
        var kind = KindOf(_monitor.StepKey);
        if (kind != CameraKind) { CameraKind = kind; LiveImage = null; }
    }

    // ── 라이브 영상·결과 폴링 ──
    private async Task PollAsync()
    {
        if (_polling || !ShowCamera) return;
        _polling = true;
        try
        {
            var q = _cam.Settings.JpegQuality;
            var jpeg = CameraKind == MonitorCameraKind.Bead
                ? await _cam.GetLatestIrJpegAsync(q)
                : await _cam.GetLatestDepthJpegAsync(q);
            LiveImage = FromBytes(jpeg) ?? LiveImage;

            LiveRoi = CameraKind switch
            {
                MonitorCameraKind.Peak => _weld.PeakRoi,
                MonitorCameraKind.Bead => _weld.WeldRoi,
                _ => null,
            };

            if (ShowResultImage && !ReferenceEquals(_weld.LastOverlay, _lastOverlayRef))
            {
                _lastOverlayRef = _weld.LastOverlay;
                ResultImage = FromBytes(_lastOverlayRef);
                ResultTitle = $"최근 검출 결과 (정지 · {DateTime.Now:HH:mm:ss})";
            }

            if (ShowLaserPane) LaserText = LiveLaserText();
        }
        catch { /* 영상 한 장 실패는 다음 틱에 다시 시도 */ }
        finally { _polling = false; }
    }

    private string LiveLaserText()
    {
        if (!_laser.IsConnected) return "레이저 미연결";
        var ch = _laser.GetReadings();
        var p = _laser.GetPlanePose();
        var chText = ch.Count >= 3
            ? $"CH1 {Ch(ch[0])}\nCH2 {Ch(ch[1])}\nCH3 {Ch(ch[2])}"
            : "채널 대기 중";
        var poseText = p.Valid ? $"z = {p.Z:0.0} mm\nrx = {p.Rx:+0.00;-0.00}°\nry = {p.Ry:+0.00;-0.00}°" : p.Note ?? "";
        return $"{chText}\n\n{poseText}";
        static string Ch(LaserChannelReading r) => r.Enabled ? $"{r.Value:0.00} mm" : "범위 밖";
    }

    // ── 평탄면 스냅샷(검출 정지 화면·레이저 측정 직전) ──
    private void OnFlatChanged(FlatDetectionSnapshot? snapshot)
        => Dispatcher.UIThread.Post(() => _ = ApplyFlatAsync(snapshot));

    private async Task ApplyFlatAsync(FlatDetectionSnapshot? s)
    {
        FlatSnapshot = s;
        FlatTitle = s is null ? "평탄 셀 검출" : $"평탄 셀 검출 (정지 · {s.DetectedAt:HH:mm:ss})";
        PreLaserTitle = s?.PreLaserAt is { } t ? $"레이저 측정 직전 (정지 · {t:HH:mm:ss})" : "레이저 측정 직전";
        FlatLaserText = s is { LaserRxDeg: { } rx, LaserRyDeg: { } ry }
            ? $"레이저 측정: rx {rx:+0.00;-0.00}°, ry {ry:+0.00;-0.00}°" +
              (s.LaserZmm is { } z ? $", z {z:0.0} mm" : "") +
              (s.LaserChannelsMm is { Count: >= 3 } c ? $"  [CH {c[0]:0.00}/{c[1]:0.00}/{c[2]:0.00}]" : "")
            : "레이저 측정 전";

        FlatImage = await EncodeIfChangedAsync(s?.Analysis.Frame, ref _flatFrameRef, FlatImage);
        PreLaserImage = await EncodeIfChangedAsync(s?.PreLaserFrame, ref _preLaserFrameRef, PreLaserImage);
    }

    private Task<Bitmap?> EncodeIfChangedAsync(CameraFrame? frame, ref CameraFrame? last, Bitmap? current)
    {
        if (ReferenceEquals(frame, last)) return Task.FromResult(current);
        last = frame;
        if (frame is null) return Task.FromResult<Bitmap?>(null);
        var q = _cam.Settings.JpegQuality;
        return Task.Run(() =>
        {
            try { return FromBytes(_cam.EncodeDepthJpeg(frame, q)); }
            catch { return null; }   // 정지 화면 인코딩 실패는 다른 표시를 막지 않는다
        });
    }

    private static Bitmap? FromBytes(byte[]? b)
    {
        if (b is not { Length: > 0 }) return null;
        using var ms = new MemoryStream(b);
        return new Bitmap(ms);
    }

    public void Dispose()
    {
        _timer.Stop();
        _monitor.Changed -= OnChanged;
        _flat.Changed -= OnFlatChanged;
    }
}
