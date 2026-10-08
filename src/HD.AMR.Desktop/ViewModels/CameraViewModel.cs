using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// RealSense D435 카메라 뷰. 기존 CameraView.razor 이식(핵심).
/// MJPEG 대신 CameraService 의 JPEG 인코더를 ~10fps 로 폴링해 Avalonia Bitmap 으로 바인딩한다.
/// 깊이 hover 프로브(GetLatestDepthMmAt)·깊이 ROI 통계(ComputeDepthRoiStats)·학습 캡처(SaveCaptureAsync)를 지원.
/// 후속(Tier 3 확장): Bead/Peak ROI 영속, IR 노출 튜닝, 평탄면 정렬 루틴, 코봇 조그 팝업.
/// </summary>
public sealed partial class CameraViewModel : ViewModelBase
{
    private readonly CameraService _svc;
    private readonly FlatDetectionMonitor _flatMonitor;
    private readonly CobotService _cobot;
    private readonly SequenceRunGate _gate;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private bool _polling;

    private const string CaptureDirKey = "Camera.Capture.Dir";
    private const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    private const string RoiXKey = "Camera.Depth.Roi.X";
    private const string RoiYKey = "Camera.Depth.Roi.Y";
    private const string RoiWKey = "Camera.Depth.Roi.W";
    private const string RoiHKey = "Camera.Depth.Roi.H";

    // FlatSurfaceAlignStep 과 같은 값 — '평탄 검증'이 시퀀스와 같은 조건으로 검출하도록.
    private const int FlatGridSize = 5;
    private const int FlatSamples = 10;
    private const double FallbackDepthMm = 400.0;

    // 레이저 이동·측정이 시퀀스 ④와 같은 값을 쓰도록 같은 파라미터 키를 읽는다.
    private const string AlignImageXAxisKey = "Camera.Align.ImageXAxis";
    private const string AlignImageYAxisKey = "Camera.Align.ImageYAxis";
    private const string AlignToolKey = "Camera.Align.Tool";
    /// <summary>레이저 이동 속도(%) — 수동 확인용이라 시퀀스보다 느리게 고정.</summary>
    private const double LaserMoveVelocity = 10;

    // 파라미터 로드 중 ROI 값 세팅은 사용자 변경이 아니므로 검출 결과를 지우지 않는다.
    private bool _loadingRoi;
    // 정지 화면 비트맵이 어느 프레임으로 만들어졌는지 — 단계만 바뀐 갱신에서 재인코딩을 피한다.
    private CameraFrame? _flatFrame;

