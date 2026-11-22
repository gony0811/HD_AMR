using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication.GasDetector;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Models;
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
    private readonly VisionInterfaceService _vision;
    private readonly GasDetectorService _gas;
    /// <summary>가스 검사 AMR(InspectionSensor:Mode=GasDetector) — 비전이 없으므로 수동 실행은 CAPTURE_REQ 대신 경유점마다 가스를 측정한다.</summary>
    private readonly bool _useGas;

    public ObservableCollection<XyPointVm> Points { get; } = new();
    public ObservableCollection<InspectionProfile> Profiles { get; } = new();

    private static readonly string[] SeamTypes = { "LINE", "CROSS", "CROSS3_R0", "CROSS3_R90", "CROSS3_R180", "CROSS3_R270", "CORNER2", "CORNER3" };

    [ObservableProperty] private int _seamTypeIndex = 1;   // 기본 CROSS
    [ObservableProperty] private double _fovMm = 30;
    [ObservableProperty] private double _defaultZ = 400;
    [ObservableProperty] private int _selected = -1;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private int _selectedProfileId;   // 0 = none

    [ObservableProperty] private int _runTool = 1;
    [ObservableProperty] private int _runUser;
    /// <summary>검사 속도 [%] — 이 화면의 실행과, 프로파일에 저장되어 ⑱ 검사 수행의 경유점 순회 속도로 쓰인다. 새 프로파일 기본 2.</summary>
    [ObservableProperty] private int _runVel = HD.AMR.App.Service.Sequence.Steps.InspectionRunStep.DefaultInspectVelPercent;
    [ObservableProperty] private double _settleSec = 0.5;
    /// <summary>ACS 검사 시 비전 CAPTURE_REQ 응답 대기(초) — 프로필 DelaySec. 드라이런에는 쓰이지 않음.</summary>
    [ObservableProperty] private double _visionTimeoutSec = HD.AMR.App.Service.Sequence.Steps.InspectionRunStep.DefaultVisionTimeoutSec;
    /// <summary>CAPTURE_REQ Wall ID(Surface ID). 선택 목록은 Wall Code 정본 표(<see cref="WallCodes"/>)에서 만든다.</summary>
    [ObservableProperty] private int _wallId = 1;

    /// <summary>Wall ID 선택 목록 — "0x01 · B 바닥" 형태. 현재 값이 정본 표에 없으면 맨 앞에 그대로 표시한다.</summary>
    public ObservableCollection<SurfaceOption> WallOptions { get; } = new();

    partial void OnWallIdChanged(int value) => RebuildWallOptions();

    private void RebuildWallOptions()
    {
        var wanted = WallCodes.All.Select(w => w.SurfaceId).ToList();
        if (WallCodes.FindBySurfaceId(WallId) is null) wanted.Insert(0, WallId);
        if (WallOptions.Select(o => o.SurfaceId).SequenceEqual(wanted)) return;
        WallOptions.Clear();
        foreach (var id in wanted)
            WallOptions.Add(new SurfaceOption(id, WallCodes.FindBySurfaceId(id) is null
                ? $"{TeachingService.SurfaceLabel(id)} (정본 표에 없음)"
                : TeachingService.SurfaceLabel(id)));
    }
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
    public bool IsCross => SeamType is "CROSS" || SeamType.StartsWith("CROSS3");
    public bool IsLine => SeamType == "LINE";
    public bool CobotConnected => _cobot.IsConnected;
    public bool VisionConnected => _vision.Client.IsConnected;
    /// <summary>비전 촬영(CAPTURE_REQ)을 쓰는 AMR 인가 — 가스 검사 AMR 이면 false(촬영 관련 UI 숨김).</summary>
    public bool UsesVision => !_useGas;
    public bool ShowVisionWarning => UsesVision && !VisionConnected;
    public bool ShowGasWarning => _useGas && !_gas.IsConnected;
    public string RunHint => _useGas
        ? "가스 디텍터로 측정해 현재 임계값으로 판정합니다(초과 NG, NG 시 재측정) — 측정 실패·NG 는 표시하고 계속 실행합니다. 측정 방식(가스 디텍터 화면)이 '이동 중 연속'이면 첫 점만 멈춰 재고 이후는 경유점 사이를 이동하는 동안 연속 측정하며, 아니면 각 포인트에 멈춰 잽니다. 블렌딩 반경(가스 디텍터 설정)이 0 보다 크면 연속 측정 때 경유점에 멈추지 않고 끝까지 한 번에 지나갑니다(무정지 — 수동 실행은 NG 구간 재측정 없음)."
        : "각 포인트 이동·정착 후 비전 인터페이스로 CAPTURE_REQ를 전송하고 CAPTURE_RES를 기다립니다. 비전 실패는 표시하고 다음 포인트를 계속 실행합니다.";
    public int PointCount => Points.Count;

    // 십자 패턴 파라미터(간이)
    private double _crossArmMm = 180, _crossSpacingMm = 30, _crossPerpRzDeg = -90;

    /// <summary>코로게이션 1피치(Top→Top) 채우기 — 단면을 따라 잰 측정 간격 [mm].</summary>
    [ObservableProperty] private double _corrugSpacingMm = 2.0;
    /// <summary>코로게이션 채우기 — 팁 이격 [mm](작업물 Z+ 로 띄움). 0 = TCP 가 표면 위.</summary>
    [ObservableProperty] private double _corrugStandoffMm;
    /// <summary>코로게이션 채우기 — 옆면 여유 [mm]. 평탄부에서 이만큼 일찍 올라가기 시작하고, 여유는 Top 으로 갈수록 줄어 Top 에서 0. 0 = 도면 그대로.</summary>
    [ObservableProperty] private double _corrugFlankClearanceMm;

    private CancellationTokenSource? _runCts;

    public InspectionPointsViewModel(IServiceScopeFactory scopeFactory, CobotService cobot,
        VisionInterfaceService vision, GasDetectorService gas,
        Microsoft.Extensions.Options.IOptions<InspectionSensorSettings> sensor,
        AcsTestCaptureTarget? testTarget = null)
    {
        _gas = gas;
        _useGas = sensor.Value.UsesGasDetector;
        _scopeFactory = scopeFactory;
        _cobot = cobot;
        _vision = vision;
        _testTarget = testTarget;
        RebuildWallOptions();
        if (_testTarget is not null)
        {
            _testTarget.Changed += t => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowTestTarget(t));
            ShowTestTarget(_testTarget.Current);
        }
    }

    private readonly AcsTestCaptureTarget? _testTarget;

    /// <summary>ACS 코봇 이동 시험으로 도달한 용접선의 taskId·attempt 를 수동 촬영(CAPTURE_REQ)에 싣는다 — 시험 도달 때 자동으로 켜진다.</summary>
    [ObservableProperty] private bool _useAcsTestTask;
    /// <summary>보관 중인 시험 용접선 표기(없으면 null — 체크 숨김).</summary>
    [ObservableProperty] private string? _acsTestTaskText;
    public bool HasAcsTestTask => AcsTestTaskText is not null && UsesVision;
    partial void OnAcsTestTaskTextChanged(string? value) => OnPropertyChanged(nameof(HasAcsTestTask));

    private void ShowTestTarget(AcsTestCaptureTarget.Target? t)
    {
        AcsTestTaskText = t?.Describe();
        UseAcsTestTask = t is not null;
    }

    /// <summary>수동 촬영 식별자 — 시험 용접선을 쓰면 그 taskId·attempt·이어지는 captureSeq, 아니면 (빈 taskId, 1, 화면 발번).</summary>
    private (Guid TaskId, byte Attempt, ushort Seq) CaptureIds(Func<ushort> fallbackSeq) =>
        _testTarget?.IdsForManualCapture(UseAcsTestTask, fallbackSeq) ?? (Guid.Empty, (byte)1, fallbackSeq());

    public override async void OnActivated()
    {
        try
        {
            await ReloadProfiles();
        }
        catch (Exception ex) { Notify($"프로필 목록 로드 실패: {ex.Message}", true); }
        RecomputeViewBox();
    }

    partial void OnSeamTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCross));
        OnPropertyChanged(nameof(IsLine));
    }
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

    /// <summary>기준 행 위에 새 행을 끼워 넣는다 — 앞뒤 행의 중간값으로 채운다.</summary>
    [RelayCommand]
    private void InsertPointAbove(XyPointVm p) => InsertPoint(p, above: true);

    /// <summary>기준 행 아래에 새 행을 끼워 넣는다.</summary>
    [RelayCommand]
    private void InsertPointBelow(XyPointVm p) => InsertPoint(p, above: false);

    private void InsertPoint(XyPointVm p, bool above)
    {
        if (IsRunning) return;
        var at = HD.AMR.App.Service.Inspection.InspectionPointInsert.InsertIndex(Points.Count, Points.IndexOf(p), above);
        var poses = Points.Select(x => x.Pose()).ToList();
        var pose = HD.AMR.App.Service.Inspection.InspectionPointInsert.NewPose(poses, at)
                   ?? new[] { 0.0, 0.0, DefaultZ, 0.0, 0.0, 0.0 };
        var from = HD.AMR.App.Service.Inspection.InspectionPointInsert.InheritFrom(Points.Count, at);
        var added = new XyPointVm(this)
        {
            X = pose[0], Y = pose[1], Z = pose[2], Rx = pose[3], Theta = pose[4], Rz = pose[5],
            Surface = from >= 0 ? Points[from].Surface : 0,
        };
        Points.Insert(at, added);
        Selected = at;
        AfterPointsChanged();
        Notify($"#{at + 1} 자리에 행 삽입 ({Points.Count}점)", false);
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

    /// <summary>
    /// 코로게이션 1피치(점1 Top X=0 → 점2 Top X=pitch) 가스 검사 경로로 포인트를 채운다(MEMBRANE-A 도면 형상).
    /// 손목은 평면 수직 고정(Rx=θ=Rz=0) — 팁 위치만 곡선을 따라간다. 저장하면 절대 6-DOF LINE 프로필이 된다.
    /// </summary>
    [RelayCommand]
    private void FillCorrugationPitch()
    {
        var opts = new CorrugationSamplingOptions(CorrugSpacingMm, CorrugStandoffMm, CorrugFlankClearanceMm);
        if (!CorrugationProfileGenerator.TryGenerate(CorrugationProfileShape.MembraneA, opts, out var wps, out var err))
        {
            Notify($"코로게이션 생성 실패: {err}", true);
            return;
        }
        Points.Clear();
        foreach (var w in wps)
            Points.Add(new XyPointVm(this) { X = w.X, Y = w.Y, Z = w.Z, Rx = 0, Theta = 0, Rz = 0, Surface = w.Surface });
        Selected = -1;
        AfterPointsChanged();
        var spacing = CorrugationProfileGenerator.Length(CorrugationProfileShape.MembraneA, CorrugFlankClearanceMm) / (wps.Count - 1);
        Notify($"코로게이션 1피치 {Points.Count}점 채움 (곡선 간격 {spacing:0.000}mm, 이격 {CorrugStandoffMm:0.#}mm, 옆면 여유 {CorrugFlankClearanceMm:0.#}mm, 손목 평면 수직 고정)", false);
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

    private ushort _manualCaptureSeq;

    /// <summary>이동 없이 해당 포인트의 좌표·Surface 로 CAPTURE_REQ 를 1회 전송한다. 가스 검사 AMR 은 현재 위치에서 가스를 1회 측정한다.</summary>
    [RelayCommand]
    private async Task InspectPoint(XyPointVm p)
    {
        if (IsRunning) return;
        if (_useGas)
        {
            if (!_gas.IsConnected) { RunErr = true; RunMsg = "가스 디텍터 미연결 — 측정할 수 없습니다."; return; }
            Selected = Points.IndexOf(p);
            try
            {
                RunErr = false; RunMsg = $"#{Selected + 1} 가스 측정 중...";
                var (judgement, text) = await _gas.MeasurePointForDisplayAsync($"수동 #{Selected + 1}");
                RunErr = judgement != GasJudgement.Ok;
                RunMsg = $"#{Selected + 1} {text}";
            }
            catch (Exception ex) { RunErr = true; RunMsg = $"가스 측정 실패: {ex.Message}"; }
            return;
        }
        if (!_vision.Client.IsConnected)
        {
            RunErr = true;
            RunMsg = "비전 인터페이스 미연결 — CAPTURE_REQ를 전송할 수 없습니다.";
            return;
        }
        Selected = Points.IndexOf(p);
        try
        {
            var wallId = (ushort)Math.Clamp(WallId, 1, 10);
            var (u, v, rawH) = FaceLocalMapper.ToFaceLocal(wallId, p.X, p.Y, p.Z);
            var h = FaceLocalMapper.CaptureHeight(rawH);   // 비전은 음수 높이를 받지 않는다
            var (tid, att, seq) = CaptureIds(() => ++_manualCaptureSeq);
            var data = CaptureReqPayload.Build(SurfaceTypes.OrFlat(p.Surface), wallId,
                u, v, h, tid, att, seq);
            var timeout = TimeSpan.FromSeconds(Math.Max(.5, VisionTimeoutSec));
            RunErr = false; RunMsg = $"#{Selected + 1} CAPTURE_REQ 전송...";
            var outcome = await _vision.Client.RequestCaptureAsync(data, timeout);
            RunErr = !outcome.Success;
            RunMsg = outcome.Success
                ? $"#{Selected + 1} 검사 CAPTURE_REQ 성공"
                : $"#{Selected + 1} 검사 실패 — {CaptureFailure(outcome)}";
        }
        catch (Exception ex) { RunErr = true; RunMsg = $"검사 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RunPoints()
    {
        if (IsRunning || !_cobot.IsConnected || Points.Count < 1) return;
        if (_useGas && !_gas.IsConnected)
        {
            RunErr = true;
            RunMsg = "가스 디텍터 미연결 — 측정을 실행할 수 없습니다.";
            return;
        }
        if (!_useGas && !_vision.Client.IsConnected)
        {
            RunErr = true;
            RunMsg = "비전 인터페이스 미연결 — CAPTURE_REQ를 실행할 수 없습니다.";
            return;
        }
        IsRunning = true; RunErr = false; RunMsg = null;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        try
        {
            int visionOk = 0, visionFail = 0;
            int gasOk = 0, gasNg = 0, gasFail = 0;
            ushort captureSeq = 0;
            var timeout = TimeSpan.FromSeconds(Math.Max(.5, VisionTimeoutSec));
            // 이동 중 연속 측정 여부는 실행 시작 시 한 번 정한다(가스 디텍터 화면 설정).
            var gasContinuous = _useGas && _gas.IsContinuousMode;
            var gasNonStop = gasContinuous && _gas.IsNonStopMode;   // 블렌딩 반경 > 0 — 경유점 무정지
            var gasNote = "";
            for (int i = 0; i < Points.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Selected = i;
                // 연속 측정: 앞 포인트 → 이 포인트 구간을 이동하면서 잰다(멈춰서 재지 않음). 첫 포인트는 앞 구간이 없어 아래 정지 측정.
                // 무정지: 남은 포인트 전체를 멈추지 않고 한 번에 지나가며 재고 끝낸다.
                if (gasNonStop && i > 0)
                {
                    var passPoses = Points.Skip(i - 1).Select(x => x.Pose()).ToList();   // [0] = 지금 서 있는 포인트
                    var passLabels = Enumerable.Range(i, Points.Count - i).Select(k => $"수동 #{k}→#{k + 1}").ToList();
                    RunMsg = $"#{i}→#{Points.Count} 무정지 이동·가스 연속 측정 중...";
                    IReadOnlyList<double[]> chain;
                    try { chain = await _cobot.Rpc.ResolveChainJointsAsync(passPoses.Skip(1).ToList(), RunTool, RunUser, ct); }
                    catch (InvalidOperationException ex) { RunErr = true; RunMsg = $"무정지 경로 IK 실패: {ex.Message} — 중단"; return; }
                    var (passScans, passError) = await _gas.MeasureNonStopPassAsync(passLabels, passPoses,
                        (k, blendR, c) => _cobot.Rpc.MoveLAsync(passPoses[k], jointPos: chain[k - 1], tool: RunTool, user: RunUser,
                            vel: RunVel, acc: 100, ovl: 100, blendR: blendR, ct: c),
                        c => _cobot.Rpc.GetTcpPoseAsync(ct: c), () => _cobot.StopMotionImmediateAsync(), ct: ct);
                    if (passScans is null) { RunErr = true; RunMsg = $"무정지 이동 실패 — {passError} — 중단"; return; }
                    gasOk += passScans.Count(x => x.Judgement == GasJudgement.Ok);
                    gasNg += passScans.Count(x => x.Judgement == GasJudgement.Ng);
                    gasFail += passScans.Count(x => x.Judgement == GasJudgement.Error);
                    var bad = passScans.Where(x => x.Judgement != GasJudgement.Ok)
                        .Select(x => x.Label.Replace("수동 ", "") + (x.Judgement == GasJudgement.Ng ? $" NG {x.Value:0.###}" : " 실패")).ToList();
                    if (bad.Count > 0) gasNote = " — " + string.Join(", ", bad);
                    Selected = Points.Count - 1;
                    break;
                }
                if (gasContinuous && i > 0)
                {
                    var segRc = 0;
                    var to = Points[i].Pose();
                    var from = Points[i - 1].Pose();
                    RunMsg = $"구간 #{i}→#{i + 1} 이동·가스 연속 측정 중...";
                    var seg = await _gas.MeasureSegmentForDisplayAsync($"수동 #{i}→#{i + 1}",
                        async c => (segRc = await _cobot.Rpc.MoveLAsync(to, tool: RunTool, user: RunUser, vel: RunVel,
                            acc: 100, ovl: 100, blendR: -1, ct: c)) == 0,
                        async c => (segRc = await _cobot.Rpc.MoveLAsync(from, tool: RunTool, user: RunUser, vel: RunVel,
                            acc: 100, ovl: 100, blendR: -1, ct: c)) == 0,
                        ct);
                    if (seg is not { } s) { RunErr = true; RunMsg = $"구간 #{i}→#{i + 1} 이동 실패 (rc={segRc}) — 중단"; return; }
                    if (s.Judgement == GasJudgement.Ok) gasOk++;
                    else if (s.Judgement == GasJudgement.Ng) gasNg++;
                    else gasFail++;
                    RunErr = s.Judgement != GasJudgement.Ok;
                    RunMsg = $"구간 #{i}→#{i + 1} {s.Text}" + (s.Judgement == GasJudgement.Ok ? "" : " (다음 구간 계속)");
                    continue;
                }
                var rc = await _cobot.Rpc.MoveLAsync(Points[i].Pose(), tool: RunTool, user: RunUser, vel: RunVel,
                    acc: 100, ovl: 100, blendR: -1, ct: ct);
                RunMsg = $"#{i + 1} 이동 (rc={rc})";
                if (rc != 0) { RunErr = true; RunMsg = $"#{i + 1} 이동 실패 (rc={rc}) — 중단"; return; }
                if (SettleSec > 0) await Task.Delay(TimeSpan.FromSeconds(SettleSec), ct);
                if (_useGas)   // 가스 검사 AMR — 비전 촬영 요청 대신 경유점 가스 측정
                {
                    RunMsg = $"#{i + 1} 가스 측정 중...";
                    var (judgement, text) = await _gas.MeasurePointForDisplayAsync($"수동 #{i + 1}", ct);
                    if (judgement == GasJudgement.Ok) gasOk++;
                    else if (judgement == GasJudgement.Ng) gasNg++;
                    else gasFail++;
                    RunErr = judgement != GasJudgement.Ok;
                    RunMsg = $"#{i + 1} {text}" + (judgement == GasJudgement.Ok ? "" : " (다음 점 계속)");
                    continue;
                }

                var p = Points[i];
                var wallId = (ushort)Math.Clamp(WallId, 1, 10);
                var (u, v, rawH) = FaceLocalMapper.ToFaceLocal(wallId, p.X, p.Y, p.Z);
                var h = FaceLocalMapper.CaptureHeight(rawH);   // 비전은 음수 높이를 받지 않는다
                var (tid, att, seq) = CaptureIds(() => ++captureSeq);
                var data = CaptureReqPayload.Build(SurfaceTypes.OrFlat(p.Surface), wallId,
                    u, v, h, tid, att, seq);
                var outcome = await _vision.Client.RequestCaptureAsync(data, timeout, ct);
                if (outcome.Success)
                {
                    visionOk++;
                    RunMsg = $"#{i + 1} 이동·CAPTURE_REQ 성공 ({visionOk}/{i + 1})";
                }
                else
                {
                    visionFail++;
                    RunErr = true;
                    RunMsg = $"#{i + 1} 비전 실패 — {CaptureFailure(outcome)} (다음 점 계속)";
                }
            }
            RunErr = _useGas ? gasNg + gasFail > 0 : visionFail > 0;
            RunMsg = _useGas
                ? $"완료 — {Points.Count}점 이동{(gasNonStop ? "(무정지 연속 측정)" : gasContinuous ? "(연속 측정)" : "")}, 가스 OK {gasOk}/{Points.Count}" +
                  (gasNg + gasFail > 0 ? $" (NG {gasNg}, 실패 {gasFail})" : "") + gasNote
                : $"완료 — {Points.Count}점 이동, 비전 OK {visionOk}/{Points.Count}" +
                  (visionFail > 0 ? $" (실패 {visionFail})" : "");
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

    private static string CaptureFailure(CaptureOutcome outcome)
    {
        if (!outcome.Sent) return "비전 인터페이스 미연결/전송 실패";
        if (!outcome.Responded) return "응답 시간 초과";
        return outcome.Code is { } code
            ? $"응답 {ResultCodeNames.NameOf((ushort)code)}"
            : "응답 코드 없음";
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
                DelaySec = VisionTimeoutSec > 0 ? VisionTimeoutSec : HD.AMR.App.Service.Sequence.Steps.InspectionRunStep.DefaultVisionTimeoutSec,
                ThMax = 180,
                WaypointsJson = System.Text.Json.JsonSerializer.Serialize(wps),
            };
            var saved = await WithDrawing(s => s.SaveProfileAsync(profile));
            await ReloadProfiles();
            SelectedProfileId = saved.Id;
            var usedBy = await WithDrawing(s => s.ListRecipesUsingProfileAsync(saved.Id));
            Notify($"저장 완료: [{SeamType}] {name} ({wps.Count}점)" +
                   (usedBy.Count > 0
                       ? $" — 레시피 {string.Join(", ", usedBy)} 에 지정된 프로필이라 ACS 실행에 즉시 반영됩니다."
                       : " — ACS 에 쓰려면 검사 레시피 페이지에서 지정하세요."), false);
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
            // 레거시 bare "CROSS3" 프로필은 0° 로 승격해 드롭다운과 정합을 맞춘다.
            var loadedSeam = p.SeamType == "CROSS3" ? "CROSS3_R0" : p.SeamType;
            var idx = Array.IndexOf(SeamTypes, loadedSeam);
            SeamTypeIndex = idx >= 0 ? idx : 1;
            ProfileName = p.Name;
            RunTool = p.RunTool; RunUser = p.RunUser; RunVel = p.RunVel; SettleSec = p.SettleDelaySec;
            VisionTimeoutSec = p.DelaySec > 0 ? p.DelaySec : HD.AMR.App.Service.Sequence.Steps.InspectionRunStep.DefaultVisionTimeoutSec;
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
            var id = SelectedProfileId;
            var cleared = await WithDrawing(s => s.DeleteProfileAsync(id));
            await ReloadProfiles();
            SelectedProfileId = 0;
            Notify(cleared.Count > 0
                ? $"삭제 완료 — 레시피 {string.Join(", ", cleared)} 의 티칭 프로필 지정이 해제되었습니다(재지정 전까지 해당 액션 FAILED)."
                : "삭제 완료", cleared.Count > 0);
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
        { "X_mm", "Y_mm", "Z_mm(WD)", "Rx_deg", "Ry_deg", "Rz_deg", "Surface(SurfaceType 0~9)" };

    public string BuildCsv(bool includeRows)
    {
        var sb = new StringBuilder();
        sb.Append("# HD.AMR 검사 경유점 · 좌표=mm, 각도=deg\r\n");
        sb.Append("# X,Y = 검사평면 좌표(mm) | Z = Working Distance(mm, +선창 내부)\r\n");
        sb.Append("# Rx,Ry,Rz = 툴 자세 오일러각(deg) | Ry = 틸트(θ)\r\n");
        sb.Append("# Surface: ").Append(SurfaceTypes.Legend()).Append("\r\n");
        sb.Append(SurfaceTypes.CsvSchemaMarker).Append("   (이 행이 없는 구 CSV 는 1=Corner→0, 2=Corrugation→1 로 변환)\r\n");
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
        var currentSchema = SurfaceTypes.IsCurrentSchemaCsv(text);
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
                Surface = c.Length > 6 ? SurfaceTypes.ParseCsvCell(c[6], currentSchema) : 0,
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
