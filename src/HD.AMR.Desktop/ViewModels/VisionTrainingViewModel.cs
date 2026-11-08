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
/// ① 데이터셋 변환, ② YOLO 학습·ONNX 내보내기, ③ U-Net 학습·내보내기, ④ 네이티브 ONNX 추론(캡처/업로드)·모델 목록.
/// 학습 프로세스는 싱글톤 WeldTrainingService 가 백그라운드로 진행하며 1초 폴링으로 상태/로그를 갱신한다.
/// </summary>
public sealed partial class VisionTrainingViewModel : ViewModelBase
{
    private readonly WeldTrainingService _train;
    private readonly OnnxBeadSegmentationService _seg;
    private readonly HD.AMR.App.Service.WeldTrackingService _weld;   // 운영 검출 파라미터(직선 피팅 방식 등)
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INavigationService _nav;
    private readonly DispatcherTimer _timer;
    private TrainingPhase _lastPhase = TrainingPhase.Idle;

    public ObservableCollection<ModelInfo> Models { get; } = new();
    public ObservableCollection<CaptureEntry> Entries { get; } = new();
    /// <summary>① 데이터셋 변환 학습 폴더(여러 개). 저장값: Vision.Dataset.TrainDirs.</summary>
    public ObservableCollection<string> TrainDirs { get; } = new();

    [ObservableProperty] private TrainingPaths? _paths;
    [ObservableProperty] private PythonInfo? _py;
    [ObservableProperty] private string _pythonExe = "python";
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private int _modalityIndex;         // ① 0 rgb, 1 ir
    [ObservableProperty] private int _convertKindIndex;      // ① 0 면, 1 선
    [ObservableProperty] private string? _valDir;            // ① 검증 폴더(null = 세션 자동 분리)
    [ObservableProperty] private int _rgbArea, _irArea, _rgbLine, _irLine;   // 선택 폴더 전체 라벨 수
    [ObservableProperty] private int _yoloKindIndex;         // ② 0 면(기본), 1 선
    [ObservableProperty] private int _unetKindIndex = 1;     // ③ 0 면, 1 선(기본)
    [ObservableProperty] private string _areaDatasetInfo = "", _lineDatasetInfo = "";
    // imgsz 864: IR 848×480 을 거의 원해상도로(640 이면 가는 비드가 0.75배로 줄고 proto 마스크도 성겨짐).
    [ObservableProperty] private int _epochs = 300, _imgsz = 864, _batch = 4;
    [ObservableProperty] private bool _brightAug;
    [ObservableProperty] private int _augPresetIndex;        // 0 선 전용(권장), 1 최소, 2 ultralytics 기본
    [ObservableProperty] private int _patience = 50;          // 조기 종료(0=끔)
    [ObservableProperty] private int _baseModelIndex;        // 0 v8n, 1 v8s, 2 11n, 3 11s
    // 내보내기 opset·imgsz·직사각 입력은 서비스 고정값(opset 12 · 학습 imgsz · 직사각), 모달리티는 학습 시점 기록으로 자동.
    [ObservableProperty] private bool _autoExport = true;    // ②·③ 학습 정상 종료 시 바로 ONNX 내보내기