    public CameraViewModel(CameraService svc, FlatDetectionMonitor flatMonitor, CobotService cobot,
        SequenceRunGate gate, FlatSurfaceCenteringService centering, IServiceScopeFactory scopeFactory)
    {
        _svc = svc;
        _flatMonitor = flatMonitor;
        _cobot = cobot;
        _gate = gate;
        _centering = centering;
        _scopeFactory = scopeFactory;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    [ObservableProperty] private Bitmap? _colorImage;
    [ObservableProperty] private Bitmap? _irImage;
    [ObservableProperty] private Bitmap? _depthImage;

    [ObservableProperty] private bool _busy;
    /// <summary>'레이저 이동·측정' 결과 — 버튼 바로 아래에 표시(상단 알림은 화면 밖이라 놓치기 쉽다).</summary>
    [ObservableProperty] private string? _laserMessage;
    [ObservableProperty] private bool _laserMessageIsError;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    // 캡처
    [ObservableProperty] private string _captureDir = "";
    [ObservableProperty] private int _captureCount;
    [ObservableProperty] private string? _lastCaptureInfo;

    // 깊이 ROI
    [ObservableProperty] private bool _roiEnabled;
    [ObservableProperty] private double _roiXPct;
    [ObservableProperty] private double _roiYPct;
    [ObservableProperty] private double _roiWPct;
    [ObservableProperty] private double _roiHPct;
    [ObservableProperty] private string? _statsText;
    [ObservableProperty] private string _probeText = "— mm";

    // 평탄면 검출 결과(검출 시점 정지 화면) — 시퀀스/평탄 검증이 FlatDetectionMonitor 로 발행.
    [ObservableProperty] private FlatDetectionSnapshot? _flatSnapshot;
    [ObservableProperty] private Bitmap? _flatImage;

    public bool HasFlat => FlatSnapshot is not null;

    public string FlatSourceText => FlatSnapshot is { } s ? $"{s.Source} · {s.DetectedAt:HH:mm:ss}" : "";

    public string FlatCellText
    {
        get
        {
            if (FlatSnapshot is not { } s) return "";
            var a = s.Analysis;
            var best = a.Cells[a.BestIndex];
            var text = $"({best.Gx + 1},{best.Gy + 1}) σ {best.SigmaMm:0.00}mm";
            if (a.SecondIndex >= 0)
            {
                var second = a.Cells[a.SecondIndex];
                text += $" / ({second.Gx + 1},{second.Gy + 1}) σ {second.SigmaMm:0.00}mm";
            }
            return text;
        }
    }

    public string FlatDepthText => FlatSnapshot is { } s
        ? $"{s.Analysis.Best.MeanMm:0}mm · 유효 {s.Analysis.Cells[s.Analysis.BestIndex].ValidRatio:P0}"
        : "";

    public string FlatDeltaText => FlatSnapshot is { DeltaXmm: { } dx, DeltaYmm: { } dy }
        ? $"{dx:+0.0;-0.0}, {dy:+0.0;-0.0}mm"
        : "—";

    public string FlatLaserText => FlatSnapshot is { LaserRxDeg: { } rx, LaserRyDeg: { } ry } s
        ? $"rx {rx:+0.00;-0.00}° · ry {ry:+0.00;-0.00}°" + (s.LaserZmm is { } z ? $" · z {z:0.0}mm" : "")
        : "—";

    public string? FlatLaserChannelsText => FlatSnapshot?.LaserChannelsMm is { Count: >= 3 } c
        ? $"채널 1/2/3: {c[0]:0.00} / {c[1]:0.00} / {c[2]:0.00} mm"
        : null;

    /// <summary>
    /// '레이저 이동·측정' 가능 여부 — '평탄 검증'으로 검출만 한 결과이고 검출 시점 코봇 위치가 기록돼 있을 때.
    /// 시퀀스 결과는 이미 레이저 위치로 이동·측정을 마쳤으므로 대상이 아니다.
    /// </summary>
    public bool CanLaserMeasure => FlatSnapshot is { Stage: FlatDetectionStage.DetectOnly, TcpPoseAtDetect: not null } && !Busy;

    public string FlatStageText => FlatSnapshot?.Stage switch
    {
        FlatDetectionStage.DetectOnly => "검출만",
        FlatDetectionStage.Moving => "이동 중",
        FlatDetectionStage.MoveSkipped => "이동 생략",
        FlatDetectionStage.Moved => "이동 완료",
        FlatDetectionStage.LaserMeasuring => "레이저 측정 중",
        FlatDetectionStage.Done => "정렬 완료",
        FlatDetectionStage.LaserMeasured => "레이저 측정 완료",
        FlatDetectionStage.Failed => "실패",
        _ => "",
    };

    public string? FlatStageMessage => FlatSnapshot?.StageMessage;

    public bool FlatStageBusy => FlatSnapshot?.Stage is FlatDetectionStage.Moving or FlatDetectionStage.LaserMeasuring;
    public bool FlatStageFailed => FlatSnapshot?.Stage is FlatDetectionStage.Failed;
    public bool FlatStageOk => HasFlat && !FlatStageBusy && !FlatStageFailed;

    partial void OnFlatSnapshotChanged(FlatDetectionSnapshot? value)
    {
        foreach (var name in new[]
                 {
                     nameof(HasFlat), nameof(FlatSourceText), nameof(FlatCellText), nameof(FlatDepthText),
                     nameof(FlatDeltaText), nameof(FlatLaserText), nameof(FlatLaserChannelsText),
                     nameof(FlatStageText), nameof(FlatStageMessage),
                     nameof(FlatStageBusy), nameof(FlatStageFailed), nameof(FlatStageOk), nameof(CanLaserMeasure),
                 })
            OnPropertyChanged(name);
    }

    public bool IsConnected => _svc.IsConnected;
    public bool IsStreaming => _svc.IsStreaming;
    public bool IsIrActive => _svc.IsIrActive;
    public string SettingsSummary =>
        $"{_svc.Settings.ColorWidth}×{_svc.Settings.ColorHeight} @{_svc.Settings.ColorFps}fps · 깊이 {_svc.Settings.DepthMinMm}~{_svc.Settings.DepthMaxMm} mm";

    // 정규화 ROI (0~1)
    public double RoiX => Math.Clamp(RoiXPct / 100, 0, 1);
    public double RoiY => Math.Clamp(RoiYPct / 100, 0, 1);
    public double RoiW => Math.Clamp(RoiWPct / 100, 0, 1);
    public double RoiH => Math.Clamp(RoiHPct / 100, 0, 1);

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanLaserMeasure));

    public override async void OnActivated()
    {
        _timer.Start();
        RefreshStatus();
        _flatMonitor.Changed += OnFlatChanged;
        await ApplyFlatAsync(_flatMonitor.Latest);
        await LoadAsync();
    }

    public override void OnDeactivated()
    {
        _timer.Stop();
        _flatMonitor.Changed -= OnFlatChanged;
        ColorImage = IrImage = DepthImage = null;
    }

    // 시퀀스 스레드 등 임의 스레드에서 호출된다 → UI 스레드로 넘긴다.
    private void OnFlatChanged(FlatDetectionSnapshot? snapshot)
        => Dispatcher.UIThread.Post(() => _ = ApplyFlatAsync(snapshot));

    private async Task ApplyFlatAsync(FlatDetectionSnapshot? snapshot)
    {
        if (!ReferenceEquals(snapshot?.Analysis, FlatSnapshot?.Analysis)) LaserMessage = null;   // 새 검출 = 이전 결과 무효
        FlatSnapshot = snapshot;
        var frame = snapshot?.Analysis.Frame;
        if (ReferenceEquals(frame, _flatFrame)) return;
        _flatFrame = frame;
        if (frame is null) { FlatImage = null; return; }
        try
        {
            var jpeg = await Task.Run(() => _svc.EncodeDepthJpeg(frame, _svc.Settings.JpegQuality));
            if (!ReferenceEquals(frame, _flatFrame)) return;   // 인코딩 중 더 새 검출이 들어옴
            using var ms = new MemoryStream(jpeg);
            FlatImage = new Bitmap(ms);
        }
        catch { /* 정지 화면 인코딩 실패는 결과값 표시를 막지 않는다 */ }
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(IsIrActive));
    }

    private async Task LoadAsync()
    {
        _loadingRoi = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            CaptureDir = await param.GetAsync(CaptureDirKey) ?? "";
            RoiEnabled = await param.GetBoolAsync(RoiEnabledKey) ?? false;
            RoiXPct = Math.Round((await param.GetDoubleAsync(RoiXKey) ?? 0) * 100, 1);
            RoiYPct = Math.Round((await param.GetDoubleAsync(RoiYKey) ?? 0) * 100, 1);
            RoiWPct = Math.Round((await param.GetDoubleAsync(RoiWKey) ?? 0) * 100, 1);
            RoiHPct = Math.Round((await param.GetDoubleAsync(RoiHKey) ?? 0) * 100, 1);
        }
        catch { /* 파라미터 로드 실패는 무시(기본값 사용) */ }
        finally { _loadingRoi = false; }
    }

    private async Task PollAsync()
    {
        RefreshStatus();
        if (_polling) return;
        if (!_svc.IsStreaming)
        {
            if (ColorImage is not null) { ColorImage = IrImage = DepthImage = null; }
            return;
        }
        _polling = true;
        try
        {
            var q = _svc.Settings.JpegQuality;
            ColorImage = await DecodeAsync(() => _svc.GetLatestColorJpegAsync(q, _cts.Token)) ?? ColorImage;
            DepthImage = await DecodeAsync(() => _svc.GetLatestDepthJpegAsync(q, _cts.Token)) ?? DepthImage;
            if (_svc.IsIrActive)
                IrImage = await DecodeAsync(() => _svc.GetLatestIrJpegAsync(q, _cts.Token)) ?? IrImage;
        }
        catch { /* 프레임 디코드 오류 무시 — 다음 틱 재시도 */ }
        finally { _polling = false; }
    }

    private static async Task<Bitmap?> DecodeAsync(Func<Task<byte[]?>> getJpeg)
    {
        var bytes = await getJpeg();
        if (bytes is not { Length: > 0 }) return null;
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    // ── 깊이 프로브(hover) — 코드비하인드가 정규화 (u,v) 로 호출 ──
    public void ProbeAt(double u, double v)
    {
        var mm = _svc.GetLatestDepthMmAt(u, v);
        ProbeText = mm is { } m ? $"{m} mm" : "— mm";
    }

    // ── ROI 드래그(코드비하인드) → 정규화 좌표 반영 ──
    public void SetRoiFromDrag(double x, double y, double w, double h)
    {
        if (w < 0.01 || h < 0.01) return;
        RoiXPct = Math.Round(x * 100, 1);
        RoiYPct = Math.Round(y * 100, 1);
        RoiWPct = Math.Round(w * 100, 1);
        RoiHPct = Math.Round(h * 100, 1);
        RoiEnabled = true;
        OnPropertyChanged(nameof(RoiX)); OnPropertyChanged(nameof(RoiY));
        OnPropertyChanged(nameof(RoiW)); OnPropertyChanged(nameof(RoiH));
    }

    partial void OnRoiXPctChanged(double value) => NotifyRoi();
    partial void OnRoiYPctChanged(double value) => NotifyRoi();
    partial void OnRoiWPctChanged(double value) => NotifyRoi();
    partial void OnRoiHPctChanged(double value) => NotifyRoi();
    private void NotifyRoi()
    {
        OnPropertyChanged(nameof(RoiX)); OnPropertyChanged(nameof(RoiY));
        OnPropertyChanged(nameof(RoiW)); OnPropertyChanged(nameof(RoiH));
        // 사용자가 ROI 를 바꾸면 이전 검출 결과는 더 이상 현재 ROI 기준이 아니다.
        if (!_loadingRoi && _flatMonitor.Latest is not null) _flatMonitor.Clear();
    }

    // ── 명령 ──
    [RelayCommand]
    private async Task StartStream()
    {
        Busy = true;
        try { await _svc.StartStreamAsync(_cts.Token); Notify("스트림 시작.", false); }
        catch (Exception ex) { Notify($"시작 실패: {ex.Message}", true); }
        finally { Busy = false; RefreshStatus(); }
    }

    [RelayCommand]
    private async Task StopStream()
    {
        Busy = true;
        try { await _svc.StopStreamAsync(_cts.Token); Notify("스트림 정지.", false); }
        catch (Exception ex) { Notify($"정지 실패: {ex.Message}", true); }
        finally { Busy = false; RefreshStatus(); }
    }

    [RelayCommand]
    private async Task SaveRoi()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await param.SetBoolAsync(RoiEnabledKey, RoiEnabled, "깊이 ROI 사용");
            await param.SetDoubleAsync(RoiXKey, RoiX, "깊이 ROI X(0~1)");
            await param.SetDoubleAsync(RoiYKey, RoiY, "깊이 ROI Y(0~1)");
            await param.SetDoubleAsync(RoiWKey, RoiW, "깊이 ROI W(0~1)");
            await param.SetDoubleAsync(RoiHKey, RoiH, "깊이 ROI H(0~1)");
            Notify("깊이 ROI 저장 완료.", false);
        }
        catch (Exception ex) { Notify($"ROI 저장 실패: {ex.Message}", true); }
    }

    /// <summary>
    /// ROI 깊이 통계 + 평탄 셀 검출(코봇 이동 없음). 시퀀스 ④와 같은 조건(ROI 미사용 시 중앙 30%,
    /// 5×5 그리드, 10회 샘플 중 σ 최소)으로 검출해 결과를 정지 화면으로 남긴다.
    /// </summary>
    [RelayCommand]
    private async Task VerifyPlane()
    {
        var s = _svc.ComputeDepthRoiStats(RoiX, RoiY, RoiW, RoiH);
        StatsText = s is null
            ? "통계 없음 — 스트리밍/ROI/유효 깊이를 확인하세요."
            : $"min {s.MinMm} · avg {s.AvgMm:0} · max {s.MaxMm} mm · 유효 {s.ValidRatio * 100:0}% ({s.ValidCount}/{s.TotalCount})";

        var useRoi = RoiEnabled && RoiW > 0 && RoiH > 0;
        var (x, y, w, h) = useRoi ? (RoiX, RoiY, RoiW, RoiH) : (0.35, 0.35, 0.30, 0.30);

        Busy = true;
        try
        {
            CameraService.DepthGridAnalysis? best = null;
            for (var i = 0; i < FlatSamples; i++)
            {
                var a = _svc.AnalyzeFlatness(x, y, w, h, FlatGridSize);
                if (a is not null && (best is null || a.Best.SigmaMm < best.Best.SigmaMm)) best = a;
                if (i < FlatSamples - 1) await Task.Delay(100, _cts.Token);
            }
            if (best is null)
            {
                Notify("평탄 셀 검출 실패 — ROI 안에 유효 깊이가 부족합니다.", true);
                return;
            }

            double z = best.Best.MeanMm > 0 ? best.Best.MeanMm : FallbackDepthMm;
            var (dx, dy) = _svc.PixelDeltaToMm(best.Best.U - (x + w / 2), best.Best.V - (y + h / 2), z);

            // 검출 시점 코봇 위치 — '레이저 이동·측정'이 같은 자리에서만 Δ 를 적용하도록 기록한다.
            var tool = await GetAlignToolAsync();
            double[]? pose = null;
            if (_cobot.IsConnected)
            {
                try { pose = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, _cts.Token); }
                catch (OperationCanceledException) { throw; }
                catch { /* 위치를 못 읽으면 레이저 이동만 비활성 */ }
            }

            var note = useRoi ? "코봇 이동 없음" : "코봇 이동 없음 · ROI 미사용 → 중앙 기본 ROI";
            if (pose is null) note += " · 코봇 위치 미기록(레이저 이동 불가)";
            _flatMonitor.Publish(new FlatDetectionSnapshot(
                "평탄 검증", DateTime.Now, best, x, y, w, h, dx, dy, FlatDetectionStage.DetectOnly, note,
                TcpPoseAtDetect: pose, Tool: tool));
        }
        catch (OperationCanceledException) { }
        finally { Busy = false; }
    }

    /// <summary>
    /// '평탄 검증'으로 찾은 평탄 셀 위로 레이저 3점 중심을 옮기고 레이저를 측정한다(코봇 실제 이동, 틸트 보정 없음).
    /// 시퀀스와 동시에 코봇을 움직이지 않도록 시퀀스 전역 잠금을 잡는다.
    /// </summary>
    [RelayCommand]
    private async Task LaserMeasure()
    {
        if (FlatSnapshot is not { } snap || !CanLaserMeasure) return;
        if (!_gate.TryEnter())
        {
            NotifyLaser("시퀀스 실행 중에는 레이저 이동을 할 수 없습니다.", true);
            return;
        }
        Busy = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            var ax = await param.GetDoubleAsync(AlignImageXAxisKey);
            var ay = await param.GetDoubleAsync(AlignImageYAxisKey);
            var shiftY = await param.GetDoubleAsync(WeldSequenceSupport.CameraToLaserShiftYKey) ?? -75.0;
            var axisX = ax is >= 0 and <= 5 ? (ToolAxisDir)(int)ax.Value : ToolAxisDir.PlusX;
            var axisY = ay is >= 0 and <= 5 ? (ToolAxisDir)(int)ay.Value : ToolAxisDir.PlusY;

            NotifyLaser("레이저를 평탄 셀 위로 이동 중…", false);
            var r = await _centering.MoveLaserToFlatAsync(snap, new LaserToFlatOptions
            {
                ImageXAxis = axisX, ImageYAxis = axisY,
                CameraToLaserShiftYmm = shiftY,
                Tool = snap.Tool ?? 1,
                Velocity = LaserMoveVelocity,
            }, progress: null, _cts.Token);
            NotifyLaser(r.Success ? r.Message : $"레이저 이동·측정 실패: {r.Message}", !r.Success);
        }
        catch (Exception ex) { NotifyLaser($"레이저 이동·측정 오류: {ex.Message}", true); }
        finally
        {
            Busy = false;
            _gate.Exit();
        }
    }

    private async Task<int> GetAlignToolAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            var t = await param.GetDoubleAsync(AlignToolKey);
            return t is >= 0 and <= 14 ? (int)t.Value : 1;
        }
        catch { return 1; }
    }

    [RelayCommand]
    private async Task Capture()
    {
        if (string.IsNullOrWhiteSpace(CaptureDir)) { Notify("먼저 저장 폴더를 선택하세요.", true); return; }
        Busy = true;
        try
        {
            var r = await _svc.SaveCaptureAsync(CaptureDir, _cts.Token);
            CaptureCount++;
            LastCaptureInfo = $"{r.Timestamp} · {r.Files.Count}개 파일";
            Notify($"캡처 저장: {r.Dir}", false);
        }
        catch (Exception ex) { Notify($"캡처 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    /// <summary>코드비하인드(StorageProvider)가 폴더를 고른 뒤 호출 — 경로 저장.</summary>
    public async Task SetCaptureDirAsync(string dir)
    {
        CaptureDir = dir;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await param.SetAsync(CaptureDirKey, dir, "학습 데이터 캡처 저장 폴더");
        }
        catch { /* 무시 */ }
        Notify($"저장 폴더 지정: {dir}", false);
    }

    private void Notify(string msg, bool error) { Message = msg; IsError = error; }

    private void NotifyLaser(string msg, bool error)
    {
        Notify(msg, error);
        LaserMessage = msg; LaserMessageIsError = error;
    }

    public override void Dispose() { base.Dispose(); _cts.Cancel(); _cts.Dispose(); }
}
