using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 검사 포인트 티칭(X-Y 평면). 기존 InspectionPoints.razor 이식.
/// X-Y 캔버스(auto-fit) + 6-DOF 포인트 표 + 코봇 실행 + 프로필 저장/불러오기 + CSV 입출력.
/// DrawingService(Scoped)는 작업마다 scope 를 연다. CSV 다운로드/업로드는 코드비하인드의 StorageProvider 로.
/// </summary>
public sealed partial class InspectionPointsViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CobotService _cobot;

    public ObservableCollection<XyPointVm> Points { get; } = new();
    public ObservableCollection<InspectionProfile> Profiles { get; } = new();

    private static readonly string[] SeamTypes = { "LINE", "CROSS", "CROSS3", "CORNER2", "CORNER3" };

    [ObservableProperty] private int _seamTypeIndex = 1;   // 기본 CROSS
    [ObservableProperty] private double _fovMm = 30;
    [ObservableProperty] private double _defaultZ = 400;
    [ObservableProperty] private int _selected = -1;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private int _selectedProfileId;   // 0 = none

    [ObservableProperty] private int _runTool = 1;
    [ObservableProperty] private int _runUser;
    [ObservableProperty] private int _runVel = 5;
    [ObservableProperty] private double _settleSec = 0.5;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _runMsg;
    [ObservableProperty] private bool _runErr;

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string? _csvMsg;
    [ObservableProperty] private bool _csvErr;

    // 뷰박스(plotted 좌표: py = -Y) — 캔버스 auto-fit.
    [ObservableProperty] private double _vbMinX = -50, _vbMinY = -50, _vbW = 100, _vbH = 100;
    /// <summary>플롯 변경 신호 — 뷰가 구독해 캔버스를 다시 그린다.</summary>
    [ObservableProperty] private int _plotVersion;

    public string SeamType => SeamTypes[Math.Clamp(SeamTypeIndex, 0, SeamTypes.Length - 1)];
    public bool IsCross => SeamType is "CROSS" or "CROSS3";
    public bool CobotConnected => _cobot.IsConnected;
    public int PointCount => Points.Count;

    // 십자 패턴 파라미터(간이)
    private double _crossArmMm = 180, _crossSpacingMm = 30, _crossPerpRzDeg = -90;

    private CancellationTokenSource? _runCts;

    public InspectionPointsViewModel(IServiceScopeFactory scopeFactory, CobotService cobot)
    {
        _scopeFactory = scopeFactory;
        _cobot = cobot;
    }

    public override async void OnActivated()
    {
        try
        {
            await ReloadProfiles();
        }
        catch (Exception ex) { Notify($"프로필 목록 로드 실패: {ex.Message}", true); }
        RecomputeViewBox();
    }

    partial void OnSeamTypeIndexChanged(int value) => OnPropertyChanged(nameof(IsCross));
    partial void OnFovMmChanged(double value) => RecomputeViewBox();

    [RelayCommand]
    private void AddPoint()
    {
        var p = Selected >= 0 && Selected < Points.Count
            ? new XyPointVm(this) { X = Points[Selected].X, Y = Points[Selected].Y, Z = Points[Selected].Z }
            : new XyPointVm(this) { X = 0, Y = 0, Z = DefaultZ };
        Points.Add(p);
        Selected = Points.Count - 1;
        AfterPointsChanged();
    }

    [RelayCommand]
    private void RemovePoint(XyPointVm p)
    {
        Points.Remove(p);
        if (Selected >= Points.Count) Selected = Points.Count - 1;
        AfterPointsChanged();
    }

    private async Task<XyPointVm?> ReadCurrentPoseAsync()
    {
        if (!_cobot.IsConnected) { Notify("코봇 RPC 미연결 — 캡처 불가.", true); return null; }
        try
        {
            var pose = await _cobot.Rpc.GetTcpPoseAsync();
            if (pose is not { Length: 6 }) { Notify("자세 읽기 실패 — 응답 형식 이상.", true); return null; }
            return new XyPointVm(this) { X = pose[0], Y = pose[1], Z = pose[2], Rx = pose[3], Theta = pose[4], Rz = pose[5] };
        }
        catch (Exception ex) { Notify($"캡처 실패: {ex.Message}", true); return null; }
    }

    [RelayCommand]
    private async Task CaptureCurrentPose()
    {
        if (IsRunning) return;
        if (await ReadCurrentPoseAsync() is not { } p) return;
        Points.Add(p);
        Selected = Points.Count - 1;
        AfterPointsChanged();
        Notify($"현재 자세 캡처: #{Points.Count}", false);
    }

    [RelayCommand]
    private async Task CaptureIntoSelected()
    {
        if (IsRunning || Selected < 0 || Selected >= Points.Count) return;
        if (await ReadCurrentPoseAsync() is not { } p) return;
        p.Surface = Points[Selected].Surface;
        Points[Selected] = p;
        AfterPointsChanged();
        Notify($"#{Selected + 1} 갱신: 현재 자세로 재교시", false);
    }

    [RelayCommand]
    private void FillCrossPattern()
    {
        var cx = Selected >= 0 && Selected < Points.Count ? Points[Selected].X : 0.0;
        var cy = Selected >= 0 && Selected < Points.Count ? Points[Selected].Y : 0.0;
        if (!CrossPatternGenerator.TryGenerate(new CrossPatternParams(_crossArmMm, _crossSpacingMm, _crossPerpRzDeg),
                out var wps, out var err))
        {
            Notify($"십자 생성 실패: {err}", true);
            return;
        }
        Points.Clear();
        foreach (var w in wps)
            Points.Add(new XyPointVm(this) { X = cx + w.X, Y = cy + w.Z, Z = DefaultZ, Rz = w.RzDeg });
        Selected = -1;
        AfterPointsChanged();
        Notify($"십자 {Points.Count}점 채움", false);
    }

    [RelayCommand]
    private async Task MoveToPoint(XyPointVm p)
    {
        if (IsRunning || !_cobot.IsConnected) return;
        Selected = Points.IndexOf(p);
        try
        {
            var rc = await _cobot.Rpc.MoveLAsync(p.Pose(), tool: RunTool, user: RunUser, vel: RunVel,
                acc: 100, ovl: 100, blendR: -1);
            RunErr = rc != 0;
            RunMsg = $"#{Selected + 1} 이동 (rc={rc})";
        }
        catch (Exception ex) { RunErr = true; RunMsg = $"이동 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RunPoints()
    {
        if (IsRunning || !_cobot.IsConnected || Points.Count < 1) return;
        IsRunning = true; RunErr = false; RunMsg = null;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        try
        {
            for (int i = 0; i < Points.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Selected = i;
                var rc = await _cobot.Rpc.MoveLAsync(Points[i].Pose(), tool: RunTool, user: RunUser, vel: RunVel,
                    acc: 100, ovl: 100, blendR: -1, ct: ct);
                RunMsg = $"#{i + 1} 이동 (rc={rc})";
                if (rc != 0) { RunErr = true; RunMsg = $"#{i + 1} 이동 실패 (rc={rc}) — 중단"; return; }
                if (SettleSec > 0) await Task.Delay(TimeSpan.FromSeconds(SettleSec), ct);
            }
            RunMsg = $"완료 — {Points.Count}점 이동";
        }
        catch (OperationCanceledException) { RunMsg = "정지됨"; }
        catch (Exception ex) { RunErr = true; RunMsg = $"실행 실패: {ex.Message}"; }
        finally
        {
            var cancelled = _runCts?.IsCancellationRequested ?? false;
            IsRunning = false; _runCts?.Dispose(); _runCts = null;
            if (RunUser != 0 && !cancelled) await TryReturnBaseFrameAsync();
        }
    }

    private async Task TryReturnBaseFrameAsync()
    {
        if (!_cobot.IsConnected) return;
        try
        {
            var rc = await _cobot.Rpc.ResetActiveFrameAsync(RunTool, 0);
            if (rc != 0) RunMsg += $" · 활성 좌표계 베이스 반납 실패 (rc={rc})";
        }
        catch { /* 로그만 — 무시 */ }
    }

    [RelayCommand]
    private void StopRun() => _runCts?.Cancel();

    // ── 프로필 ──
    [RelayCommand]
    private async Task SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(ProfileName) || Points.Count == 0) return;
        try
        {
            var wps = Points.Select(p => new InspectionWaypoint(
                X: p.X, Z: p.Z, Theta: p.Theta, ThetaManual: true,
                Surface: (byte)p.Surface, SurfaceManual: true, Y: p.Y, RzDeg: p.Rz, RxDeg: p.Rx)).ToList();
            var name = ProfileName.Trim();
            var existing = Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.SeamType, SeamType, StringComparison.OrdinalIgnoreCase));
            var profile = new InspectionProfile
            {
                Id = existing?.Id ?? 0,
                Name = name,
                SeamType = SeamType,
                PoseAbsolute = true,
                RunTool = RunTool, RunUser = RunUser, RunVel = RunVel, SettleDelaySec = SettleSec,
                ThMax = 180,
                WaypointsJson = System.Text.Json.JsonSerializer.Serialize(wps),
            };
            var saved = await WithDrawing(s => s.SaveProfileAsync(profile));
            await ReloadProfiles();
            SelectedProfileId = saved.Id;
            Notify($"저장 완료: [{SeamType}] {name} ({wps.Count}점)", false);
        }
        catch (Exception ex) { Notify($"저장 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private async Task LoadSelected()
    {
        if (SelectedProfileId == 0) return;
        try
        {
            var p = await WithDrawing(s => s.GetProfileAsync(SelectedProfileId));
            if (p is null) { Notify("프로필을 찾을 수 없습니다.", true); return; }
            var idx = Array.IndexOf(SeamTypes, p.SeamType);
            SeamTypeIndex = idx >= 0 ? idx : 1;
            ProfileName = p.Name;
            RunTool = p.RunTool; RunUser = p.RunUser; RunVel = p.RunVel; SettleSec = p.SettleDelaySec;
            var wps = System.Text.Json.JsonSerializer.Deserialize<List<InspectionWaypoint>>(p.WaypointsJson) ?? new();
            Points.Clear();
            foreach (var w in wps)
                Points.Add(new XyPointVm(this) { X = w.X, Y = w.Y, Z = w.Z, Rx = w.RxDeg, Theta = w.Theta, Rz = w.RzDeg, Surface = w.Surface });
            Selected = -1;
            AfterPointsChanged();
            Notify($"불러오기 완료: {p.Name} ({Points.Count}점)", false);
        }
        catch (Exception ex) { Notify($"불러오기 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        if (SelectedProfileId == 0) return;
        try
        {
            await WithDrawing(s => s.DeleteProfileAsync(SelectedProfileId));
            await ReloadProfiles();
            SelectedProfileId = 0;
            Notify("삭제 완료", false);
        }
        catch (Exception ex) { Notify($"삭제 실패: {ex.Message}", true); }
    }

    private async Task ReloadProfiles()
    {
        Profiles.Clear();
        foreach (var p in await WithDrawing(s => s.ListProfilesAsync()))
            Profiles.Add(p);
    }

    // ── CSV (코드비하인드가 파일 I/O 담당) ──
    private static readonly string[] CsvHeaderCols =
        { "X_mm", "Y_mm", "Z_mm(WD)", "Rx_deg", "Ry_deg", "Rz_deg", "Surface(0=Flat,1=Corner,2=Corrugation)" };

    public string BuildCsv(bool includeRows)
    {
        var sb = new StringBuilder();
        sb.Append("# HD.AMR 검사 경유점 · 좌표=mm, 각도=deg\r\n");
        sb.Append("# X,Y = 검사평면 좌표(mm) | Z = Working Distance(mm, +선창 내부)\r\n");
        sb.Append("# Rx,Ry,Rz = 툴 자세 오일러각(deg) | Ry = 틸트(θ)\r\n");
        sb.Append("# Surface: 0=Flat, 1=Corner, 2=Corrugation\r\n");
        sb.Append(string.Join(",", CsvHeaderCols)).Append("\r\n");
        if (includeRows)
            foreach (var p in Points)
                sb.Append(F(p.X)).Append(',').Append(F(p.Y)).Append(',').Append(F(p.Z)).Append(',')
                  .Append(F(p.Rx)).Append(',').Append(F(p.Theta)).Append(',').Append(F(p.Rz)).Append(',')
                  .Append(p.Surface).Append("\r\n");
        return sb.ToString();
    }

    public string TemplateCsv() => BuildCsv(false) + $"0,0,{F(DefaultZ)},0,0,0,0\r\n";

    public string SuggestedCsvName()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "waypoints" : ProfileName.Trim();
        return $"{Sanitize(name)}_{SeamType}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
    }

    public void ImportCsv(string text)
    {
        var parsed = ParseCsv(text, out var warn);
        if (parsed is null) { NotifyCsv($"불러오기 실패 — {warn}", true); return; }
        Points.Clear();
        foreach (var p in parsed) Points.Add(p);
        Selected = -1;
        AfterPointsChanged();
        NotifyCsv(string.IsNullOrEmpty(warn) ? $"불러오기 완료: {Points.Count}점" : $"불러오기 완료: {Points.Count}점 ({warn})", false);
    }

    public void NotifyCsv(string msg, bool error) { CsvMsg = msg; CsvErr = error; }

    private List<XyPointVm>? ParseCsv(string text, out string warn)
    {
        warn = "";
        var list = new List<XyPointVm>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int skipped = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var c = line.Split(',');
            if (!double.TryParse(c[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x)) continue;
            if (c.Length < 6) { skipped++; continue; }
            list.Add(new XyPointVm(this)
            {
                X = x, Y = D(c[1]), Z = D(c[2]), Rx = D(c[3]), Theta = D(c[4]), Rz = D(c[5]),
                Surface = c.Length > 6 && byte.TryParse(c[6].Trim(), out var s) && s <= 2 ? s : 0,
            });
        }
        if (list.Count == 0) { warn = "유효한 데이터 행이 없습니다."; return null; }
        if (skipped > 0) warn = $"{skipped}개 행 건너뜀";
        return list;
    }

    // ── 뷰박스 / 헬퍼 ──
    public void AfterPointsChanged()
    {
        for (var i = 0; i < Points.Count; i++) Points[i].Index = i + 1;   // 표시용 # 재번호
        RecomputeViewBox();
        OnPropertyChanged(nameof(PointCount));
        PlotVersion++;
    }

    public void RecomputeViewBox()
    {
        if (Points.Count == 0)
        {
            var h0 = Math.Max(FovMm, 50);
            VbMinX = -h0; VbMinY = -h0; VbW = 2 * h0; VbH = 2 * h0;
        }
        else
        {
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minPY = double.PositiveInfinity, maxPY = double.NegativeInfinity;
            foreach (var p in Points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minPY = Math.Min(minPY, -p.Y); maxPY = Math.Max(maxPY, -p.Y);
            }
            var pad = FovMm * 0.75 + 10;
            minX = Math.Min(minX, 0); maxX = Math.Max(maxX, 0);
            minPY = Math.Min(minPY, 0); maxPY = Math.Max(maxPY, 0);
            VbMinX = minX - pad; VbW = (maxX - minX) + 2 * pad;
            VbMinY = minPY - pad; VbH = (maxPY - minPY) + 2 * pad;
        }
        PlotVersion++;
    }

    private async Task<T> WithDrawing<T>(Func<DrawingService, Task<T>> op)
    {
        using var scope = _scopeFactory.CreateScope();
        return await op(scope.ServiceProvider.GetRequiredService<DrawingService>());
    }
    private async Task WithDrawing(Func<DrawingService, Task> op)
    {
        using var scope = _scopeFactory.CreateScope();
        await op(scope.ServiceProvider.GetRequiredService<DrawingService>());
    }

    private void Notify(string msg, bool error) { Message = msg; IsError = error; }
    internal static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static double D(object? v) => double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
    private static string Sanitize(string s)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
    }

    public override void Dispose() { base.Dispose(); _runCts?.Cancel(); }
}

/// <summary>편집 가능한 6-DOF 검사 포인트. 좌표 변경 시 캔버스/뷰박스 갱신.</summary>
public sealed partial class XyPointVm : ObservableObject
{
    private readonly InspectionPointsViewModel _owner;
    public XyPointVm(InspectionPointsViewModel owner) => _owner = owner;

    [ObservableProperty] private int _index;      // 표시용 # (1-based)
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _z;
    [ObservableProperty] private double _rx;
    [ObservableProperty] private double _theta;   // 툴 RY
    [ObservableProperty] private double _rz;
    [ObservableProperty] private int _surface;    // 0 Flat, 1 Corner, 2 Corrugation

    public double[] Pose() => new[] { X, Y, Z, Rx, Theta, Rz };

    partial void OnXChanged(double value) => _owner.AfterPointsChanged();
    partial void OnYChanged(double value) => _owner.AfterPointsChanged();
    partial void OnZChanged(double value) => _owner.RecomputeViewBox();  // Z 는 라벨만 — 뷰박스 재계산으로 충분
}