    // ③ U-Net (PyTorch, 크롭 학습)
    [ObservableProperty] private int _unetEpochs = 100, _unetBatch = 8, _unetCrop = 384, _unetPatience = 30;
    [ObservableProperty] private int _unetBaseIndex;           // 0 base16(빠름), 1 base32(정확)
    [ObservableProperty] private bool _addRotated = true;
    [ObservableProperty] private bool _emitterOffOnly;        // ① IR 변환 시 이미터 OFF(도트 없음) 추정 촬영본만
    [ObservableProperty] private bool _clahe;                 // ① 변환 시 CLAHE 전처리(학습한 모델은 _clahe)    // ① 변환 시 시계 90° 회전본 포함(데이터셋에만)
    [ObservableProperty] private string? _convertText;
    private bool _loadingSources;
    [ObservableProperty] private bool _working;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    // ④ 추론
    [ObservableProperty] private ModelInfo? _selModel;
    /// <summary>④ 점검 이미지 목록 — 학습·검증 폴더의 라벨 촬영본([train]/[val]) + 라벨링 폴더의 나머지([ - ]).</summary>
    public ObservableCollection<InferItem> InferItems { get; } = new();
    [ObservableProperty] private InferItem? _inferItem;
    [ObservableProperty] private bool _inferValOnly = _lastInferValOnly;
    // ④ 마지막 선택 — 뷰모델은 화면 이동마다 새로 만들어지므로(Transient) 앱 실행 중에는 정적으로 기억해 되돌린다.
    private static string? _lastModelPath, _lastInferStem;
    private static bool _lastInferValOnly;
    [ObservableProperty] private string? _inferSplitText;
    /// <summary>④ 점검 이미지 출처 폴더 요약(데이터셋 탭과 같은 모양) — 폴더별 train/val 수.</summary>
    public ObservableCollection<InferSourceRow> InferSources { get; } = new();
    /// <summary>선택 모델이 현재 데이터셋으로 학습되지 않았을 가능성 경고(없으면 null).</summary>
    [ObservableProperty] private string? _inferModelWarning;
    private int _inferListVer;
    [ObservableProperty] private int _inferModalityIndex = 1;
    [ObservableProperty] private double _conf = 0.25, _maskThr = 0.50;
    [ObservableProperty] private string? _inferText;
    [ObservableProperty] private bool _inferOk;
    /// <summary>오버레이에 정답 선(초록)이 그려졌는지 — 범례의 "정답 선" 항목 표시용.</summary>
    [ObservableProperty] private bool _inferHasGt;
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
    public bool TorchReady => Py is { TorchOk: true };
    public string UnetExportButtonText => $"best.pt → weld_unet_{_unetTrainedModality}_{WeldModelNames.KindToken(_unetTrainedKind)}{(_unetTrainedClahe ? "_clahe" : "")}_(날짜시각).onnx";
    private LabelKind _yoloTrainedKind = LabelKind.Area, _unetTrainedKind = LabelKind.Line;   // 마지막 학습의 데이터셋 종류(파일명 미리보기)
    private bool _yoloTrainedClahe, _unetTrainedClahe;                                        // 마지막 학습이 CLAHE 데이터셋인지
    private string _yoloTrainedModality = "ir", _unetTrainedModality = "ir";                  // 마지막 학습의 모달리티(학습 시점 기록)
    public bool NeedInstall => Py is { UltralyticsOk: false };
    public string InstallHint => Py is null ? "" : $"설치: {Py.Exe} -m pip install ultralytics onnx";
    public string ExportButtonText => $"best.pt → weld_yolo_{_yoloTrainedModality}_{WeldModelNames.KindToken(_yoloTrainedKind)}{(_yoloTrainedClahe ? "_clahe" : "")}_(날짜시각).onnx";
    private string Modality => ModalityIndex == 1 ? "ir" : "rgb";
    private LabelKind ConvertKind => ConvertKindIndex == 1 ? LabelKind.Line : LabelKind.Area;
    public string RgbCountText => $"RGB (면 {RgbArea} · 선 {RgbLine})";
    public string IrCountText => $"IR (면 {IrArea} · 선 {IrLine})";
    public string AreaKindText => $"▦ 면 라벨 ({(ModalityIndex == 1 ? IrArea : RgbArea)}개)";
    public string LineKindText => $"📏 선 라벨 ({(ModalityIndex == 1 ? IrLine : RgbLine)}개)";
    public string ValDirText => ValDir ?? "(지정 안 함 — 학습 폴더를 촬영 세션 단위로 자동 분리)";
    public bool HasValDir => ValDir is not null;
    public bool NoTrainDirs => TrainDirs.Count == 0;
    public string YoloDatasetInfo => YoloKindIndex == 1 ? LineDatasetInfo : AreaDatasetInfo;
    public string UnetDatasetInfo => UnetKindIndex == 1 ? LineDatasetInfo : AreaDatasetInfo;

