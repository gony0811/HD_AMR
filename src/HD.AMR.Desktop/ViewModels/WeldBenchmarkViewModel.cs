using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Vision;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 용접선 검출 방식 일괄 비교평가. 선 라벨(정답)이 있는 촬영본 전체에 선택한 방식들을 돌려
/// 방식별 합격률·각도/중심 오차를 표로 보여 주고, 촬영본을 고르면 방식별 오버레이(정답=초록)를 나란히 보여 준다.
/// 결과는 싱글톤 서비스(LastReport)에 남아 페이지를 다시 열어도 유지된다.
/// </summary>
public sealed partial class WeldBenchmarkViewModel : ViewModelBase
{
    private readonly WeldDetectionBenchmarkService _bench;
    private CancellationTokenSource? _cts;

    public ObservableCollection<SummaryRow> Summaries { get; } = new();
    public ObservableCollection<StemRow> Stems { get; } = new();
    public ObservableCollection<MethodCard> Cards { get; } = new();

    [ObservableProperty] private int _modalityIndex = 1;       // 0 rgb, 1 ir
    [ObservableProperty] private bool _useParam = true;
    [ObservableProperty] private bool _useDl = true;
    [ObservableProperty] private bool _useRidge = true;
    [ObservableProperty] private bool _useUnet = true;
    [ObservableProperty] private bool _valOnly;
    [ObservableProperty] private bool _useWeldRoi;
    [ObservableProperty] private double _angleTol = WeldLineMetrics.DefaultAngleTolDeg;
    [ObservableProperty] private double _centerTol = WeldLineMetrics.DefaultCenterTolPx;
    [ObservableProperty] private bool _failOnly;
    [ObservableProperty] private int _lineFitIndex;              // 0 운영값, 1 RANSAC, 2 최소제곱
    [ObservableProperty] private int _labeledCount;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string? _reportInfo;
    [ObservableProperty] private StemRow? _selectedStem;

    public string Modality => ModalityIndex == 1 ? "ir" : "rgb";
    public bool HasReport => _bench.LastReport is not null;

    public WeldBenchmarkViewModel(WeldDetectionBenchmarkService bench) => _bench = bench;

    public override async void OnActivated()
    {
        if (_bench.LastReport is { } r) ShowReport(r);
        await RefreshCountAsync();
    }

    public override void OnDeactivated() { }

    async partial void OnModalityIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Modality));
        await RefreshCountAsync();
    }

    partial void OnFailOnlyChanged(bool value) { if (_bench.LastReport is { } r) FillStems(r); }

    partial void OnSelectedStemChanged(StemRow? value) => FillCards(value);

    [RelayCommand]
    private async Task RefreshCountAsync()
    {
        try { LabeledCount = (await _bench.ListLabeledStemsAsync(Modality)).Count; }
        catch (Exception ex) { Notify($"목록 조회 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Running) return;
        var methods = new List<WeldDetectionMethod>();
        if (UseParam) methods.Add(WeldDetectionMethod.Param);
        if (UseDl) methods.Add(WeldDetectionMethod.Dl);
        if (UseRidge) methods.Add(WeldDetectionMethod.Ridge);
        if (UseUnet) methods.Add(WeldDetectionMethod.Unet);
        if (methods.Count == 0) { Notify("비교할 검출 방식을 하나 이상 선택하세요.", true); return; }

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
            var opt = new BenchmarkOptions(Modality, methods, ValOnly, UseWeldRoi, AngleTol, CenterTol, fit);
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
        ModalityIndex = r.Modality == "ir" ? 1 : 0;
        Summaries.Clear();
        // 합격률 높은 순 → 1위가 맨 위.
        foreach (var s in r.Summaries.OrderByDescending(s => s.PassRate).ThenBy(s => s.OffsetMedian))
            Summaries.Add(new SummaryRow(s));
        ReportInfo = $"{r.At:yyyy-MM-dd HH:mm:ss} · {r.Modality.ToUpperInvariant()} · 촬영본 {r.ImageCount}장" +
                     $"{(r.Options.ValOnly ? " · val만" : "")}{(r.Options.UseWeldRoi ? " · Weld ROI" : " · 전체 프레임")}" +
                     $" · 직선 피팅 {(r.LineFit == WeldLineFitMode.Ransac ? "RANSAC" : "최소제곱")}" +
                     $" · 합격 기준 각도≤{r.Options.AngleTolDeg:0.#}° 중심≤{r.Options.CenterTolPx:0.#}px" +
                     (r.CsvPath is null ? "" : $"\nCSV: {r.CsvPath}");
        OnPropertyChanged(nameof(HasReport));
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
        foreach (var i in row.Items) Cards.Add(new MethodCard(i));
    }

    private void Notify(string m, bool err) { Message = m; IsError = err; }
}

public sealed class SummaryRow(MethodSummary s)
{
    public string Method => s.Method.ToString();
    public string PassRate => $"{s.PassRate:P0} ({s.Passed}/{s.Count})";
    public string DetectRate => $"{s.DetectRate:P0}";
    public string Angle => $"{Fmt(s.AngleMedian, "0.00")} / {Fmt(s.AngleP90, "0.00")}";
    public string Offset => $"{Fmt(s.OffsetMedian, "0.0")} / {Fmt(s.OffsetP90, "0.0")}";
    public string Coverage => double.IsNaN(s.CoverageMean) ? "-" : $"{s.CoverageMean:P0}";
    public string Millis => Fmt(s.MillisMean, "0");
    internal static string Fmt(double v, string f) => double.IsNaN(v) || double.IsInfinity(v) ? "-" : v.ToString(f);
}

public sealed class StemRow(string stem, IReadOnlyList<BenchmarkItem> items)
{
    public string Stem { get; } = stem;
    public IReadOnlyList<BenchmarkItem> Items { get; } = items;
    public bool AllPass => Items.All(i => i.Err.Pass);
    // 예: "✓P ✗D ✓R  012_20261105_101500"
    public string Label => string.Join(' ', Items.Select(i => (i.Err.Pass ? "✓" : "✗") + i.Method.ToString()[0])) + "  " + Stem;
}

public sealed class MethodCard
{
    public MethodCard(BenchmarkItem i)
    {
        Title = $"{i.Method} — {(i.Err.Pass ? "합격" : i.Err.Detected ? "불합격" : "미검출")}{(i.Err.BranchMatched ? " (가지 일치)" : "")}";
        Detail = i.Err.Detected
            ? $"각도 {i.Err.AngleDeg:0.00}° · 중심 {SummaryRow.Fmt(i.Err.CenterOffsetPx, "0.0")}px · 평균거리 {i.Err.MeanDistPx:0.0}px · 커버리지 {i.Err.Coverage:P0} · {i.Millis}ms"
            : $"{i.Message ?? "검출 실패"} · {i.Millis}ms";
        IsPass = i.Err.Pass;
        if (i.Overlay is { Length: > 0 })
        {
            try { using var ms = new MemoryStream(i.Overlay); Image = new Bitmap(ms); }
            catch { Image = null; }
        }
    }

    public string Title { get; }
    public string Detail { get; }
    public bool IsPass { get; }
    public Bitmap? Image { get; }
}
