using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 용접라인 추적(수동 2-shot). 기존 WeldTrackingPanel.razor 이식(핵심).
/// ROI 에디터(weld/peak) + 1회 검출/피크 캡처/각도 계산 + 검출 오버레이. 검출은 카메라·OpenCV/ONNX 의존
/// (Windows 전용, macOS 는 Noop). 후속: 검출 파라미터 상세 튜닝, 스케일 2점 보정, 학습/라벨 연동.
/// </summary>
public sealed partial class WeldTrackingViewModel : ViewModelBase
{
    private readonly WeldTrackingService _svc;
    private readonly CameraService _cam;
    private readonly DispatcherTimer _timer;
    private bool _polling;

    public WeldTrackingViewModel(WeldTrackingService svc, CameraService cam)
    {
        _svc = svc;
        _cam = cam;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    [ObservableProperty] private Bitmap? _editorImage;
    [ObservableProperty] private Bitmap? _overlayImage;
    [ObservableProperty] private Bitmap? _peak1Image;
    [ObservableProperty] private Bitmap? _peak2Image;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;

    [ObservableProperty] private int _methodIndex;      // 0 DL, 1 Param
    [ObservableProperty] private int _drawKindIndex;    // 0 weld, 1 peak
    private string DrawKind => DrawKindIndex == 1 ? "peak" : "weld";
    [ObservableProperty] private int _mx, _my, _mw, _mh;
    [ObservableProperty] private string _loadName = "";
    [ObservableProperty] private double _pitch;

    public bool IsStreaming => _cam.IsStreaming;
    public bool DetectorAvailable => _svc.DetectorAvailable;
    public string MethodText => _svc.Method == WeldDetectionMethod.Dl ? "DL 모델" : "파라미터(CV)";
    public string StateText => _svc.State.ToString();
    public string ProfileName { get => _svc.ProfileName; set { _svc.ProfileName = value; OnPropertyChanged(); } }
    public IReadOnlyList<string> Profiles => _svc.ListProfiles();

    public RoiRect? WeldRoi => _svc.WeldRoi;
    public RoiRect? PeakRoi => _svc.PeakRoi;
    public string WeldRoiText => RoiText(_svc.WeldRoi);
    public string PeakRoiText => RoiText(_svc.PeakRoi);

    // 에디터 프레임 픽셀 크기(드래그 정규화→픽셀 변환용).
    public int FrameW => _svc.Params.Mode == WeldImageMode.Ir ? _cam.Settings.IrWidth : _cam.Settings.ColorWidth;
    public int FrameH => _svc.Params.Mode == WeldImageMode.Ir ? _cam.Settings.IrHeight : _cam.Settings.ColorHeight;

    public string M1Text => MeasText(_svc.M1);
    public string M2Text => MeasText(_svc.M2);
    public string AngleText => _svc.Angle is { } a ? $"{a.ThetaDeg:0.00}° ({a.ThetaRad:0.0000} rad)" : "—";
    public bool CanComputeAngle => !Busy && _svc.M1 is not null && _svc.M2 is not null && _svc.ScaleAvailable;

    public override void OnActivated()
    {
        Pitch = _svc.Pitch;
        MethodIndex = _svc.Method == WeldDetectionMethod.Param ? 1 : 0;
        _timer.Start();
        RefreshStatus();
        RefreshOverlays();
    }

    public override void OnDeactivated()
    {
        _timer.Stop();
        EditorImage = OverlayImage = Peak1Image = Peak2Image = null;
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(DetectorAvailable));
        OnPropertyChanged(nameof(MethodText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(WeldRoiText));
        OnPropertyChanged(nameof(PeakRoiText));
        OnPropertyChanged(nameof(WeldRoi));
        OnPropertyChanged(nameof(PeakRoi));
        OnPropertyChanged(nameof(M1Text));
        OnPropertyChanged(nameof(M2Text));
        OnPropertyChanged(nameof(AngleText));
        ComputeAngleCommand.NotifyCanExecuteChanged();
    }

    private async Task PollAsync()
    {
        RefreshStatus();
        if (_polling || !_cam.IsStreaming) { if (!_cam.IsStreaming) EditorImage = null; return; }
        _polling = true;
        try
        {
            var ir = _svc.Params.Mode == WeldImageMode.Ir;
            var q = _cam.Settings.JpegQuality;
            EditorImage = await Decode(() => ir ? _cam.GetLatestIrJpegAsync(q) : _cam.GetLatestColorJpegAsync(q)) ?? EditorImage;
        }
        catch { /* 무시 */ }
        finally { _polling = false; }
    }

    private void RefreshOverlays()
    {
        OverlayImage = FromBytes(_svc.LastOverlay);
        Peak1Image = FromBytes(_svc.Peak1Overlay);
        Peak2Image = FromBytes(_svc.Peak2Overlay);
    }

    private static Bitmap? FromBytes(byte[]? b)
    {
        if (b is not { Length: > 0 }) return null;
        using var ms = new MemoryStream(b);
        return new Bitmap(ms);
    }

    private static async Task<Bitmap?> Decode(Func<Task<byte[]?>> get)
    {
        var b = await get();
        return FromBytes(b);
    }

    partial void OnMethodIndexChanged(int value)
    {
        _svc.Method = value == 1 ? WeldDetectionMethod.Param : WeldDetectionMethod.Dl;
        RefreshStatus();
    }

    partial void OnPitchChanged(double value) => _svc.Pitch = value;

    // ── ROI ──
    /// <summary>에디터 드래그(정규화) → 현재 종류 ROI(픽셀)로 반영.</summary>
    public void SetRoiFromDrag(double u, double v, double w, double h)
    {
        if (w < 0.01 || h < 0.01) return;
        var r = new RoiRect((int)(u * FrameW), (int)(v * FrameH), (int)(w * FrameW), (int)(h * FrameH));
        Mx = r.X; My = r.Y; Mw = r.Width; Mh = r.Height;
        if (DrawKind == "peak") _svc.SetPeakRoi(r); else _svc.SetWeldRoi(r);
        RefreshStatus();
    }

    [RelayCommand]
    private void ApplyManualRoi()
    {
        var r = new RoiRect(Mx, My, Mw, Mh);
        if (DrawKind == "peak") _svc.SetPeakRoi(r); else _svc.SetWeldRoi(r);
        RefreshStatus();
    }

    [RelayCommand]
    private void ResetRoi() { _svc.ResetRoi(); RefreshStatus(); }

    [RelayCommand]
    private void SaveProfile()
    {
        _svc.SaveProfile(string.IsNullOrWhiteSpace(_svc.ProfileName) ? "default" : _svc.ProfileName);
        OnPropertyChanged(nameof(Profiles));
        Message = $"프로파일 저장: {_svc.ProfileName}";
    }

    [RelayCommand]
    private void LoadProfile()
    {
        if (string.IsNullOrEmpty(LoadName)) return;
        _svc.LoadProfile(LoadName);
        var r = DrawKind == "peak" ? _svc.PeakRoi : _svc.WeldRoi;
        if (r is not null) { Mx = r.X; My = r.Y; Mw = r.Width; Mh = r.Height; }
        RefreshStatus();
        Message = $"프로파일 로드: {LoadName}";
    }

    // ── 검출/측정 ──
    [RelayCommand] private Task FindPeak() => Run(() => _svc.FindPeak());
    [RelayCommand] private Task DetectOnce() => Run(() => _svc.DetectOnce());
    [RelayCommand] private Task Capture(string id) => Run(() => _svc.CapturePeak(int.Parse(id)));

    [RelayCommand(CanExecute = nameof(CanComputeAngle))]
    private void ComputeAngle() { _svc.ComputeAngle(); RefreshStatus(); }

    [RelayCommand]
    private void ResetMeasurements() { _svc.ResetMeasurements(); RefreshStatus(); RefreshOverlays(); }

    private async Task Run(Action action)
    {
        if (Busy) return;
        Busy = true;
        try { await Task.Run(action); Message = _svc.Message; }
        catch (Exception ex) { Message = ex.Message; }
        finally { Busy = false; RefreshStatus(); RefreshOverlays(); }
    }

    private static string RoiText(RoiRect? r) => r is null ? "미지정" : $"({r.X},{r.Y},{r.Width}×{r.Height})";

    private string MeasText(PeakMeasurement? m)
    {
        if (m is null) return "—";
        var peak = m.Peak is { Found: true } ? $", peak@{m.Peak.ProgressPos:0}px/{m.Peak.DepthValue}mm" : "";
        return $"{_svc.DMm(m):0.0}mm ({m.DPixel:0.0}px) (conf {m.Confidence:P0}{peak})";
    }
}