    public VisionTrainingViewModel(WeldTrainingService train, OnnxBeadSegmentationService seg,
        IServiceScopeFactory scopeFactory, INavigationService nav, HD.AMR.App.Service.WeldTrackingService weld)
    {
        _weld = weld;
        _train = train; _seg = seg; _scopeFactory = scopeFactory; _nav = nav;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public override async void OnActivated()
    {
        Paths = await _train.GetPathsAsync();
        OnPropertyChanged(nameof(HasPaths));
        if (Paths is null) return;
        await LoadSourcesAsync();
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
        OnPropertyChanged(nameof(TorchReady));
    }
    partial void OnRgbAreaChanged(int value) => RaiseCounts();
    partial void OnIrAreaChanged(int value) => RaiseCounts();
    partial void OnRgbLineChanged(int value) => RaiseCounts();
    partial void OnIrLineChanged(int value) => RaiseCounts();
    partial void OnModalityIndexChanged(int value) => RaiseCounts();
    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(RgbCountText)); OnPropertyChanged(nameof(IrCountText));
        OnPropertyChanged(nameof(AreaKindText)); OnPropertyChanged(nameof(LineKindText));
    }
    partial void OnValDirChanged(string? value) { OnPropertyChanged(nameof(ValDirText)); OnPropertyChanged(nameof(HasValDir)); }
    partial void OnAreaDatasetInfoChanged(string value) { OnPropertyChanged(nameof(YoloDatasetInfo)); OnPropertyChanged(nameof(UnetDatasetInfo)); }
    partial void OnLineDatasetInfoChanged(string value) { OnPropertyChanged(nameof(YoloDatasetInfo)); OnPropertyChanged(nameof(UnetDatasetInfo)); }
    partial void OnYoloKindIndexChanged(int value) { OnPropertyChanged(nameof(YoloDatasetInfo)); _ = RefreshInferListAsync(); }
    partial void OnUnetKindIndexChanged(int value) { OnPropertyChanged(nameof(UnetDatasetInfo)); _ = RefreshInferListAsync(); }
    partial void OnInferModalityIndexChanged(int value) => _ = RefreshInferListAsync();
    partial void OnInferValOnlyChanged(bool value) { _lastInferValOnly = value; _ = RefreshInferListAsync(); }
    // 사용자가 고른 이미지만 기억 — 목록 재구성 중 '첫 항목' 대체 선택은 기억하지 않는다.
    partial void OnInferItemChanged(InferItem? value) { if (value is not null && !_fillingInfer) _lastInferStem = value.Stem; }
    private bool _fillingInfer;
    partial void OnSelModelChanged(ModelInfo? value)
    {
        if (value is not null) _lastModelPath = value.Path;
        OnPropertyChanged(nameof(ShowInferModality));
        LoadInferThresholds();
        _ = RefreshInferListAsync();
    }

    // ── ④ 임계값 — 운영 파라미터(Weld.Yolo.Confidence/MaskThreshold, Weld.Unet.Threshold)와 연결 ──
    /// <summary>선택 모델이 U-Net 이면 true(파일명 기준). U-Net 은 conf 없이 확률 임계 하나만 쓴다.</summary>
    public bool InferIsUnet => SelModel?.Name.Contains("unet", StringComparison.OrdinalIgnoreCase) == true;
    public double MaskMin => InferIsUnet ? HD.AMR.App.Service.WeldTrackingService.UnetThresholdMin : HD.AMR.App.Service.WeldTrackingService.DlMaskThresholdMin;
    public double MaskMax => InferIsUnet ? HD.AMR.App.Service.WeldTrackingService.UnetThresholdMax : HD.AMR.App.Service.WeldTrackingService.DlMaskThresholdMax;
    public string MaskText => InferIsUnet ? $"확률 {MaskThr:0.00}" : $"mask {MaskThr:0.00}";
    public string MaskLabel => InferIsUnet ? "확률" : "mask";   // ④ 임계값 라벨(값은 슬라이더 옆에 따로 표시)
    public string InferOpsText => InferIsUnet
        ? $"파라미터 확률 {_weld.SavedUnetThreshold:0.00}"
        : $"파라미터 conf {_weld.SavedDlConfidence:0.00} · mask {_weld.SavedDlMaskThreshold:0.00}";
    /// <summary>슬라이더 값이 저장된 파라미터와 다른지 — 되돌리기·파라미터에 저장 활성화.</summary>
    public bool InferThrDirty => InferIsUnet
        ? Math.Round(MaskThr, 2) != _weld.SavedUnetThreshold
        : Math.Round(Conf, 2) != _weld.SavedDlConfidence || Math.Round(MaskThr, 2) != _weld.SavedDlMaskThreshold;
    partial void OnMaskThrChanged(double value) { OnPropertyChanged(nameof(MaskText)); OnInferThresholdChanged(); }
    partial void OnConfChanged(double value) => OnInferThresholdChanged();

    // 슬라이더를 움직이면 멈춘 뒤 300ms 에 마지막 추론 이미지(촬영본 또는 업로드)를 다시 추론 — 임계값 트렌드 확인용.
    private bool _loadingThresholds;
    private CancellationTokenSource? _reinferCts;
    private async void OnInferThresholdChanged()
    {
        OnPropertyChanged(nameof(InferThrDirty));
        if (_loadingThresholds || OverlayImage is null || SelModel is null) return;
        _reinferCts?.Cancel();
        var cts = _reinferCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
            while (Inferring) await Task.Delay(50, cts.Token);   // 진행 중 추론이 끝나면 최신 값으로 한 번 더
        }
        catch (TaskCanceledException) { return; }
        if (UploadName is { } name && _uploadBytes is { } bytes) await InferUploadAsync(name, bytes);
        else if (InferItem is not null) await RunInfer();
    }

    /// <summary>모델 종류에 맞는 저장된 파라미터 임계값을 슬라이더 시작값으로 불러온다.</summary>
    private void LoadInferThresholds()
    {
        OnPropertyChanged(nameof(InferIsUnet)); OnPropertyChanged(nameof(MaskMin)); OnPropertyChanged(nameof(MaskMax));
        _loadingThresholds = true;
        try
        {
            if (InferIsUnet) MaskThr = _weld.SavedUnetThreshold;
            else { Conf = _weld.SavedDlConfidence; MaskThr = _weld.SavedDlMaskThreshold; }
        }
        finally { _loadingThresholds = false; }
        OnPropertyChanged(nameof(MaskText)); OnPropertyChanged(nameof(MaskLabel)); OnPropertyChanged(nameof(InferOpsText));
        OnPropertyChanged(nameof(InferThrDirty));
    }

    /// <summary>④ 슬라이더를 저장된 파라미터 값으로 되돌린다(마지막 추론 이미지는 다시 추론).</summary>
    [RelayCommand]
    private void RevertInferThresholds()
    {
        if (InferIsUnet) MaskThr = _weld.SavedUnetThreshold;
        else { Conf = _weld.SavedDlConfidence; MaskThr = _weld.SavedDlMaskThreshold; }
    }

    /// <summary>④ 슬라이더 값을 운영 파라미터로 저장 — 용접 추적·시퀀스의 검출이 이 값을 쓴다.</summary>
    [RelayCommand]
    private async Task SaveInferThresholds()
    {
        bool ok;
        if (InferIsUnet)
        {
            _weld.SetUnetThreshold(MaskThr);
            ok = await _weld.SaveUnetThresholdAsync();
            if (ok) Notify($"파라미터 저장: Weld.Unet.Threshold = {_weld.SavedUnetThreshold:0.00}", false);
        }
        else
        {
            _weld.SetDlThresholds(Conf, MaskThr);
            ok = await _weld.SaveDlThresholdsAsync();
            if (ok) Notify($"파라미터 저장: Weld.Yolo.Confidence = {_weld.SavedDlConfidence:0.00}, Weld.Yolo.MaskThreshold = {_weld.SavedDlMaskThreshold:0.00}", false);
        }
        if (!ok) Notify(_weld.Message ?? "파라미터 저장 실패", true);
        OnPropertyChanged(nameof(InferOpsText)); OnPropertyChanged(nameof(InferThrDirty));
    }

    // ④ 판정 기준 — 모델 파일명(weld_{yolo|unet}_{ir|rgb}_{area|line}_…)에서 읽는다.
    // 표식이 없는 모델(예: 학습 run 의 best.onnx)만 ②/③ 데이터셋 선택·아래 모달리티 드롭박스를 쓴다.
    private LabelKind InferKind =>
        (SelModel is null ? null : WeldModelNames.LabelKindOf(SelModel.Name))
        ?? (InferIsUnet ? (UnetKindIndex == 1 ? LabelKind.Line : LabelKind.Area)
                        : (YoloKindIndex == 1 ? LabelKind.Line : LabelKind.Area));
    /// <summary>④ 추론 모달리티 — 모델 파일명 표식, 없으면 드롭박스.</summary>
    private string InferModality =>
        (SelModel is null ? null : WeldModelNames.ModalityOf(SelModel.Name)) ?? (InferModalityIndex == 1 ? "ir" : "rgb");
    /// <summary>모델 이름에 모달리티 표식이 없을 때만 드롭박스 표시.</summary>
    public bool ShowInferModality => SelModel is not null && WeldModelNames.ModalityOf(SelModel.Name) is null;

    /// <summary>④ 목록 재구성. 판정은 현재 폴더 설정으로 데이터셋 변환과 같은 규칙(<see cref="WeldTrainingService.GetSplitAsync"/>).</summary>
    private async Task RefreshInferListAsync()
    {
        var ver = ++_inferListVer;
        var mod = InferModality;
        var kind = InferKind;
        SplitView split;
        string? labelDir;
        try
        {
            split = await _train.GetSplitAsync(mod, kind);
            labelDir = await _train.GetLabelDirAsync();
        }
        catch { split = new SplitView(new(), false); labelDir = null; }
        if (ver != _inferListVer) return;   // 그 사이 조건이 바뀜 — 최신 호출만 반영

        var items = split.Items.Select(x => new InferItem(x.Stem, x.Dir, x.IsVal)).ToList();
        if (InferValOnly) items.RemoveAll(i => i.IsVal != true);
        else if (labelDir is not null)
        {
            var known = items.Select(i => i.Stem).ToHashSet(StringComparer.Ordinal);
            foreach (var e in Entries)
                if ((mod == "ir" ? e.HasIr : e.HasRgb) && !known.Contains(e.Stem))
                    items.Add(new InferItem(e.Stem, labelDir, null));
        }
        // IR 이면 촬영본마다 이미지로 추정한 이미터 상태(ON/OFF/?) — 파일별 캐시, 처음만 이미지당 수십 ms.
        if (mod == "ir")
        {
            var states = await Task.Run(() => items.AsParallel()
                .Select(i => EmitterDotEstimator.ClassifyFile(Path.Combine(i.Dir, LabelFiles.ImageName(i.Stem, mod)))).ToList());
            if (ver != _inferListVer) return;
            items = items.Select((it, k) => it with { Emitter = states[k] }).ToList();
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Stem, b.Stem));

        var sel = _lastInferStem ?? InferItem?.Stem;   // 마지막으로 본 이미지 유지
        _fillingInfer = true;
        try
        {
            InferItems.Clear();
            foreach (var i in items) InferItems.Add(i);
            InferItem = InferItems.FirstOrDefault(i => i.Stem == sel) ?? InferItems.FirstOrDefault();
        }
        finally { _fillingInfer = false; }

        // 출처 폴더 요약 — 학습 폴더(설정 순서) · 검증 폴더 · 라벨링 폴더([ - ]).
        InferSources.Clear();
        string Counts(string dir)
        {
            var g = split.Items.Where(i => LabelFiles.SameDir(i.Dir, dir)).ToList();
            int v = g.Count(i => i.IsVal);
            return $"train {g.Count - v} · val {v}";
        }
        bool first = true;
        foreach (var d in TrainDirs)
        {
            InferSources.Add(new InferSourceRow(first ? "학습 폴더" : "", d, Counts(d)));
            first = false;
        }
        if (TrainDirs.Count == 0) InferSources.Add(new InferSourceRow("학습 폴더", "(없음 — ① 데이터셋에서 추가)", ""));
        InferSources.Add(ValDir is null
            ? new InferSourceRow("검증 폴더", "지정 안 함 — 학습 폴더를 촬영 세션 단위로 자동 분리", "")
            : new InferSourceRow("검증 폴더", ValDir, Counts(ValDir)));
        if (labelDir is not null)
        {
            var known = split.Items.Select(i => i.Stem).ToHashSet(StringComparer.Ordinal);
            int unl = Entries.Count(e => (mod == "ir" ? e.HasIr : e.HasRgb) && !known.Contains(e.Stem));
            InferSources.Add(new InferSourceRow("라벨링 폴더", labelDir, $"[ - ] {unl}"));
        }

        int nVal = split.Items.Count(i => i.IsVal);
        InferSplitText = $"점검 이미지 출처 · 모델 기준({LabelFiles.KindText(kind)} 라벨 · {mod.ToUpperInvariant()}) — " +
                         "[train]/[val] 은 현재 폴더 설정으로 데이터셋을 변환할 때의 구분, [ - ] 는 데이터셋에 안 들어가는 이미지" +
                         (split.SingleSession ? " · 촬영 세션이 1개뿐이라 val 이 train 과 같은 자리 영상입니다" : "") +
                         (InferValOnly && nVal == 0 ? " · val 이미지가 없습니다" : "");
        InferModelWarning = ModelDatasetWarning(kind, mod);
    }

    // 선택 모델이 현재 데이터셋으로 학습되지 않았을 가능성 — 그러면 [val] 이미지도 모델이 학습했을 수 있다.
    // (폴더 설정을 바꾸고 아직 변환하지 않은 경우는 알 수 없다 — 변환 후 학습한 모델이면 정확.)
    private string? ModelDatasetWarning(LabelKind kind, string mod)
    {
        if (SelModel is null || Paths is null) return null;
        var ds = Paths.DatasetOf(kind);
        var yaml = Path.Combine(ds, "data.yaml");
        if (!File.Exists(yaml))
            return $"{LabelFiles.KindText(kind)} 데이터셋을 아직 변환하지 않았습니다 — [train]/[val] 구분이 이 모델의 실제 학습과 다를 수 있습니다.";
        var conv = File.GetLastWriteTime(yaml);
        if (SelModel.Modified < conv)
            return $"이 모델({SelModel.Modified:MM-dd HH:mm})은 마지막 {LabelFiles.KindText(kind)} 데이터셋 변환({conv:MM-dd HH:mm}) 전에 만들어졌습니다 — [val] 이미지도 모델이 학습했을 수 있습니다.";
        var trainImgs = Path.Combine(ds, "images", "train");
        var dsMod = Directory.Exists(trainImgs)
            ? Directory.EnumerateFiles(trainImgs, "*.png").Select(f => f.EndsWith("_ir.png") ? "ir" : "rgb").FirstOrDefault()
            : null;
        if (dsMod is not null && dsMod != mod)
            return $"현재 {LabelFiles.KindText(kind)} 데이터셋은 {dsMod.ToUpperInvariant()} 인데 모델은 {mod.ToUpperInvariant()} 입니다 — [train]/[val] 구분이 이 모델의 실제 학습과 다를 수 있습니다.";
        return null;
    }

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

    // ── 데이터 폴더(학습 여러 개 + 검증 1개) ───────────────────────────────
    private async Task LoadSourcesAsync()
    {
        var src = await _train.GetDatasetSourcesAsync();
        TrainDirs.Clear();
        foreach (var d in src.TrainDirs) TrainDirs.Add(d);
        ValDir = src.ValDir;
        _loadingSources = true;
        try
        {
            AddRotated = await _train.GetAddRotatedAsync();
            EmitterOffOnly = await _train.GetEmitterOffOnlyAsync();
            Clahe = await _train.GetClaheAsync();
        }
        finally { _loadingSources = false; }
        OnPropertyChanged(nameof(NoTrainDirs));
    }

    async partial void OnClaheChanged(bool value)
    {
        if (_loadingSources) return;
        try { await _train.SetClaheAsync(value); }
        catch (Exception ex) { Notify($"설정 저장 실패: {ex.Message}", true); }
    }

    async partial void OnEmitterOffOnlyChanged(bool value)
    {
        if (_loadingSources) return;
        try { await _train.SetEmitterOffOnlyAsync(value); }
        catch (Exception ex) { Notify($"설정 저장 실패: {ex.Message}", true); }
        await RefreshInferListAsync();   // ④ train/val 구분도 같은 필터를 따른다
    }

    async partial void OnAddRotatedChanged(bool value)
    {
        if (_loadingSources) return;
        try { await _train.SetAddRotatedAsync(value); }
        catch (Exception ex) { Notify($"설정 저장 실패: {ex.Message}", true); }
    }

    private async Task SaveSourcesAsync()
    {
        await _train.SetDatasetSourcesAsync(TrainDirs, ValDir);
        OnPropertyChanged(nameof(NoTrainDirs));
        await RefreshCountsAsync();
    }

    /// <summary>뷰의 폴더 선택기 결과 → 학습 폴더 추가(중복·검증 폴더와 같은 폴더는 무시).</summary>
    public async Task AddTrainDirAsync(string dir)
    {
        if (TrainDirs.Any(d => LabelFiles.SameDir(d, dir))) { Notify("이미 추가된 폴더입니다.", true); return; }
        if (LabelFiles.SameDir(dir, ValDir)) { Notify("검증 폴더로 지정된 폴더입니다.", true); return; }
        TrainDirs.Add(dir);
        await SaveSourcesAsync();
    }

    [RelayCommand]
    private async Task RemoveTrainDir(string? dir)
    {
        if (dir is null) return;
        TrainDirs.Remove(dir);
        await SaveSourcesAsync();
    }

    /// <summary>뷰의 폴더 선택기 결과 → 검증 폴더 지정(학습 목록에 같은 폴더가 있으면 거기서 뺀다).</summary>
    public async Task SetValDirAsync(string dir)
    {
        foreach (var d in TrainDirs.Where(d => LabelFiles.SameDir(d, dir)).ToList()) TrainDirs.Remove(d);
        ValDir = dir;
        await SaveSourcesAsync();
    }

    [RelayCommand]
    private async Task ClearValDir()
    {
        ValDir = null;
        await SaveSourcesAsync();
    }

    private IEnumerable<string> SourceDirs => ValDir is null ? TrainDirs : TrainDirs.Append(ValDir);

    private async Task RefreshCountsAsync()
    {
        try
        {
            // 학습·검증 폴더 전체의 라벨 수(변환 대상). ④ 추론 이미지 목록은 라벨링 폴더 기준.
            var dirs = SourceDirs.ToList();
            // 원본 폴더의 예전 회전본(_cw90/_ccw90)은 변환에서 제외되므로 개수에서도 뺀다.
            var all = await Task.Run(() => dirs.SelectMany(LabelDataService.List)
                .Where(e => DatasetSplitter.BaseStem(e.Stem) == e.Stem).ToList());
            RgbArea = all.Count(e => e.RgbArea); IrArea = all.Count(e => e.IrArea);
            RgbLine = all.Count(e => e.RgbLine); IrLine = all.Count(e => e.IrLine);

            using var scope = _scopeFactory.CreateScope();
            var list = await scope.ServiceProvider.GetRequiredService<LabelDataService>().ListAsync();
            Entries.Clear();
            foreach (var e in list) Entries.Add(e);
            RefreshDatasetInfo();
            await RefreshInferListAsync();
        }
        catch { /* 무시 */ }
    }

    // 변환된 데이터셋(dl/dataset_area·dataset_line)의 train/val 이미지 수 표시.
    private void RefreshDatasetInfo()
    {
        if (Paths is null) return;
        string Info(LabelKind k)
        {
            var ds = Paths.DatasetOf(k);
            int N(string sub) { var d = Path.Combine(ds, "images", sub); return Directory.Exists(d) ? Directory.GetFiles(d, "*.png").Length : 0; }
            if (!File.Exists(Path.Combine(ds, "data.yaml"))) return $"{LabelFiles.DatasetFolder(k)} — 아직 변환 안 함";
            var mod = Directory.Exists(Path.Combine(ds, "images", "train"))
                ? Directory.GetFiles(Path.Combine(ds, "images", "train"), "*.png").Select(f => f.EndsWith("_ir.png") ? "IR" : "RGB").FirstOrDefault() ?? "" : "";
            return $"{LabelFiles.DatasetFolder(k)} — {mod} train {N("train")} / val {N("val")}" +
                   (WeldTrainingService.HasClaheMarker(ds) ? " · CLAHE" : "") +
                   $" · {File.GetLastWriteTime(Path.Combine(ds, "data.yaml")):MM-dd HH:mm} 변환";
        }
        AreaDatasetInfo = Info(LabelKind.Area);
        LineDatasetInfo = Info(LabelKind.Line);
    }

    // 마지막 학습(best.pt)의 라벨 종류·CLAHE·모달리티 → 내보내기 버튼의 파일명 미리보기.
    private async Task RefreshTrainedInfoAsync()
    {
        try
        {
            _yoloTrainedKind = await _train.TrainedKindAsync(unet: false);
            _unetTrainedKind = await _train.TrainedKindAsync(unet: true);
            _yoloTrainedClahe = await _train.TrainedClaheAsync(unet: false);
            _unetTrainedClahe = await _train.TrainedClaheAsync(unet: true);
            _yoloTrainedModality = (await _train.TrainedModalityAsync(unet: false)).Modality;
            _unetTrainedModality = (await _train.TrainedModalityAsync(unet: true)).Modality;
            OnPropertyChanged(nameof(ExportButtonText)); OnPropertyChanged(nameof(UnetExportButtonText));
        }
        catch (Exception ex) { Notify(ex.Message, true); }
    }

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        try
        {
            var sel = SelModel?.Path ?? _lastModelPath;   // 화면에 다시 들어오면 마지막으로 쓴 모델
            await RefreshTrainedInfoAsync();
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
            if (TrainDirs.Count == 0) { Notify("학습 폴더를 1개 이상 추가하세요.", true); return; }
            var r = await _train.ConvertAsync(Modality, ConvertKind, TrainDirs.ToList(), ValDir, AddRotated, EmitterOffOnly, Clahe);
            ConvertText = $"{LabelFiles.KindText(r.Kind)} 라벨 변환 완료 — 사용 {r.Used}건" + (r.Rotated > 0 ? $"(90° 회전본 {r.Rotated}건 포함)" : "") +
                          $" / 건너뜀 {r.Skipped}건 / 폴리곤 {r.Instances}개\n" +
                          (ValDir is null ? $"세션 {r.Sessions}개 → " : "검증 폴더 지정 → ") + $"train {r.Train}건 / val {r.Val}건\n{r.DataYaml}";
            // 방금 만든 데이터셋을 학습 화면 기본값으로(면→YOLO, 선→U-Net 이 흔한 조합이지만 둘 다 맞춘다).
            if (r.Kind == LabelKind.Area) YoloKindIndex = 0; else UnetKindIndex = 1;
            await RefreshCountsAsync();
            Notify($"데이터셋 변환 완료 — 사용 {r.Used}건.", false);
        }
        catch (Exception ex) { Notify($"변환 실패: {ex.Message}", true); }
        finally { Working = false; }
    }

    [RelayCommand]
    private async Task StartTrain()
    {
        Message = null;
        var model = BaseModelIndex switch { 1 => "yolov8s-seg.pt", 2 => "yolo11n-seg.pt", 3 => "yolo11s-seg.pt", _ => "yolov8n-seg.pt" };
        var aug = AugPresetIndex switch { 1 => TrainingAugmentation.Minimal, 2 => TrainingAugmentation.Full, _ => TrainingAugmentation.Line };
        // 자동 내보내기 설정은 학습 시작 시점 값으로 고정(학습 중 화면 값을 바꿔도 영향 없음).
        Func<Task>? then = null;
        if (AutoExport) then = () => _train.StartExportAsync();
        try { await _train.StartTrainingAsync(Epochs, Imgsz, Batch, model, aug, BrightAug, Patience, YoloKindIndex == 1 ? LabelKind.Line : LabelKind.Area, then); }
        catch (Exception ex) { Notify($"학습 시작 실패: {ex.Message}", true); }
        await RefreshTrainedInfoAsync();
    }

    [RelayCommand]
    private async Task StartExport()
    {
        Message = null;
        try { await _train.StartExportAsync(); }
        catch (Exception ex) { Notify($"내보내기 시작 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private async Task StartUnetTrain()
    {
        Message = null;
        Func<Task>? then = null;
        if (AutoExport) then = () => _train.StartUnetExportAsync();
        try { await _train.StartUnetTrainingAsync(UnetEpochs, UnetBatch, UnetCrop, UnetPatience, UnetBaseIndex == 1 ? 32 : 16, UnetKindIndex == 1 ? LabelKind.Line : LabelKind.Area, then); }
        catch (Exception ex) { Notify($"U-Net 학습 시작 실패: {ex.Message}", true); }
        await RefreshTrainedInfoAsync();
    }

    [RelayCommand]
    private async Task StartUnetExport()
    {
        Message = null;
        try { await _train.StartUnetExportAsync(); }
        catch (Exception ex) { Notify($"U-Net 내보내기 시작 실패: {ex.Message}", true); }
    }

    [RelayCommand] private void Stop() => _train.Stop();
    [RelayCommand] private void ClearLog() { _train.ClearLog(); LogText = ""; }

    [RelayCommand]
    private async Task RunInfer()
    {
        if (Inferring) return;
        if (SelModel is null || InferItem is null) { Notify("모델과 이미지를 선택하세요.", true); return; }
        Inferring = true; Message = null;
        try
        {
            UploadName = null;
            var mod = InferModality;
            var r = await _seg.RunOnCaptureAsync(SelModel.Path, InferItem.Stem, mod, (float)Conf, (float)MaskThr, InferItem.Dir);
            var img = await File.ReadAllBytesAsync(Path.Combine(InferItem.Dir, LabelFiles.ImageName(InferItem.Stem, mod)));
            // 정답 선 라벨이 있으면 오차 계산·초록 선 표시.
            var linePath = Path.Combine(InferItem.Dir, LabelFiles.LineName(InferItem.Stem, mod));
            var gt = File.Exists(linePath) ? WeldLineLabel.FromJson(await File.ReadAllTextAsync(linePath)) : null;
            await ShowInferAsync(r, img, gt);
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
            await ShowInferAsync(r, bytes, null);
        }
        catch (Exception ex) { Notify($"업로드 추론 실패: {ex.Message}", true); }
        finally { Inferring = false; }
    }

    // 결과 = 모델이 찾은 용접선(운영 검출과 같은 중심선 추출). 정답 선 라벨이 있으면 비교평가와 같은 기준으로 오차·합격.
    private async Task ShowInferAsync(InferResult r, byte[] image, WeldLineLabel? gt)
    {
        var mask = _seg.LastMask;
        if (mask is null || mask.Length != r.Width * r.Height)
        {
            InferOk = false;
            InferText = $"마스크를 얻지 못했습니다 · 검출 {r.Count}개";
            return;
        }
        var p = _weld.Params.Clone();
        var lr = await Task.Run(() => InferLineCheck.Run(image, mask, r.Width, r.Height, p, gt));
        string head;
        if (!lr.Found) head = $"용접선 검출 실패 — {lr.Message}";
        else if (lr.Errors is { } e)
            head = $"{(e.Pass ? "✓ 합격" : "✗ 불합격")}{(e.BranchMatched ? "(가지 일치 — 정답 선이 여러 가지라 가장 가까운 가지와 비교)" : "")} — 정답 대비 각도 오차 {e.AngleDeg:0.0}° · 중심 오차 {e.CenterOffsetPx:0.0}px " +
                   $"(기준 {WeldLineMetrics.DefaultAngleTolDeg:0}° / {WeldLineMetrics.DefaultCenterTolPx:0}px)";
        else head = $"용접선 검출 — 기울기 {lr.TiltDeg:0.0}° (선 라벨이 없어 오차는 계산하지 않음)";
        InferOk = lr.Found && (lr.Errors?.Pass ?? true);
        InferText = $"{head}\n{(_seg.LastClahe ? "CLAHE 전처리 적용 · " : "")}{(lr.Horizontal ? "가로" : "세로")} 진행 · 검출 {r.Count}개 · 최고점수 {r.MaxScore:0.00} · {r.Millis} ms ({r.Width}×{r.Height})";
        InferHasGt = lr.Errors is not null;   // InferLineCheck 는 크기가 맞는 정답 선이 있을 때만 오차 계산·초록 선 표시
        using var ms = new MemoryStream(lr.OverlayJpeg);
        OverlayImage = new Bitmap(ms);
    }

    // 업로드한(검출 실패한) 이미지를 라벨링 폴더에 학습 데이터로 저장하고 라벨링 화면으로 이동.
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

/// <summary>④ 점검 이미지 항목. <see cref="IsVal"/> null = 데이터셋에 안 들어가는 라벨링 폴더 이미지.</summary>
public sealed record InferItem(string Stem, string Dir, bool? IsVal, EmitterState? Emitter = null)
{
    public string Text => (IsVal switch { true => "[val]   ", false => "[train] ", _ => "[ - ]   " }) + Stem +
                          (Emitter is { } e ? $"  · 이미터 {EmitterDotEstimator.Text(e)}" : "");
}

/// <summary>④ 점검 이미지 출처 폴더 한 줄(구분 · 경로 · 개수).</summary>
public sealed record InferSourceRow(string Label, string Path, string Counts);
