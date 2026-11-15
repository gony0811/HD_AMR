using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Vision;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 용접선 검출 방식 일괄 비교평가. 선 라벨(정답)이 있는 촬영본에 YOLO·U-Net 모델을 돌려
/// 방식별 합격률·각도/중심 오차를 표로 보여 주고, 촬영본을 고르면 방식별 오버레이(정답=초록)를 나란히 보여 준다.
/// 평가 모델은 기본이 운영 모델(Weld.Yolo/Unet.ModelPath)이고, 바꿔도 이 평가에서만 쓴다(운영 설정 불변).
/// 결과는 싱글톤 서비스(LastReport)에 남아 페이지를 다시 열어도 유지된다.
/// </summary>
public sealed partial class WeldBenchmarkViewModel : ViewModelBase
{
    private readonly WeldDetectionBenchmarkService _bench;
    private readonly WeldTrainingService _train;
    private readonly WeldTrackingService _weld;
    private CancellationTokenSource? _cts;

    public ObservableCollection<SummaryRow> Summaries { get; } = new();
    public ObservableCollection<StemRow> Stems { get; } = new();
    public ObservableCollection<MethodCard> Cards { get; } = new();
    public ObservableCollection<BenchModelOption> YoloModels { get; } = new();
    public ObservableCollection<BenchModelOption> UnetModels { get; } = new();

    [ObservableProperty] private int _modalityIndex = 1;       // 0 rgb, 1 ir
    [ObservableProperty] private bool _useDl = true;
    [ObservableProperty] private bool _useUnet = true;
    [ObservableProperty] private BenchModelOption? _selectedYoloModel;
    [ObservableProperty] private BenchModelOption? _selectedUnetModel;
    [ObservableProperty] private bool _valOnly = true;
    [ObservableProperty] private bool _useWeldRoi;
    [ObservableProperty] private double _angleTol = WeldLineMetrics.DefaultAngleTolDeg;
    [ObservableProperty] private double _centerTol = WeldLineMetrics.DefaultCenterTolPx;
    [ObservableProperty] private bool _failOnly;
    [ObservableProperty] private int _lineFitIndex;              // 0 운영값, 1 RANSAC, 2 최소제곱
    [ObservableProperty] private int _labeledCount;
    [ObservableProperty] private int _valCount;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string? _reportInfo;
    [ObservableProperty] private string? _csvText;
    [ObservableProperty] private StemRow? _selectedStem;

    public string Modality => ModalityIndex == 1 ? "ir" : "rgb";
    public bool HasReport => _bench.LastReport is not null;
    /// <summary>⑤ 추론·검증 경로로 평가한 결과인지 — 오버레이 범례(노랑 실선/점선·옅은 빨강 마스크)는 이 경로에서만 맞다.</summary>
    public bool ManagedLegend => _bench.LastReport?.ManagedPath == true;

    // 라디오 버튼용(양방향) — 대상 모달리티 / 범위 / ROI.
    public bool IsRgb { get => ModalityIndex == 0; set { if (value) ModalityIndex = 0; } }
    public bool IsIr { get => ModalityIndex == 1; set { if (value) ModalityIndex = 1; } }
    public bool AllImages { get => !ValOnly; set { if (value) ValOnly = false; } }
    public bool ValOnlyChecked { get => ValOnly; set { if (value) ValOnly = true; } }
    public bool FullFrame { get => !UseWeldRoi; set { if (value) UseWeldRoi = false; } }
    public bool WeldRoiChecked { get => UseWeldRoi; set { if (value) UseWeldRoi = true; } }

    public string ValText => HasValDir ? $"검증셋(val) {ValCount}장" : "검증셋(val) · 세션 자동 분리";
    public string AllText => $"전체 {LabeledCount}장";
    public string SourceText => $"선 라벨 기준 · {Modality.ToUpperInvariant()} · 학습 폴더 + 검증 폴더";

