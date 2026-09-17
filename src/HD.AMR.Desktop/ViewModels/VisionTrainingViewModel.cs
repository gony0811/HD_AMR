using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service.Vision;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 비전 학습(YOLOv8-seg · CPU 파이프라인). 기존 VisionTraining.razor 이식 —
/// ① 데이터셋 변환/회전 증강, ② 학습, ③ ONNX 내보내기·모델 목록, ④ 네이티브 ONNX 추론(캡처/업로드).
/// 학습 프로세스는 싱글톤 WeldTrainingService 가 백그라운드로 진행하며 1초 폴링으로 상태/로그를 갱신한다.
/// </summary>
public sealed partial class VisionTrainingViewModel : ViewModelBase
{
    private readonly WeldTrainingService _train;
    private readonly OnnxBeadSegmentationService _seg;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INavigationService _nav;
    private readonly DispatcherTimer _timer;
    private TrainingPhase _lastPhase = TrainingPhase.Idle;

    public ObservableCollection<ModelInfo> Models { get; } = new();
    public ObservableCollection<CaptureEntry> Entries { get; } = new();

    [ObservableProperty] private TrainingPaths? _paths;
    [ObservableProperty] private PythonInfo? _py;
    [ObservableProperty] private string _pythonExe = "python";
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private int _modalityIndex;         // ① 0 rgb, 1 ir
    [ObservableProperty] private int _rgbMasks, _irMasks;
    [ObservableProperty] private int _epochs = 300, _imgsz = 640, _batch = 4;
    [ObservableProperty] private bool _minimalAug = true, _brightAug;
    [ObservableProperty] private int _baseModelIndex;        // 0 n, 1 s
    [ObservableProperty] private int _opset = 12, _exportImgsz = 640;
    [ObservableProperty] private int _exportModalityIndex = 1;   // 0 rgb, 1 ir
    [ObservableProperty] private int _rotDirIndex;           // 0 cw, 1 ccw, 2 both
    [ObservableProperty] private string? _convertText, _rotText;
    [ObservableProperty] private bool _working;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    // ④ 추론
    [ObservableProperty] private ModelInfo? _selModel;
    [ObservableProperty] private CaptureEntry? _inferEntry;
    [ObservableProperty] private int _inferModalityIndex = 1;
    [ObservableProperty] private double _conf = 0.25, _maskThr = 0.50;
    [ObservableProperty] private string? _inferText;
    [ObservableProperty] private bool _inferOk;
    [ObservableProperty] private Bitmap? _overlayImage;
    [ObservableProperty] private bool _inferring;
    [ObservableProperty] private string? _uploadName;
    [ObservableProperty] private int _addModalityIndex = 1;
    private byte[]? _uploadBytes;

    // 상태/로그
    [ObservableProperty] private string _phaseText = "Idle";
    [ObservableProperty] private int _phaseKind;             // 0 idle,1 running,2 done,3 error
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private double _progressPct;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _logText = "";

    public bool HasPaths => Paths is not null;
    public bool HasUpload => _uploadBytes is not null;
    public bool NoModels => Models.Count == 0;
    public string PyDetail => Py?.Detail ?? "";
    public int PyKind => Py is null ? 0 : Py.UltralyticsOk ? 1 : Py.PythonOk ? 2 : 3;
    public bool PyReady => Py is { UltralyticsOk: true };
    public bool NeedInstall => Py is { UltralyticsOk: false };
    public string InstallHint => Py is null ? "" : $"설치: {Py.Exe} -m pip install ultralytics onnx";
    public string ExportButtonText => $"best.pt → weld_seg_{ExportModality}.onnx";
    private string Modality => ModalityIndex == 1 ? "ir" : "rgb";
    private string ExportModality => ExportModalityIndex == 1 ? "ir" : "rgb";

