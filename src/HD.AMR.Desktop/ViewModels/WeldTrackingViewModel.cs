using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Vision;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 용접라인 추적(수동 2-shot). 기존 WeldTrackingPanel.razor 이식(핵심).
/// ROI 에디터(Bead/Peak — 시퀀스와 같은 Camera.Bead/Peak.Roi 파라미터) + 진행축 선택 + 1회 검출/피크 캡처/
/// 각도 계산 + 검출 오버레이 + 학습 데이터 캡처(Camera 페이지와 같은 저장 폴더). 검출은 카메라·OpenCV/ONNX 의존
/// (Windows 전용, macOS 는 Noop). 후속: 검출 파라미터 상세 튜닝, 스케일 2점 보정, 학습/라벨 연동.
/// </summary>
public sealed partial class WeldTrackingViewModel : ViewModelBase
{
    private readonly WeldTrackingService _svc;
    private readonly CameraService _cam;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DispatcherTimer _timer;
    private bool _polling;

    private readonly WeldTrainingService _train;

    public WeldTrackingViewModel(WeldTrackingService svc, CameraService cam, IServiceScopeFactory scopeFactory,
        WeldTrainingService train)
    {
        _svc = svc;
        _cam = cam;
        _scopeFactory = scopeFactory;
        _train = train;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    [ObservableProperty] private Bitmap? _editorImage;
    [ObservableProperty] private Bitmap? _overlayImage;
    [ObservableProperty] private Bitmap? _peak1Image;
    [ObservableProperty] private Bitmap? _peak2Image;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;

    [ObservableProperty] private int _methodIndex;      // 0 DL, 1 Param, 2 Ridge, 3 U-Net
    [ObservableProperty] private int _drawKindIndex;    // 0 weld(=Bead ROI), 1 peak

    /// <summary>
    /// 진행축(0 가로, 1 세로) — 서비스 값을 직접 읽고 쓴다. 시퀀스가 실행 중 검사방향으로 바꾸므로
    /// 백킹 필드를 두지 않고 폴링(RefreshStatus)마다 다시 읽어 화면과 실제 값을 맞춘다.
    /// </summary>
    public int ProgressAxisIndex
    {
        get => _svc.Params.ProgressAxis == WeldProgressAxis.Vertical ? 1 : 0;
        set
        {
            var axis = value == 1 ? WeldProgressAxis.Vertical : WeldProgressAxis.Horizontal;
            if (_svc.Params.ProgressAxis == axis) return;
            _svc.Params.ProgressAxis = axis;
            OnPropertyChanged();
        }
    }
    /// <summary>
    /// 검출 입력 영상(0 IR, 1 RGB Grayscale, 2 RGB HSV(S)) — 진행축처럼 서비스 값을 직접 읽고 쓴다.
    /// 바꾸면 파라미터(Weld.ImageMode)에 저장되어 재시작 후에도 유지된다. DL 모델은 이 값에 맞춰
    /// weld_seg_{ir|rgb}.onnx 를 자동 선택(DL 모델 '자동')하므로, 학습한 모델의 모달리티와 맞춰야 한다.
    /// </summary>
    public int ImageModeIndex
    {
        get => _svc.Params.Mode switch { WeldImageMode.RgbGrayscale => 1, WeldImageMode.RgbHsv => 2, _ => 0 };
        set
        {
            var mode = value switch { 1 => WeldImageMode.RgbGrayscale, 2 => WeldImageMode.RgbHsv, _ => WeldImageMode.Ir };
            if (_svc.Params.Mode == mode) return;
            _svc.Params.Mode = mode;
            _svc.SaveMode();
            EditorImage = null; // 다른 카메라 스트림(IR↔컬러)으로 전환 — 이전 영상 잔상 제거
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRgbMode));
            _ = LoadDlModelsAsync();   // DL 모델 드롭다운 '자동 (weld_seg_{ir|rgb}.onnx)' 표시 갱신
        }
    }

    /// <summary>RGB 계열 입력이면 true — RGB 시퀀스 동작(IR 기준)·ROI 지정 주의 안내 표시용.</summary>
    public bool IsRgbMode => _svc.Params.Mode != WeldImageMode.Ir;

    /// <summary>DL conf/mask 임계값 — 진행축과 같이 서비스 값을 직접 읽고 쓴다(설정 시 파라미터 저장).</summary>
    public double DlConfidence
    {
        get => _svc.Params.DlConfidence;
        set
        {
            _svc.SetDlThresholds(value, _svc.Params.DlMaskThreshold);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DlConfidenceText));
        }
    }

    public double DlMaskThreshold
    {
        get => _svc.Params.DlMaskThreshold;
        set
        {
            _svc.SetDlThresholds(_svc.Params.DlConfidence, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DlMaskThresholdText));
        }
    }

    public string DlConfidenceText => $"conf {_svc.Params.DlConfidence:0.00}";
    public string DlMaskThresholdText => $"mask {_svc.Params.DlMaskThreshold:0.00}";
    public double DlConfidenceMin => WeldTrackingService.DlConfidenceMin;
    public double DlConfidenceMax => WeldTrackingService.DlConfidenceMax;
    public double DlMaskThresholdMin => WeldTrackingService.DlMaskThresholdMin;
    public double DlMaskThresholdMax => WeldTrackingService.DlMaskThresholdMax;

    private string DrawKind => DrawKindIndex == 1 ? "peak" : "weld";
    [ObservableProperty] private int _mx, _my, _mw, _mh;
    [ObservableProperty] private string _loadName = "";
    [ObservableProperty] private double _pitch;

    public bool IsStreaming => _cam.IsStreaming;
    public bool DetectorAvailable => _svc.DetectorAvailable;
    public string MethodText => _svc.Method switch
    {
        WeldDetectionMethod.Dl => "DL 모델",
        WeldDetectionMethod.Ridge => "Ridge 필터",
        WeldDetectionMethod.Unet => "U-Net",
        _ => "파라미터(CV)",
    };
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

    // 학습 데이터 캡처 — 저장 폴더는 Camera 페이지와 공유(Camera.Capture.Dir), 누적 횟수는 페이지별.
    [ObservableProperty] private string _captureDir = "";
    [ObservableProperty] private int _captureCount;
    [ObservableProperty] private string? _lastCaptureInfo;

    public override async void OnActivated()
    {
        Pitch = _svc.Pitch;
        MethodIndex = _svc.Method switch { WeldDetectionMethod.Param => 1, WeldDetectionMethod.Ridge => 2, WeldDetectionMethod.Unet => 3, _ => 0 };
        RefreshRidge();
        await LoadDlModelsAsync();
        _timer.Start();
        RefreshStatus();
        RefreshOverlays();
        // Camera 페이지에서 폴더를 바꿨을 수 있으므로 활성화마다 다시 읽는다.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            CaptureDir = await scope.ServiceProvider.GetRequiredService<ParameterService>()
                .GetAsync(CameraViewModel.CaptureDirKey) ?? "";
        }
        catch { /* 파라미터 로드 실패는 무시 */ }
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
        OnPropertyChanged(nameof(ProgressAxisIndex));
        OnPropertyChanged(nameof(ImageModeIndex));   // 시작 시 비동기로 복원되는 저장값 반영
        OnPropertyChanged(nameof(IsRgbMode));
        OnPropertyChanged(nameof(DlConfidence));
        OnPropertyChanged(nameof(DlMaskThreshold));
        OnPropertyChanged(nameof(DlConfidenceText));
        OnPropertyChanged(nameof(DlMaskThresholdText));
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
        var method = value switch { 1 => WeldDetectionMethod.Param, 2 => WeldDetectionMethod.Ridge, 3 => WeldDetectionMethod.Unet, _ => WeldDetectionMethod.Dl };
        OnPropertyChanged(nameof(IsRidge));
        OnPropertyChanged(nameof(IsDl));
        if (_svc.Method == method) { RefreshStatus(); return; }
        _svc.Method = method;
        _ = _svc.SaveDetectionSettingsAsync();
        RefreshStatus();
    }

    // ── DL 모델 선택 — 학습 models 폴더의 .onnx 목록. 고르면 Weld.Dl.ModelPath 에 저장(시퀀스·재시작 공유) ──
    /// <summary>드롭다운 항목. Path 빈값 = 자동(weld_seg_{ir|rgb}.onnx). Exists=false 는 저장값이 가리키는 파일이 없음.</summary>
    public sealed record DlModelOption(string Path, string Label, bool Exists = true)
    {
        public override string ToString() => Label;
    }

    public bool IsDl => MethodIndex == 0;
    public System.Collections.ObjectModel.ObservableCollection<DlModelOption> DlModels { get; } = new();
    [ObservableProperty] private DlModelOption? _selectedDlModel;
    private bool _loadingModels;

    /// <summary>모델 목록을 다시 읽고 현재 저장값을 선택한다. 저장값 파일이 없으면 '⚠ 파일 없음' 항목으로 보여준다.</summary>
    [RelayCommand]
    private async Task LoadDlModelsAsync()
    {
        List<ModelInfo> models;
        try { models = await _train.ListModelsAsync(); }
        catch { models = new(); }

        _loadingModels = true;
        try
        {
            var autoName = $"weld_seg_{(_svc.Params.Mode == WeldImageMode.Ir ? "ir" : "rgb")}.onnx";
            DlModels.Clear();
            DlModels.Add(new DlModelOption("", $"자동 ({autoName})"));
            foreach (var m in models) DlModels.Add(new DlModelOption(m.Path, m.Name));

            var current = _svc.Params.DlModelPath?.Trim().Trim('"').Trim() ?? "";
            DlModelOption? match = current.Length == 0
                ? DlModels[0]
                : DlModels.FirstOrDefault(o => o.Path.Length > 0 &&
                    (string.Equals(o.Path, current, StringComparison.OrdinalIgnoreCase) ||
                     (!System.IO.Path.IsPathFullyQualified(current) &&
                      string.Equals(System.IO.Path.GetFileName(o.Path), current, StringComparison.OrdinalIgnoreCase))));
            if (match is null)
            {
                match = new DlModelOption(current, $"⚠ 파일 없음: {System.IO.Path.GetFileName(current)}", Exists: false);
                DlModels.Insert(1, match);
            }
            SelectedDlModel = match;
        }
        finally { _loadingModels = false; }
    }

    partial void OnSelectedDlModelChanged(DlModelOption? value)
    {
        if (_loadingModels || value is null || !value.Exists) return;
        if (string.Equals(_svc.Params.DlModelPath ?? "", value.Path, StringComparison.OrdinalIgnoreCase)) return;
        _svc.Params.DlModelPath = value.Path;
        _ = SaveDlModelAsync(value);
    }

    private async Task SaveDlModelAsync(DlModelOption opt)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ParameterService>().SetAsync(
                WeldDetectionSettingsStore.DlModelPathKey, opt.Path,
                WeldDetectionSettingsStore.Describe(WeldDetectionSettingsStore.DlModelPathKey));
            Message = $"DL 모델 저장: {opt.Label}";
        }
        catch (Exception ex) { Message = $"DL 모델 저장 실패: {ex.Message}"; }
        // 저장 실패로 남은 '파일 없음' 항목 정리 — 다음 목록 갱신 때 반영된다.
        var stale = DlModels.Where(o => !o.Exists).ToList();
        foreach (var s in stale) DlModels.Remove(s);
    }

    // ── Ridge 검출 파라미터 — 서비스 Params 를 직접 읽고 쓰며, 바꿀 때마다 DB 저장(재시작 후 복원·시퀀스 공유) ──
    public bool IsRidge => MethodIndex == 2;

    public double RidgeSigmaMin { get => _svc.Params.RidgeSigmaMin; set => SetRidge(() => _svc.Params.RidgeSigmaMin = Math.Clamp(value, 0.5, 20)); }
    public double RidgeSigmaMax { get => _svc.Params.RidgeSigmaMax; set => SetRidge(() => _svc.Params.RidgeSigmaMax = Math.Clamp(value, 0.5, 20)); }
    public int RidgeScales { get => _svc.Params.RidgeScales; set => SetRidge(() => _svc.Params.RidgeScales = Math.Clamp(value, 1, 6)); }
    public int RidgePolarityIndex
    {
        get => (int)_svc.Params.RidgePolarity;
        set => SetRidge(() => _svc.Params.RidgePolarity = Enum.IsDefined((WeldRidgePolarity)value) ? (WeldRidgePolarity)value : WeldRidgePolarity.Both);
    }
    public double RidgeMaxAngleDeg { get => _svc.Params.RidgeMaxAngleDeg; set => SetRidge(() => _svc.Params.RidgeMaxAngleDeg = Math.Clamp(value, 0, 90)); }
    public double RidgeThresholdK { get => _svc.Params.RidgeThresholdK; set => SetRidge(() => _svc.Params.RidgeThresholdK = Math.Clamp(value, 0, 50)); }
    public double RidgeRelative { get => _svc.Params.RidgeRelative; set => SetRidge(() => _svc.Params.RidgeRelative = Math.Clamp(value, 0, 1)); }

    private void SetRidge(Action apply)
    {
        apply();
        _ = _svc.SaveDetectionSettingsAsync();
        RefreshRidge();
    }

    private void RefreshRidge()
    {
        OnPropertyChanged(nameof(RidgeSigmaMin));
        OnPropertyChanged(nameof(RidgeSigmaMax));
        OnPropertyChanged(nameof(RidgeScales));
        OnPropertyChanged(nameof(RidgePolarityIndex));
        OnPropertyChanged(nameof(RidgeMaxAngleDeg));
        OnPropertyChanged(nameof(RidgeThresholdK));
        OnPropertyChanged(nameof(RidgeRelative));
    }

    /// <summary>Ridge 파라미터를 코드 기본값으로 되돌리고 저장.</summary>
    [RelayCommand]
    private void ResetRidge()
    {
        var d = new WeldDetectionParams();
        SetRidge(() =>
        {
            _svc.Params.RidgeSigmaMin = d.RidgeSigmaMin;
            _svc.Params.RidgeSigmaMax = d.RidgeSigmaMax;
            _svc.Params.RidgeScales = d.RidgeScales;
            _svc.Params.RidgePolarity = d.RidgePolarity;
            _svc.Params.RidgeMaxAngleDeg = d.RidgeMaxAngleDeg;
            _svc.Params.RidgeThresholdK = d.RidgeThresholdK;
            _svc.Params.RidgeRelative = d.RidgeRelative;
        });
        Message = "Ridge 파라미터 기본값 복원";
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

    // ── 학습 데이터 캡처 ──
    [RelayCommand]
    private async Task TrainingCapture()
    {
        if (string.IsNullOrWhiteSpace(CaptureDir)) { Message = "먼저 저장 폴더를 선택하세요."; return; }
        if (Busy) return;
        Busy = true;
        try
        {
            var r = await _cam.SaveCaptureAsync(CaptureDir);
            CaptureCount++;
            LastCaptureInfo = $"{r.Timestamp} · {r.Files.Count}개 파일";
            Message = $"캡처 저장: {r.Dir}";
        }
        catch (Exception ex) { Message = $"캡처 실패: {ex.Message}"; }
        finally { Busy = false; }
    }

    /// <summary>코드비하인드(StorageProvider)가 폴더를 고른 뒤 호출 — Camera 페이지와 같은 키에 저장.</summary>
    public async Task SetCaptureDirAsync(string dir)
    {
        CaptureDir = dir;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ParameterService>()
                .SetAsync(CameraViewModel.CaptureDirKey, dir, "학습 데이터 캡처 저장 폴더");
        }
        catch { /* 무시 */ }
        Message = $"저장 폴더 지정: {dir}";
    }

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