    // ── 데이터 폴더(비전 학습 ① 과 같은 Vision.Dataset.* 설정) ──
    [ObservableProperty] private List<string> _trainDirs = new();
    [ObservableProperty] private string? _valDir;
    public bool HasValDir => ValDir is not null;
    public string TrainDirsText => TrainDirs.Count == 0 ? "(없음 — 비전 학습 ① 에서 추가)" : string.Join(" · ", TrainDirs.Select(DirName));
    public string TrainDirsTip => TrainDirs.Count == 0 ? TrainDirsText : string.Join("\n", TrainDirs);
    public string TrainCountText => $"{TrainDirs.Count}개 · {LabeledCount - ValCount}장";
    public string ValDirText => ValDir ?? "지정 안 함 — 학습 폴더를 촬영 세션 단위로 자동 분리";
    public string ValCountText => HasValDir ? $"{ValCount}장" : "";
    private static string DirName(string d) => Path.GetFileName(d.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : d;

    private readonly INavigationService _nav;

    public WeldBenchmarkViewModel(WeldDetectionBenchmarkService bench, WeldTrainingService train, WeldTrackingService weld,
        INavigationService nav)
    {
        _bench = bench;
        _train = train;
        _weld = weld;
        _nav = nav;
        // 페이지 VM 은 이동할 때마다 새로 만들어진다 — 마지막 평가 조건을 앱 실행 중 기억해 되살린다.
        // (backing field 직접 대입 = 변경 콜백·중복 새로고침 없이 복원. 갱신은 OnActivated 가 한 번.)
        if (_saved is { } c)
        {
#pragma warning disable MVVMTK0034
            _modalityIndex = c.ModalityIndex; _valOnly = c.ValOnly; _useDl = c.UseDl; _useUnet = c.UseUnet;
            _useWeldRoi = c.UseWeldRoi; _angleTol = c.AngleTol; _centerTol = c.CenterTol; _lineFitIndex = c.LineFitIndex;
            _failOnly = c.FailOnly;
#pragma warning restore MVVMTK0034
        }
    }

    private sealed record SavedConditions(int ModalityIndex, bool ValOnly, bool UseDl, bool UseUnet, string? YoloPath,
        string? UnetPath, bool UseWeldRoi, double AngleTol, double CenterTol, int LineFitIndex, bool FailOnly);
    private static SavedConditions? _saved;

    private void SaveConditions() => _saved = new SavedConditions(ModalityIndex, ValOnly, UseDl, UseUnet,
        SelectedYoloModel?.Path ?? _saved?.YoloPath, SelectedUnetModel?.Path ?? _saved?.UnetPath,
        UseWeldRoi, AngleTol, CenterTol, LineFitIndex, FailOnly);

    public override async void OnActivated()
    {
        if (_bench.LastReport is { } r) ShowReport(r);
        await RefreshCountAsync();
    }

    public override void OnDeactivated() => SaveConditions();

    async partial void OnModalityIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Modality)); OnPropertyChanged(nameof(IsRgb)); OnPropertyChanged(nameof(IsIr));
        OnPropertyChanged(nameof(SourceText));
        await RefreshCountAsync();
    }

    partial void OnValOnlyChanged(bool value) { OnPropertyChanged(nameof(AllImages)); OnPropertyChanged(nameof(ValOnlyChecked)); }
    partial void OnUseWeldRoiChanged(bool value) { OnPropertyChanged(nameof(FullFrame)); OnPropertyChanged(nameof(WeldRoiChecked)); }
    partial void OnLabeledCountChanged(int value) { OnPropertyChanged(nameof(AllText)); OnPropertyChanged(nameof(TrainCountText)); }
    partial void OnValCountChanged(int value)
    {
        OnPropertyChanged(nameof(ValText)); OnPropertyChanged(nameof(TrainCountText)); OnPropertyChanged(nameof(ValCountText));
    }
    partial void OnTrainDirsChanged(List<string> value)
    {
        OnPropertyChanged(nameof(TrainDirsText)); OnPropertyChanged(nameof(TrainDirsTip)); OnPropertyChanged(nameof(TrainCountText));
    }
    partial void OnValDirChanged(string? value)
    {
        OnPropertyChanged(nameof(HasValDir)); OnPropertyChanged(nameof(ValDirText)); OnPropertyChanged(nameof(ValCountText));
        OnPropertyChanged(nameof(ValText));
    }

    /// <summary>뷰의 폴더 선택기 결과 → 검증 폴더 지정(학습 목록에 같은 폴더가 있으면 거기서 뺀다 — 비전 학습과 같은 규칙).</summary>
    public async Task SetValDirAsync(string dir)
    {
        try
        {
            var train = TrainDirs.Where(d => !LabelFiles.SameDir(d, dir)).ToList();
            await _train.SetDatasetSourcesAsync(train, dir);
            await RefreshCountAsync();
        }
        catch (Exception ex) { Notify($"검증 폴더 저장 실패: {ex.Message}", true); }
    }

    /// <summary>검증 폴더 지정 해제 — 설정만 비운다(파일 그대로). 이후 val 은 학습 폴더를 촬영 세션 단위로 자동 분리.</summary>
    [RelayCommand]
    private async Task ClearValDirAsync()
    {
        try
        {
            await _train.SetDatasetSourcesAsync(TrainDirs, null);
            await RefreshCountAsync();
        }
        catch (Exception ex) { Notify($"검증 폴더 해제 실패: {ex.Message}", true); }
    }

    [RelayCommand] private void EditInTraining() => _nav.NavigateTo<VisionTrainingViewModel>();

    partial void OnFailOnlyChanged(bool value) { if (_bench.LastReport is { } r) FillStems(r); }

    partial void OnSelectedStemChanged(StemRow? value) => FillCards(value);

    /// <summary>촬영본 수와 모달리티에 맞는 모델 목록을 다시 읽는다.</summary>
    [RelayCommand]
    private async Task RefreshCountAsync()
    {
        try
        {
            var src = await _train.GetDatasetSourcesAsync();
            TrainDirs = src.TrainDirs.ToList();
            ValDir = src.ValDir;
            (LabeledCount, ValCount) = await _bench.CountLabeledAsync(Modality);
            await LoadModelsAsync();
        }
        catch (Exception ex) { Notify($"목록 조회 실패: {ex.Message}", true); }
    }

    // 모델 목록 — 이름의 모달리티 표식이 현재 대상과 같거나 표식이 없는 것만. 기본 선택 = 운영 모델(있으면).
    private async Task LoadModelsAsync()
    {
        var all = await _train.ListModelsAsync();
        bool Fits(ModelInfo m) => WeldModelNames.ModalityOf(m.Name) is not { } mod || mod == Modality;
        Fill(YoloModels, all.Where(m => WeldModelNames.IsYolo(m.Path) && Fits(m)), _weld.Params.DlModelPath,
            SelectedYoloModel?.Path ?? _saved?.YoloPath, o => SelectedYoloModel = o);
        Fill(UnetModels, all.Where(m => WeldModelNames.IsUnet(m.Path) && Fits(m)), _weld.Params.UnetModelPath,
            SelectedUnetModel?.Path ?? _saved?.UnetPath, o => SelectedUnetModel = o);

        static void Fill(ObservableCollection<BenchModelOption> target, IEnumerable<ModelInfo> models, string? operational,
            string? keep, Action<BenchModelOption?> select)
        {
            var opName = string.IsNullOrWhiteSpace(operational) ? null : Path.GetFileName(operational.Trim().Trim('"'));
            target.Clear();
            foreach (var m in models.OrderByDescending(m => m.Modified))
                target.Add(new BenchModelOption(m.Path, m.Name, string.Equals(m.Name, opName, StringComparison.OrdinalIgnoreCase)));
            select(target.FirstOrDefault(o => o.Path == keep) ?? target.FirstOrDefault(o => o.IsOperational) ?? target.FirstOrDefault());
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Running) return;
        var methods = new List<WeldDetectionMethod>();
        if (UseDl) methods.Add(WeldDetectionMethod.Dl);
        if (UseUnet) methods.Add(WeldDetectionMethod.Unet);
        if (methods.Count == 0) { Notify("비교할 방식을 하나 이상 선택하세요.", true); return; }
        if (UseDl && SelectedYoloModel is null) { Notify($"{Modality.ToUpperInvariant()} YOLO 모델이 없습니다 — 비전 학습에서 내보내거나 YOLO 체크를 끄세요.", true); return; }
        if (UseUnet && SelectedUnetModel is null) { Notify($"{Modality.ToUpperInvariant()} U-Net 모델이 없습니다 — 비전 학습에서 내보내거나 U-Net 체크를 끄세요.", true); return; }

        Running = true; Message = null; Progress = 0; ProgressText = null;
        _cts = new CancellationTokenSource();
        var prog = new Progress<(int Done, int Total)>(p =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ProgressText = $"{p.Done} / {p.Total}";
        });
        try
        {
            WeldLineFitMode? fit = LineFitIndex switch { 1 => WeldLineFitMode.Ransac, 2 => WeldLineFitMode.LeastSquares, _ => null };
            var opt = new BenchmarkOptions(Modality, methods, ValOnly, UseWeldRoi, AngleTol, CenterTol, fit,
                UseDl ? SelectedYoloModel?.Path : null, UseUnet ? SelectedUnetModel?.Path : null);
            var r = await _bench.RunAsync(opt, prog, _cts.Token);
            ShowReport(r);
            Notify(r.Warning ?? $"완료 — 촬영본 {r.ImageCount}장 × {methods.Count}방식", r.Warning is not null);
        }
        catch (OperationCanceledException) { Notify("중단했습니다.", true); }
        catch (Exception ex) { Notify($"비교평가 실패: {ex.Message}", true); }
        finally { Running = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    private void ShowReport(BenchmarkReport r)
    {
        Summaries.Clear();
        // 합격률 높은 순 → 1위가 맨 위.
        foreach (var s in r.Summaries.OrderByDescending(s => s.PassRate).ThenBy(s => s.OffsetMedian))
            Summaries.Add(new SummaryRow(s, r.ModelName(s.Method)));
        ReportInfo = $"{r.At:MM-dd HH:mm} · {r.Modality.ToUpperInvariant()} · {(r.Options.ValOnly ? "val " : "전체 ")}{r.ImageCount}장" +
                     $" · {(r.Options.UseWeldRoi ? "Weld ROI" : "전체 프레임")}" +
                     $" · {(r.LineFit == WeldLineFitMode.Ransac ? "RANSAC" : "최소제곱")}" +
                     $" · 각도≤{r.Options.AngleTolDeg:0.#}° 중심≤{r.Options.CenterTolPx:0.#}px" +
                     $" · 추론: {r.EngineText}";
        CsvText = r.CsvPath is null ? null : $"CSV · {r.CsvPath}";
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(ManagedLegend));
        FillStems(r);
    }

    private void FillStems(BenchmarkReport r)
    {
        var keep = SelectedStem?.Stem;
        Stems.Clear();
        foreach (var g in r.Items.GroupBy(i => i.Stem).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var row = new StemRow(g.Key, g.ToList());
            if (FailOnly && row.AllPass) continue;
            Stems.Add(row);
        }
        SelectedStem = Stems.FirstOrDefault(s => s.Stem == keep) ?? Stems.FirstOrDefault();
    }

    private void FillCards(StemRow? row)
    {
        foreach (var c in Cards) c.Image?.Dispose();
        Cards.Clear();
        if (row is null) return;
        bool managed = _bench.LastReport?.ManagedPath == true;
        foreach (var i in row.Items) Cards.Add(new MethodCard(i, managed));
    }

    private void Notify(string m, bool err) { Message = m; IsError = err; }

    /// <summary>화면 표기 — 다른 페이지(용접 추적)와 같은 이름.</summary>
    public static string MethodName(WeldDetectionMethod m) => m switch
    {
        WeldDetectionMethod.Dl => "YOLO",
        WeldDetectionMethod.Unet => "U-Net",
        WeldDetectionMethod.Ridge => "Ridge",
        _ => "파라미터(CV)",
    };

    public static string MethodLetter(WeldDetectionMethod m) => m switch
    {
        WeldDetectionMethod.Dl => "Y",
        WeldDetectionMethod.Unet => "U",
        WeldDetectionMethod.Ridge => "R",
        _ => "P",
    };
}