    public VisionTrainingViewModel(WeldTrainingService train, OnnxBeadSegmentationService seg,
        IServiceScopeFactory scopeFactory, INavigationService nav)
    {
        _train = train; _seg = seg; _scopeFactory = scopeFactory; _nav = nav;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public override async void OnActivated()
    {
        Paths = await _train.GetPathsAsync();
        OnPropertyChanged(nameof(HasPaths));
        if (Paths is null) return;
        await RefreshCountsAsync();
        await RefreshModelsAsync();
        _timer.Start();
        await PollAsync();
        _ = DetectPythonAsync();
    }

    public override void OnDeactivated() => _timer.Stop();

    partial void OnPyChanged(PythonInfo? value)
    {
        OnPropertyChanged(nameof(PyDetail)); OnPropertyChanged(nameof(PyKind));
        OnPropertyChanged(nameof(PyReady)); OnPropertyChanged(nameof(NeedInstall)); OnPropertyChanged(nameof(InstallHint));
    }
    partial void OnExportModalityIndexChanged(int value) => OnPropertyChanged(nameof(ExportButtonText));

    private async Task PollAsync()
    {
        if (_lastPhase != TrainingPhase.Done && _train.Phase == TrainingPhase.Done) await RefreshModelsAsync();
        _lastPhase = _train.Phase;

        PhaseText = _train.Phase.ToString();
        PhaseKind = _train.Phase switch
        {
            TrainingPhase.Training or TrainingPhase.Exporting or TrainingPhase.Converting => 1,
            TrainingPhase.Done => 2, TrainingPhase.Error => 3, _ => 0,
        };
        StatusText = _train.StatusText;
        IsBusy = _train.IsBusy;

        int cur = _train.TrainCurrentEpoch, tot = _train.TrainTotalEpochs;
        ShowProgress = tot > 0 && (_train.IsBusy || cur > 0);
        if (ShowProgress)
        {
            ProgressPct = 100.0 * cur / tot;
            ProgressText = $"{cur}/{tot} epoch · {ProgressPct:0.0}%";
            EtaText = Eta(cur, tot);
        }
        LogText = string.Join('\n', _train.LogTail(500));
    }

    // 경과시간 ÷ 완료 epoch 로 epoch당 평균을 구해 남은 epoch에 외삽 → ETA.
    private string Eta(int cur, int tot)
    {
        var start = _train.TrainStartUtc;
        if (start is null || tot <= 0) return "";
        var end = _train.TrainEndUtc ?? DateTime.UtcNow;
        var elapsed = (end - start.Value).TotalSeconds;
        if (_train.Phase != TrainingPhase.Training) return $"경과 {Dur(elapsed)} · 종료";
        if (cur <= 0) return $"예상 시간 계산 중… (경과 {Dur(elapsed)})";
        var remain = elapsed / cur * Math.Max(0, tot - cur);
        return $"약 {Dur(remain)} 남음 · 경과 {Dur(elapsed)}";
    }
    private static string Dur(double s) =>
        s < 60 ? $"{Math.Round(s)}초" : s < 3600 ? $"{Math.Floor(s / 60)}분 {Math.Round(s % 60)}초" : $"{Math.Floor(s / 3600)}시간 {Math.Round(s % 3600 / 60)}분";

    private async Task DetectPythonAsync()
    {
        Py = await _train.DetectPythonAsync();
        PythonExe = Py.Exe;
    }

    [RelayCommand]
    private async Task SavePython()
    {
        try { await _train.SetPythonAsync(PythonExe); await DetectPythonAsync(); }
        catch (Exception ex) { Notify(ex.Message, true); }
    }

    private async Task RefreshCountsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var list = await scope.ServiceProvider.GetRequiredService<LabelDataService>().ListAsync();
            Entries.Clear();
            foreach (var e in list) Entries.Add(e);
            RgbMasks = list.Count(e => e.RgbMask);
            IrMasks = list.Count(e => e.IrMask);
            InferEntry ??= Entries.FirstOrDefault();
        }
        catch { /* 무시 */ }
    }

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        try
        {
            var sel = SelModel?.Path;
            var list = await _train.ListModelsAsync();
            Models.Clear();
            foreach (var m in list) Models.Add(m);
            SelModel = Models.FirstOrDefault(m => m.Path == sel) ?? Models.FirstOrDefault();
            OnPropertyChanged(nameof(NoModels));
        }
        catch (Exception ex) { Notify(ex.Message, true); }
    }

    [RelayCommand]
    private async Task Convert()
    {
        Working = true; Message = null;
        try
        {
            await RefreshCountsAsync();
            var r = await _train.ConvertAsync(Modality);
            ConvertText = $"변환 완료 — 사용 {r.Used}건 / 건너뜀 {r.Skipped}건 / 폴리곤 {r.Instances}개\n{r.DataYaml}";
            ExportModalityIndex = ModalityIndex;
            Notify($"데이터셋 변환 완료 — 사용 {r.Used}건.", false);
        }
        catch (Exception ex) { Notify($"변환 실패: {ex.Message}", true); }
        finally { Working = false; }
    }

    [RelayCommand]
    private async Task GenerateRotated()
    {
        Working = true; Message = null;
        try
        {
            var dir = RotDirIndex switch { 1 => "ccw", 2 => "both", _ => "cw" };
            var r = await _train.GenerateRotatedCopiesAsync(dir);
            await RefreshCountsAsync();
            RotText = $"회전본 생성 완료 — {r.Created}건 생성 (건너뜀 {r.Skipped}건). 이제 데이터셋 변환을 다시 실행하세요.";
            Notify($"90° 회전본 {r.Created}건 생성 완료.", false);
        }
        catch (Exception ex) { Notify($"회전본 생성 실패: {ex.Message}", true); }
        finally { Working = false; }
    }

    [RelayCommand]
    private async Task StartTrain()
    {
        Message = null;
        try { await _train.StartTrainingAsync(Epochs, Imgsz, Batch, BaseModelIndex == 1 ? "yolov8s-seg.pt" : "yolov8n-seg.pt", MinimalAug, BrightAug); }
        catch (Exception ex) { Notify($"학습 시작 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private async Task StartExport()
    {
        Message = null;
        try { await _train.StartExportAsync(Opset, ExportImgsz, ExportModality); }
        catch (Exception ex) { Notify($"내보내기 시작 실패: {ex.Message}", true); }
    }

    [RelayCommand] private void Stop() => _train.Stop();
    [RelayCommand] private void ClearLog() { _train.ClearLog(); LogText = ""; }

    [RelayCommand]
    private async Task RunInfer()
    {
        if (Inferring) return;
        if (SelModel is null || InferEntry is null) { Notify("모델과 이미지를 선택하세요.", true); return; }
        Inferring = true; Message = null;
        try
        {
            UploadName = null;
            var r = await _seg.RunOnCaptureAsync(SelModel.Path, InferEntry.Stem, InferModalityIndex == 1 ? "ir" : "rgb", (float)Conf, (float)MaskThr);
            ShowInfer(r);
        }
        catch (Exception ex) { Notify($"추론 실패: {ex.Message}", true); }
        finally { Inferring = false; }
    }

    /// <summary>외부 검증용 이미지(뷰의 파일 선택기) → 바이트로 바로 추론.</summary>
    public async Task InferUploadAsync(string name, byte[] bytes)
    {
        if (Inferring) return;
        if (SelModel is null) { Notify("먼저 모델을 선택하세요.", true); return; }
        Inferring = true; Message = null;
        try
        {
            _uploadBytes = bytes; UploadName = name;
            OnPropertyChanged(nameof(HasUpload));
            if (name.Contains("_rgb", StringComparison.OrdinalIgnoreCase)) AddModalityIndex = 0;
            else if (name.Contains("_ir", StringComparison.OrdinalIgnoreCase)) AddModalityIndex = 1;
            var r = await _seg.RunOnBytesAsync(SelModel.Path, bytes, (float)Conf, (float)MaskThr);
            ShowInfer(r);
        }
        catch (Exception ex) { Notify($"업로드 추론 실패: {ex.Message}", true); }
        finally { Inferring = false; }
    }

    private void ShowInfer(InferResult r)
    {
        InferOk = r.Count > 0;
        InferText = $"검출 {r.Count}개 · 최고점수 {r.MaxScore:0.00} · 커버리지 {r.Coverage * 100:0.0}% · {r.Millis} ms ({r.Width}×{r.Height})";
        var b = _seg.LastOverlay;
        if (b is { Length: > 0 }) { using var ms = new MemoryStream(b); OverlayImage = new Bitmap(ms); }
    }

    // 업로드한(검출 실패한) 이미지를 캡처 폴더에 학습 데이터로 저장하고 라벨링 화면으로 이동.
    [RelayCommand]
    private async Task AddToTrainingAndLabel()
    {
        if (_uploadBytes is null) { Notify("먼저 이미지를 업로드하세요.", true); return; }
        try
        {
            var mod = AddModalityIndex == 1 ? "ir" : "rgb";
            using var scope = _scopeFactory.CreateScope();
            var stem = await scope.ServiceProvider.GetRequiredService<LabelDataService>().SaveCaptureImageAsync(_uploadBytes, mod);
            LabelEditorViewModel.Preselect(stem, mod);
            _nav.NavigateTo<LabelEditorViewModel>();
        }
        catch (Exception ex) { Notify($"학습셋 추가 실패: {ex.Message}", true); }
    }

    private void Notify(string m, bool err) { Message = m; IsError = err; }
}
