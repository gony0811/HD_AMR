using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// ⑤ 리치 맵 — 정차 거리(x)·가로(y)·높이(z)·텔레스코픽(h) 격자를 역기구학으로 훑어
/// "어디에 서면 어디까지 닿는가" 를 CSV 로 남긴다(<see cref="ReachMap"/>). 코봇은 움직이지 않는다.
/// 역기구학은 컨트롤러 또는 FR5-WML 모델(<see cref="Fr5Kinematics"/>, 코봇 미연결 가능) 중에 고른다.
/// </summary>
public sealed partial class SeamMoveTestViewModel
{
    private CancellationTokenSource? _reachCts;

    [ObservableProperty] private string _reachWallCodes = "SM,B,T,SU,SL";
    [ObservableProperty] private string _reachStrokes = "0,500,1000";
    [ObservableProperty] private double _reachXFrom = 800;
    [ObservableProperty] private double _reachXTo = 2000;
    [ObservableProperty] private double _reachXStep = 200;
    [ObservableProperty] private double _reachYFrom = -1200;
    [ObservableProperty] private double _reachYTo = 1200;
    [ObservableProperty] private double _reachYStep = 400;
    [ObservableProperty] private double _reachZFrom = 0;
    [ObservableProperty] private double _reachZTo = 4000;
    [ObservableProperty] private double _reachZStep = 500;
    [ObservableProperty] private double _reachApproachMm = CobotInspectionMoveStep.DefaultApproachStandoffMm;
    [ObservableProperty] private double _reachAlignMm = 300;
    [ObservableProperty] private double _reachFocusMm = 0;
    [ObservableProperty] private bool _reachOffline;
    [ObservableProperty] private bool _reachRunning;
    [ObservableProperty] private string _reachProgressText = "";
    [ObservableProperty] private string _reachSummaryText = "";

    /// <summary>정차 방식 — FRONT(종전) 또는 측면(측면 정차 검사 사양 §8).</summary>
    public IReadOnlyList<string> ReachModes { get; } = new[] { "FRONT", "SIDE_LEFT", "SIDE_RIGHT" };
    [ObservableProperty] private string _reachMode = "FRONT";
    [ObservableProperty] private decimal _reachExportVersion = 1;

    partial void OnReachModeChanged(string value)
    {
        // 측면은 수직벽만 — 기본 면 목록이 다른 면을 품고 있으면 수직벽 4종으로 바꿔 준다.
        if (value != "FRONT" && ReachWallCodes.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(w => !StationModes.IsSideWall(w.Trim().ToUpperInvariant())))
            ReachWallCodes = string.Join(",", StationModes.SideWallCodes);
        OnPropertyChanged(nameof(ReachSizeText));
    }