/// <summary>평가 모델 드롭다운 항목. IsOperational = 운영 파라미터에 지정된 모델.</summary>
public sealed record BenchModelOption(string Path, string Name, bool IsOperational)
{
    public string Label => IsOperational ? $"{Name}  (운영)" : Name;
    public override string ToString() => Label;
}

public sealed class SummaryRow(MethodSummary s, string? model)
{
    public string Method => WeldBenchmarkViewModel.MethodName(s.Method);
    public string Model => model is null ? "" : System.IO.Path.GetFileNameWithoutExtension(model);
    public bool HasModel => model is not null;
    public string PassRate => $"{s.PassRate:P0} ({s.Passed}/{s.Count})";
    public double PassPct => s.PassRate * 100;
    public string DetectRate => $"{s.DetectRate:P0}";
    public string Angle => $"{Fmt(s.AngleMedian, "0.00")} / {Fmt(s.AngleP90, "0.00")}";
    public string Offset => $"{Fmt(s.OffsetMedian, "0.0")} / {Fmt(s.OffsetP90, "0.0")}";
    public string Millis => Fmt(s.MillisMean, "0");
    internal static string Fmt(double v, string f) => double.IsNaN(v) || double.IsInfinity(v) ? "-" : v.ToString(f);
}

/// <summary>방식별 합격 표시 칩(Y/U/P/R).</summary>
public sealed record StemMark(string Letter, bool Pass, string Tip);