    [RelayCommand]
    private void ExportSideReach()
    {
        try
        {
            var dir = ReachMapService.OutputDirectory;
            if (!Directory.Exists(dir)) { Failure($"리치 맵 폴더가 없습니다: {dir}"); return; }
            // 방식별 최신 측면 맵 한 장씩(같은 방식이면 최신이 이긴다).
            var latest = new Dictionary<StationMode, (ReachMapFile File, string Path)>();
            foreach (var path in Directory.EnumerateFiles(dir, "reachmap*.csv").OrderBy(File.GetLastWriteTimeUtc))
            {
                var file = ReachMapCsv.Parse(File.ReadAllText(path));
                if (file is not null && file.Stamp.Mode.IsSide()) latest[file.Stamp.Mode] = (file, path);
            }
            if (latest.Count == 0) { Failure("측면 리치 맵(SIDE_LEFT/SIDE_RIGHT) CSV 가 없습니다 — 정차 방식을 측면으로 두고 먼저 생성하세요."); return; }
            if (latest.Count == 2 && !ReachMapCsv.StampMatches(latest[StationMode.SideLeft].File.Stamp,
                    latest[StationMode.SideRight].File.Stamp with { Mode = StationMode.SideLeft }))
            {
                Failure("좌/우 측면 맵의 장비 조건(장착·공구·이격 등)이 서로 다릅니다 — 같은 조건으로 다시 생성하세요.");
                return;
            }

            var strokes = latest.Values.SelectMany(v => v.File.Cells.Select(c => c.StrokeMm)).ToList();
            var version = (int)ReachExportVersion;
            var json = SideReachExport.Build(latest.Values.Select(v => v.File), version, DateTimeOffset.Now,
                (strokes.Min(), strokes.Max()));
            var outPath = Path.Combine(dir, $"side_reachmap_v{version}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(outPath, json);
            var missing = latest.Count == 2 ? "" : $" (주의: {(latest.ContainsKey(StationMode.SideLeft) ? "SIDE_RIGHT" : "SIDE_LEFT")} 맵 없음)";
            Success($"측면 리치 맵 JSON 저장: {outPath} — 원본 {string.Join(", ", latest.Values.Select(v => Path.GetFileName(v.Path)))}{missing}");
        }
        catch (Exception ex) { Failure($"측면 리치 맵 내보내기 실패: {ex.Message}"); }
    }

    /// <summary>입력 격자의 셀 수와 최대 IK 호출 수 — 시작 전에 규모를 보여 준다.</summary>
    public string ReachSizeText
    {
        get
        {
            var spec = TryBuildReachSpec(out var error);
            return spec is null ? error! : $"셀 {spec.CellCount}개 · IK 최대 {spec.CellCount * 2}회 (범위 밖 셀은 IK 생략)";
        }
    }

    partial void OnReachWallCodesChanged(string value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachStrokesChanged(string value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachXFromChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachXToChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachXStepChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachYFromChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachYToChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachYStepChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachZFromChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachZToChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));
    partial void OnReachZStepChanged(double value) => OnPropertyChanged(nameof(ReachSizeText));

    partial void OnReachOfflineChanged(bool value) => RunReachMapCommand.NotifyCanExecuteChanged();

    partial void OnReachRunningChanged(bool value)
    {
        RunReachMapCommand.NotifyCanExecuteChanged();
        CancelReachMapCommand.NotifyCanExecuteChanged();
    }

    private ReachMapSpec? TryBuildReachSpec(out string? error)
    {
        error = null;
        var walls = ReachWallCodes.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (walls.Length == 0) { error = "wall_code 를 하나 이상 입력하세요."; return null; }
        var unknown = walls.Where(w => WallCodes.Find(w) is null).ToArray();
        if (unknown.Length > 0) { error = $"미정의 wall_code: {string.Join(", ", unknown)}"; return null; }
        if (!StationModes.TryParse(ReachMode, out var mode, out var modeError)) { error = modeError; return null; }
        if (mode.IsSide() && walls.FirstOrDefault(w => !StationModes.IsSideWall(w)) is { } notSide)
        { error = $"측면 리치 맵은 수직벽(PM/SM/F/A)만 — {notSide}"; return null; }

        var strokes = new List<double>();
        foreach (var s in ReachStrokes.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(s, out var v) || v < 0) { error = $"스트로크 '{s}' 를 읽을 수 없습니다(0 이상 mm)."; return null; }
            strokes.Add(v);
        }
        if (strokes.Count == 0) { error = "스트로크를 하나 이상 입력하세요."; return null; }

        return new ReachMapSpec(
            walls, strokes.Distinct().ToArray(),
            ReachMapSpec.Range(ReachXFrom, ReachXTo, ReachXStep),
            ReachMapSpec.Range(ReachYFrom, ReachYTo, ReachYStep),
            ReachMapSpec.Range(ReachZFrom, ReachZTo, ReachZStep),
            ApproachStandoffMm: ReachApproachMm,
            AlignStandoffMm: ReachAlignMm,
            FocusStandoffMm: ReachFocusMm,
            Mode: mode);
    }

    private bool CanRunReachMap() => !ReachRunning && !Busy && (ReachOffline || CobotConnected);

    [RelayCommand(CanExecute = nameof(CanRunReachMap))]
    private async Task RunReachMap()
    {
        var spec = TryBuildReachSpec(out var error);
        if (spec is null) { Failure(error!); return; }

        ReachRunning = true;
        var offline = ReachOffline;
        if (!offline) Busy = true;   // TCP 실시간 읽기·이동을 멈춰 컨트롤러 RPC 를 IK 에 양보한다.
        ReachSummaryText = "";
        _reachCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ReachMapService>();
            var started = DateTime.Now;
            var progress = new Progress<ReachMapProgress>(p =>
            {
                var elapsed = DateTime.Now - started;
                var eta = p.Done > 0 ? TimeSpan.FromTicks(elapsed.Ticks * (p.Total - p.Done) / p.Done) : TimeSpan.Zero;
                ReachProgressText = $"{p.Done}/{p.Total} · IK {p.IkCalls}회 · 남은 시간 약 {eta:mm\\:ss} — " +
                                    $"[{p.Last.WallCode} h{p.Last.StrokeMm:0} x{p.Last.Xmm:0} y{p.Last.Ymm:0} z{p.Last.Zmm:0}] " +
                                    ReachMapResult.Label(p.Last.Status);
            });

            var (result, path) = offline
                ? await Task.Run(() => svc.RunOfflineAsync(spec, progress, _reachCts.Token))   // 계산만 — UI 스레드를 막지 않게
                : await svc.RunAsync(spec, Tool, progress, _reachCts.Token);
            ReachSummaryText = result.SummaryText();
            Success($"리치 맵 저장: {path}");
        }
        catch (OperationCanceledException) { Failure("리치 맵을 취소했습니다(저장하지 않음)."); }
        catch (Exception ex) { Failure($"리치 맵 실패: {ex.Message}"); }
        finally
        {
            _reachCts?.Dispose();
            _reachCts = null;
            if (!offline) Busy = false;
            ReachRunning = false;
        }
    }

    private bool CanCancelReachMap() => ReachRunning;

    [RelayCommand(CanExecute = nameof(CanCancelReachMap))]
    private void CancelReachMap() => _reachCts?.Cancel();
}