public sealed class StemRow(string stem, IReadOnlyList<BenchmarkItem> items)
{
    public string Stem { get; } = stem;
    public IReadOnlyList<BenchmarkItem> Items { get; } = items;
    public bool AllPass => Items.All(i => i.Err.Pass);
    public IReadOnlyList<StemMark> Marks { get; } = items
        .Select(i => new StemMark(WeldBenchmarkViewModel.MethodLetter(i.Method), i.Err.Pass,
            $"{WeldBenchmarkViewModel.MethodName(i.Method)} {(i.Err.Pass ? "합격" : i.Err.Detected ? "불합격" : "미검출")}"))
        .ToList();
}

public sealed class MethodCard
{
    /// <param name="managed">⑤ 추론·검증 경로 — 검출선이 양 끝점 2개라 커버리지가 의미 없어 표시하지 않는다.</param>
    public MethodCard(BenchmarkItem i, bool managed)
    {
        Title = WeldBenchmarkViewModel.MethodName(i.Method);
        Verdict = (i.Err.Pass ? "합격" : i.Err.Detected ? "불합격" : "미검출") + (i.Err.BranchMatched ? " · 가지 일치" : "");
        Detail = i.Err.Detected
            ? $"각도 {i.Err.AngleDeg:0.00}° · 중심 {SummaryRow.Fmt(i.Err.CenterOffsetPx, "0.0")}px · 평균거리 {i.Err.MeanDistPx:0.0}px" +
              (managed ? "" : $" · 커버리지 {i.Err.Coverage:P0}") + $" · {i.Millis}ms"
            : $"{i.Message ?? "검출 실패"} · {i.Millis}ms";
        IsPass = i.Err.Pass;
        if (i.Overlay is { Length: > 0 })
        {
            try { using var ms = new MemoryStream(i.Overlay); Image = new Bitmap(ms); }
            catch { Image = null; }
        }
    }

    public string Title { get; }
    public string Verdict { get; }
    public string Detail { get; }
    public bool IsPass { get; }
    public Bitmap? Image { get; }
}
