using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 용접 위치 시험 — ACS 가 보낸 용접선 좌표(<c>seamStartW</c>, 맵 좌표)로 코봇 TOOL 이 실제로 가는지 확인한다.
///
/// 사슬: seam(맵 m) → z 기준 보정 → 면 법선(wall_code×theta) → standoff 후퇴 → <see cref="SeamBaseTransform"/>
/// → 코봇 BASE 위치·자세(광축이 면을 향함) → MoveL.
/// 계산은 <see cref="SeamBaseTransform"/> 한 곳에 있고(단위 테스트 대상) 이 뷰모델은 입력·읽기·이동만 맡는다.
///
/// <b>안전.</b> 이 페이지는 코봇을 실제로 움직인다. 그래서:
///  · 이동 목표는 용접선 그 점이 아니라 면에서 <see cref="StandoffMm"/> 물러난 <b>접근점</b>이다.
///  · 이동 직전에 AMR pose·스트로크를 다시 읽어 목표를 재계산한다(표시값이 낡아도 엉뚱한 곳으로 가지 않도록).
///  · AMR 이 정지해 있어야 하고, 안전 확인 체크와 역기구학 사전 점검을 통과해야 이동 버튼이 열린다.
///  · 수동 AMR pose(하드웨어 없이 계산 검증용)로는 이동하지 않는다 — 계산 전용이다.
///  · 자세를 바꾸는 이동이라 이동 전 현재 TCP 와의 자세 차이를 계산해 보여 주고, 큰 회전은 경고한다.
/// </summary>
public sealed partial class SeamMoveTestViewModel : ViewModelBase
{
    // 정지 판정 — MountCalibrationViewModel·QrLocalizationService 와 같은 기준.
    private const double MoveTolMm = 5.0;
    private const double MoveTolDeg = 0.2;
    private const int StationaryWindowMs = 2500;

    /// <summary>이동 속도 상한 [%] — 시험 이동은 느리게.</summary>
    private const double MaxVelPct = 30.0;

    /// <summary>이 각도를 넘는 자세 변경은 로그로 한 번 더 경고한다 [도].</summary>
    private const double LargeSwingDeg = 45.0;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly TelescopicService _lift;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    private readonly Queue<(DateTime T, double X, double Y, double Yaw)> _poseHistory = new();

    private double[] _mountAtHome = new double[6];
    private ToolAxisDir _opticalAxis = ToolAxisDir.PlusZ;
    private bool _opticalAxisFromParam;

    // 현재 TCP 실시간 읽기 — MountCalibrationViewModel 과 같은 방식(2틱마다, 3회 연속 실패 시 중단).
    private double[]? _tcp;
    private DateTime _tcpAt;
    private int _tcpFailStreak;
    private bool _tcpPolling;
    private int _tick;

    /// <summary>wall_code 선택 목록 — 정본 10종(<see cref="WallCodes"/>) + 미지정.</summary>
    public IReadOnlyList<string> WallCodeOptions { get; } =
        new[] { "" }.Concat(WallCodes.All.Select(w => w.Code)).ToArray();

    public SeamMoveTestViewModel(IServiceScopeFactory scopeFactory, AMRService amr, CobotService cobot,
        TelescopicService lift)
    {
        _scopeFactory = scopeFactory;
        _amr = amr;
        _cobot = cobot;
        _lift = lift;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => OnTick();
    }

    // ── 입력: 용접선(ACS 계약 단위 m) ────────────────────────────────
    [ObservableProperty] private double _seamStartX;
    [ObservableProperty] private double _seamStartY;
    [ObservableProperty] private double _seamStartZ;
    [ObservableProperty] private double _seamEndX;
    [ObservableProperty] private double _seamEndY;
    [ObservableProperty] private double _seamEndZ;
    [ObservableProperty] private bool _hasSeamEnd;

    /// <summary>VDA5050 `startWeldInspection` 액션 JSON 붙여넣기 — 실제 수신 전문으로 시험하기 위한 입력.</summary>
    [ObservableProperty] private string _actionJson = "";

    // ── 입력: 자세·보정 ─────────────────────────────────────────────
    /// <summary>정차 노드 theta [도] — 벽 정면 방향. 미사용 시 AMR yaw 를 쓴다.</summary>
    [ObservableProperty] private double _nodeThetaDeg;
    [ObservableProperty] private bool _useNodeTheta;

    /// <summary>도면 전역 z(선창 바닥 기준) → AMR 바닥 기준 보정 [mm]. 해당 층 바닥 높이(level_z)를 넣는다.</summary>
    [ObservableProperty] private double _zDatumOffsetMm;

    /// <summary>ACS `drawingPos.wall_code` — 면 법선(앙각)·TOOL 자세 결정 키. 빈 값이면 자세를 만들지 않는다.</summary>
    [ObservableProperty] private string? _wallCode;

    /// <summary>면 법선 자세로 이동할지 — 끄면 현재 TCP 자세를 유지하고 위치만 바꾼다(구 동작).</summary>
    [ObservableProperty] private bool _useWallNormalPose = true;

    /// <summary>광축 둘레 추가 회전 [도] — 0° = 용접선(없으면 벽면 수평) 방향 기준.</summary>
    [ObservableProperty] private double _toolSpinDeg;

    [ObservableProperty] private double _standoffMm = SeamBaseTransform.DefaultStandoffMm;
    [ObservableProperty] private int _tool = 1;
    [ObservableProperty] private double _velPct = 10;

    // 수동 AMR pose — 하드웨어 없이 계산 경로만 검증할 때. 이동은 막는다.
    [ObservableProperty] private bool _useManualAmrPose;
    [ObservableProperty] private double _manualAmrXm;
    [ObservableProperty] private double _manualAmrYm;
    [ObservableProperty] private double _manualAmrYawDeg;
    [ObservableProperty] private double _manualStrokeMm;

    /// <summary>현재 TCP 를 주기적으로 읽어 AMR·맵 좌표로 환산해 보여준다(코봇 RPC 읽기 전용).</summary>
    [ObservableProperty] private bool _liveTcp = true;

    [ObservableProperty] private bool _safetyConfirmed;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    // ── 계산 결과 ───────────────────────────────────────────────────
    [ObservableProperty] private SeamBaseTarget? _target;
    [ObservableProperty] private string _targetText = "용접선 좌표를 입력하면 환산 결과가 표시됩니다.";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private string _moveLog = "";

    /// <summary>경고 문구가 있는가 — 주의 카드 표시 조건.</summary>
    public bool HasNotes => !string.IsNullOrEmpty(NotesText);

    partial void OnNotesTextChanged(string value) => OnPropertyChanged(nameof(HasNotes));

    public override void OnActivated()
    {
        _timer.Start();
        OnTick();
        _ = LoadMountAsync();
    }

    public override void OnDeactivated() => _timer.Stop();

    public override void Dispose()
    {
        base.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task LoadMountAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            _mountAtHome = await calib.GetMountAsync();

            // 광축(대상을 향하는 툴축) — 카메라 페이지 설정값. 검사 시퀀스(③⑱)와 같은 규약을 쓴다.
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            (_opticalAxis, _opticalAxisFromParam) = await WeldSequenceSupport.GetDepthAxisAsync(param);

            OnPropertyChanged(nameof(MountText));
            OnPropertyChanged(nameof(MountCheckText));
            OnPropertyChanged(nameof(OpticalAxisText));
            OnPropertyChanged(nameof(SpinHintText));
            Recompute();
        }
        catch (Exception ex) { Failure($"장착 보정(T_A_B) 로드 실패: {ex.Message}"); }
    }

    // ── 실시간 읽기 ─────────────────────────────────────────────────
    private void OnTick()
    {
        if (_amr.LatestStatus is { } st)
        {
            var now = DateTime.UtcNow;
            _poseHistory.Enqueue((now, st.Pose.X * 1000.0, st.Pose.Y * 1000.0, st.Pose.Angle * 180.0 / Math.PI));
            while (_poseHistory.Count > 0 &&
                   (now - _poseHistory.Peek().T).TotalMilliseconds > StationaryWindowMs)
                _poseHistory.Dequeue();
        }
        else _poseHistory.Clear();

        if (++_tick % 2 == 0 && LiveTcp && CobotConnected && !Busy && !_tcpPolling)
            _ = PollTcpAsync();

        OnPropertyChanged(nameof(AmrPoseText));
        OnPropertyChanged(nameof(LiftText));
        OnPropertyChanged(nameof(CurrentTcpText));
        OnPropertyChanged(nameof(IsAmrStationary));
        OnPropertyChanged(nameof(FacingSourceText));
        OnPropertyChanged(nameof(ReadinessText));
        Recompute();
        MoveToApproachCommand.NotifyCanExecuteChanged();
    }

    private async Task PollTcpAsync()
    {
        _tcpPolling = true;
        try
        {
            _tcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool, _cts.Token);
            _tcpAt = DateTime.UtcNow;
            _tcpFailStreak = 0;
        }
        catch (OperationCanceledException) { /* 페이지 이탈 — 정상 */ }
        catch (Exception ex)
        {
            if (++_tcpFailStreak >= 3)
            {
                LiveTcp = false;
                Failure($"코봇 TCP 실시간 읽기를 중단했습니다 — '지금 읽기'로 재시도하세요: {ex.Message}");
            }
        }
        finally { _tcpPolling = false; }
    }

    [RelayCommand]
    private async Task RefreshTcpNow()
    {
        try
        {
            _tcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool, _cts.Token);
            _tcpAt = DateTime.UtcNow;
            _tcpFailStreak = 0;
            OnPropertyChanged(nameof(CurrentTcpText));
            Success("현재 TCP 를 읽었습니다.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failure($"코봇 TCP 읽기 실패: {ex.Message}"); }
    }

    public bool AmrConnected => _amr.IsConnected;
    public bool CobotConnected => _cobot.IsConnected;
    public bool HasAmrPose => _amr.LatestStatus is not null;

    private double AmrXm => UseManualAmrPose ? ManualAmrXm : _amr.LatestStatus?.Pose.X ?? 0;
    private double AmrYm => UseManualAmrPose ? ManualAmrYm : _amr.LatestStatus?.Pose.Y ?? 0;
    private double AmrYawRad => UseManualAmrPose
        ? ManualAmrYawDeg * Math.PI / 180.0
        : _amr.LatestStatus?.Pose.Angle ?? 0;

    private double StrokeMm => UseManualAmrPose ? ManualStrokeMm : _lift.Latest is { HeightMm: >= 0 } s ? s.HeightMm : 0;

    public string AmrPoseText => UseManualAmrPose
        ? $"수동 입력: X {ManualAmrXm:0.000} m, Y {ManualAmrYm:0.000} m, Yaw {ManualAmrYawDeg:0.00}° (계산 전용 — 이동 불가)"
        : HasAmrPose
            ? $"SLAM: X {AmrXm:0.000} m, Y {AmrYm:0.000} m, Yaw {AmrYawRad * 180.0 / Math.PI:0.00}°"
            : "AMR 상태 없음 — 연결·측위를 확인하세요.";

    public string LiftText => UseManualAmrPose
        ? $"수동 스트로크 {ManualStrokeMm:0} mm"
        : _lift.Latest is { HeightMm: >= 0 } s
            ? $"텔레스코픽 스트로크 {s.HeightMm} mm (완전 하강 = 0)"
            : "텔레스코픽 높이 없음 — 스트로크 0 으로 계산합니다(T_A_B tz 오차 주의).";

    public string MountText => _mountAtHome.All(v => Math.Abs(v) < 1e-9)
        ? "T_A_B 미보정 (전부 0) — 장착 보정 페이지에서 먼저 보정하세요."
        : $"T_A_B(완전 하강): [{string.Join(", ", _mountAtHome.Select(v => v.ToString("0.0")))}] mm/도";

    /// <summary>
    /// 현재 TCP 를 T_A_B 로 역환산해 <b>AMR 차체 좌표</b>와 <b>맵 좌표</b>로 보여준다.
    ///   p_A = T_A_B · p_B,  p_W = T_W_A · p_A
    /// 툴 축 방향도 AMR 기준으로 함께 표시한다 — 오일러각(짐벌락)을 읽지 않고 "어디를 향하는가"로
    /// 확인할 수 있어야 장착 회전·광축 설정의 오류가 눈에 띈다.
    /// </summary>
    public string CurrentTcpText
    {
        get
        {
            if (_tcp is not { Length: 6 } tcp)
                return LiveTcp ? "현재 TCP: 읽는 중… (코봇 연결 확인)" : "현재 TCP: 실시간 읽기 꺼짐 — '지금 읽기'를 누르세요.";

            var mount = MapCalibration.MountPoseAtStroke(_mountAtHome, StrokeMm);
            var tAT = FrameMath.Multiply(FrameMath.PoseToMatrix(mount), FrameMath.PoseToMatrix(tcp));   // AMR 차체 기준
            var amrPose = MapCalibration.AmrPoseToMmDeg(AmrXm, AmrYm, AmrYawRad);
            var tWT = FrameMath.Multiply(FrameMath.PoseToMatrix(amrPose), tAT);                          // 맵 기준
            var inAmr = FrameMath.MatrixToPose(tAT);
            var inMap = FrameMath.MatrixToPose(tWT);

            // 툴 축(AMR 기준) — 광축은 설정된 툴축, 나머지는 X·Y.
            var ax = (int)_opticalAxis / 2;
            var sign = (int)_opticalAxis % 2 == 0 ? 1.0 : -1.0;
            var optical = new[] { sign * tAT[0, ax], sign * tAT[1, ax], sign * tAT[2, ax] };
            var toolX = new[] { tAT[0, 0], tAT[1, 0], tAT[2, 0] };
            var toolY = new[] { tAT[0, 1], tAT[1, 1], tAT[2, 1] };

            var age = (DateTime.UtcNow - _tcpAt).TotalSeconds;
            return
                $"현재 TCP (tool #{Tool}, {age:0.0}초 전{(LiveTcp ? "" : ", 실시간 꺼짐")})\n" +
                $"  BASE   : [{Fmt(tcp)}] mm/도\n" +
                $"  AMR 차체: [{inAmr[0]:0.0}, {inAmr[1]:0.0}, {inAmr[2]:0.0}] mm  (T_A_B 역환산, +X=전방·+Y=좌측)\n" +
                $"  맵      : [{inMap[0]:0.0}, {inMap[1]:0.0}, {inMap[2]:0.0}] mm\n" +
                $"  축 방향 : 광축 툴{FlatSurfaceCenteringService.AxisName(_opticalAxis)} → {BodyAxisText(optical)} / " +
                $"툴X → {BodyAxisText(toolX)} / 툴Y → {BodyAxisText(toolY)}";
        }
    }

    /// <summary>축 벡터(AMR 차체 기준)를 방향 이름으로 — 연직이면 위/아래, 아니면 전/후/좌/우(+앙각).</summary>
    private static string BodyAxisText(double[] v)
    {
        var elev = Math.Asin(Math.Clamp(v[2], -1.0, 1.0)) * 180.0 / Math.PI;
        if (elev > 85) return "위(+Z)";
        if (elev < -85) return "아래(−Z)";
        var dir = BodyDir(Math.Atan2(v[1], v[0]) * 180.0 / Math.PI);
        return Math.Abs(elev) < 10 ? dir : $"{dir}(앙각 {elev:+0;-0}°)";
    }

    /// <summary>
    /// 저장된 T_A_B.rz 가 맞다면 BASE 축 조그가 AMR 차체 기준 어느 쪽으로 가야 하는지 — <b>조그로 대조</b>해
    /// 장착 회전을 현장에서 검증하기 위한 줄. 실제 조그 방향이 다르면 T_A_B.rz 가 그만큼 틀린 것이다.
    /// AMR 차체 규약: +X = 전방(주행 방향, SLAM yaw 방향), +Y = 좌측.
    /// </summary>
    public string MountCheckText
    {
        get
        {
            var rz = _mountAtHome.Length > 5 ? _mountAtHome[5] : 0;
            return $"이 값(rz={rz:0.#}°)대로면 조그 시 — BASE +X → {BodyDir(rz)}, BASE +Y → {BodyDir(rz + 90)} " +
                   "(AMR 기준 +X=전방·+Y=좌측). 실제와 다르면 T_A_B.rz 가 그 차이만큼 틀린 것입니다.";
        }
    }

    /// <summary>맵 평면 각도(도) → AMR 차체 기준 방향 이름. 45° 경계에서 가장 가까운 쪽으로 읽는다.</summary>
    private static string BodyDir(double deg)
    {
        var d = MapCalibration.NormalizeDeg(deg);
        if (d is > -45 and <= 45) return "AMR 전방";
        if (d is > 45 and <= 135) return "AMR 좌측";
        if (d is > -135 and <= -45) return "AMR 우측";
        return "AMR 후방";
    }

    /// <summary>벽 정면 방향(법선 방위각)을 무엇에서 가져왔는지 — 결과 표시용.</summary>
    public string FacingSourceText =>
        UseNodeTheta ? $"노드 theta {NodeThetaDeg:0.00}° (입력값 고정)"
        : UseManualAmrPose ? $"수동 AMR yaw {ManualAmrYawDeg:0.00}°"
        : HasAmrPose ? $"AMR yaw {AmrYawRad * 180.0 / Math.PI:0.00}° (실시간 — 로봇이 돌면 목표도 따라 바뀜)"
        : "0.00° (AMR 측위 없음 — 맵 +X 가정, 아래 경고 참고)";

    public string OpticalAxisText =>
        $"광축(면을 바라보는 툴축): 툴{FlatSurfaceCenteringService.AxisName(_opticalAxis)}" +
        (_opticalAxisFromParam ? "" : " — 파라미터 미설정, 기본값 사용(카메라 페이지에서 저장 권장)");

    /// <summary>
    /// spin 부호 안내 — spin 은 <b>광축 둘레</b> 회전이라, 광축이 툴 −Z 로 설정된 설비에서는
    /// 부호가 툴 RZ 와 반대가 된다(−Z 둘레 +θ = +Z 둘레 −θ). 실수하기 쉬운 지점이라 화면에 명시한다.
    /// </summary>
    public string SpinHintText => _opticalAxis switch
    {
        ToolAxisDir.PlusZ => "spin +90° = 툴 좌표계 RZ +90°(반시계). 광축이 툴 +Z 라 부호가 그대로입니다.",
        ToolAxisDir.MinusZ => "⚠ 광축이 툴 −Z 라 부호가 반대입니다 — 툴 RZ 를 반시계 90° 돌리려면 spin 에 −90 을 넣으세요.",
        _ => $"spin 은 광축(툴{FlatSurfaceCenteringService.AxisName(_opticalAxis)}) 둘레 회전이라 툴 RZ 와 1:1 대응하지 않습니다.",
    };

    /// <summary>선택한 wall_code 의 면 자세 설명 — 법선이 어느 쪽을 향하는지.</summary>
    public string WallCodeText
    {
        get
        {
            var w = WallCodes.Find(WallCode);
            if (w is null)
                return string.IsNullOrWhiteSpace(WallCode)
                    ? "wall_code 미지정 — 수직벽으로 가정(수평 후퇴), TOOL 자세는 현재 자세 유지."
                    : $"미정의 wall_code '{WallCode}'";
            var normal = w.Orientation switch
            {
                SurfaceOrientation.Floor => "연직 아래(−Z)",
                SurfaceOrientation.Ceiling => "연직 위(+Z)",
                SurfaceOrientation.ChamferLower => "벽 정면에서 45° 아래",
                SurfaceOrientation.ChamferUpper => "벽 정면에서 45° 위",
                _ => "수평(벽 정면)",
            };
            return $"{w.DisplayName} · Wall ID 0x{w.SurfaceId:X2} · 면 자세 {w.Orientation} → 법선 {normal}";
        }
    }

    /// <summary>최근 <see cref="StationaryWindowMs"/> 동안 AMR 이 멈춰 있었는가.</summary>
    public bool IsAmrStationary
    {
        get
        {
            if (UseManualAmrPose) return false;
            if (_poseHistory.Count < 4) return false;
            var (_, x0, y0, a0) = _poseHistory.Peek();
            foreach (var (_, x, y, a) in _poseHistory)
                if (Math.Abs(x - x0) > MoveTolMm || Math.Abs(y - y0) > MoveTolMm ||
                    Math.Abs(MapCalibration.NormalizeDeg(a - a0)) > MoveTolDeg)
                    return false;
            return true;
        }
    }

    public string ReadinessText =>
        UseManualAmrPose ? "수동 AMR pose 사용 중 — 계산만 가능합니다."
        : !CobotConnected ? "코봇 RPC 미연결"
        : !HasAmrPose ? "AMR 측위 없음"
        : !IsAmrStationary ? "AMR 이동 중 — 완전히 정지한 뒤 이동하세요."
        : Target is null ? "계산 결과 없음"
        : StandoffMm < SeamBaseTransform.MinSafeStandoffMm ? $"standoff {StandoffMm:0}mm — {SeamBaseTransform.MinSafeStandoffMm:0}mm 이상 필요"
        : !SafetyConfirmed ? "안전 확인 체크가 필요합니다."
        : "이동 가능";

    // ── 계산 ────────────────────────────────────────────────────────
    private void Recompute()
    {
        try
        {
            var input = BuildInput();
            var t = SeamBaseTransform.Resolve(input);
            Target = t;
            TargetText =
                $"맵 좌표 seam  : [{Fmt(t.SeamStartMapMm)}] mm (z 보정 {ZDatumOffsetMm:0} mm 적용)\n" +
                $"맵 좌표 접근점: [{Fmt(t.ApproachMapMm)}] mm (standoff {StandoffMm:0} mm)\n" +
                $"BASE seam     : [{Fmt(t.SeamStartBaseMm)}] mm\n" +
                $"BASE 접근점   : [{Fmt(t.ApproachBaseMm)}] mm  ← 이동 목표\n" +
                $"BASE 원점 거리: 수평 {t.PlanarDistanceMm:0} mm / 3D {t.DistanceMm:0} mm\n" +
                $"사용 T_A_B    : [{Fmt(t.MountUsed)}] (스트로크 {StrokeMm:0} mm 반영)\n" +
                $"면까지 거리   : 법선 방향 {t.NormalDistanceMm:0} mm (코봇 BASE 기준 — standoff {StandoffMm:0} mm 보다 커야 정상)\n" +
                $"면 법선(맵)   : [{Fmt3(t.SurfaceNormalMap)}]" +
                (t.Surface is { } so ? $" ({so})" : " (wall_code 미지정 — 수평 가정)") +
                (t.TargetPoseBase is { } tp
                    ? $"\n이동 목표 pose: [{Fmt(tp)}] mm/도  ← 광축 툴{FlatSurfaceCenteringService.AxisName(_opticalAxis)} 이 면을 향함" +
                      $"\n자세 확인(맵)  : 광축 {AxisText(t.SurfaceNormalMap)} / 툴X {AxisText(t.ToolXMap)} / 툴Y {AxisText(t.ToolYMap)}"
                    : "\n이동 목표 자세: (현재 TCP 자세 유지 — wall_code 미지정 또는 법선 자세 끔)") +
                (t.DirectionReason is { } r ? $"\n검사 방향 유도: {r}" : "") +
                $"\n벽 정면 방향  : {FacingSourceText}";

            // AMR yaw 폴백인데 측위가 없으면 0°(맵 +X)로 계산된다 — 숫자가 조용히 틀리므로 경고에 올린다.
            var notes = t.Notes.ToList();
            if (!UseWallNormalPose && !string.IsNullOrWhiteSpace(WallCode))
                notes.Insert(0, $"'면 법선 자세로 이동' 이 꺼져 있어 wall_code({WallCode})를 무시하고 " +
                                "현재 TCP 자세를 그대로 유지합니다 — wall_code 를 바꿔도 자세가 변하지 않습니다.");
            if (!UseNodeTheta && !UseManualAmrPose && !HasAmrPose)
                notes.Insert(0, "AMR 측위 없음인데 노드 theta 도 미사용 — 벽 정면 방향을 0°(맵 +X)로 가정해 계산했습니다. " +
                                "노드 theta 를 입력하거나 AMR 측위를 확인하세요.");
            NotesText = string.Join("\n", notes);
        }
        catch (Exception ex)
        {
            Target = null;
            TargetText = $"계산 불가: {ex.Message}";
            NotesText = "";
        }
    }

    private SeamBaseInput BuildInput() => new(
        SeamStartW: new[] { SeamStartX, SeamStartY, SeamStartZ },
        SeamEndW: HasSeamEnd ? new[] { SeamEndX, SeamEndY, SeamEndZ } : null,
        AmrXm: AmrXm,
        AmrYm: AmrYm,
        AmrYawRad: AmrYawRad,
        MountAtHome: _mountAtHome,
        TelescopicStrokeMm: StrokeMm,
        ZDatumOffsetMm: ZDatumOffsetMm,
        StandoffMm: StandoffMm,
        WallFacingThetaRad: UseNodeTheta ? NodeThetaDeg * Math.PI / 180.0 : null,
        WallCode: UseWallNormalPose ? WallCode : null,
        OpticalAxis: _opticalAxis,
        ToolSpinDeg: ToolSpinDeg);

    [RelayCommand]
    private void Compute()
    {
        Recompute();
        if (Target is not null) Success("환산했습니다.");
    }

    /// <summary>수신 액션 JSON(§8.4 전문)을 그대로 붙여넣어 seam 좌표·standoff 를 채운다.</summary>
    [RelayCommand]
    private void ParseAction()
    {
        if (string.IsNullOrWhiteSpace(ActionJson)) { Failure("액션 JSON 이 비어 있습니다."); return; }
        try
        {
            var action = JsonSerializer.Deserialize<VdaAction>(ActionJson);
            if (action is null) { Failure("액션 JSON 을 해석하지 못했습니다."); return; }

            if (!WeldInspectionActionParser.TryParse(action, out var req, out var error))
            {
                Failure($"액션 파라미터 해석 실패: {error}");
                return;
            }

            SeamStartX = req!.SeamStartW[0];
            SeamStartY = req.SeamStartW[1];
            SeamStartZ = req.SeamStartW[2];
            SeamEndX = req.SeamEndW[0];
            SeamEndY = req.SeamEndW[1];
            SeamEndZ = req.SeamEndW[2];
            HasSeamEnd = true;
            WallCode = req.DrawingPos.WallCode;
            if (req.StandoffMm > 0) StandoffMm = req.StandoffMm;

            Recompute();
            Success($"액션 해석 완료 — jobRef={req.JobRef}, seamType={req.SeamType}, wall={req.DrawingPos.WallCode}(면 자세 적용). " +
                    "노드 theta 는 액션에 없으므로 필요하면 직접 입력하세요(order 노드의 nodePosition.theta).");
        }
        catch (JsonException ex) { Failure($"JSON 형식 오류: {ex.Message}"); }
    }

    // ── 이동 ────────────────────────────────────────────────────────
    public bool CanMoveToApproach =>
        !Busy && CobotConnected && !UseManualAmrPose && HasAmrPose && IsAmrStationary
        && Target is not null && SafetyConfirmed
        && StandoffMm >= SeamBaseTransform.MinSafeStandoffMm;

    /// <summary>
    /// 접근점으로 TOOL 직선 이동. 자세(Rx/Ry/Rz)는 <b>현재 TCP 자세를 그대로 유지</b>하고 위치만 바꾼다 —
    /// 이 화면의 목적은 "좌표 환산이 맞는가" 확인이지 검사 자세 결정이 아니다(자세는 레시피·티칭 담당).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveToApproach))]
    private async Task MoveToApproach()
    {
        Busy = true;
        Message = null;
        try
        {
            // ① 이동 직전 재계산 — 표시값이 낡았어도 현재 pose·스트로크 기준으로 간다.
            var t = SeamBaseTransform.Resolve(BuildInput());
            Target = t;

            if (_mountAtHome.All(v => Math.Abs(v) < 1e-9))
            {
                Failure("T_A_B 미보정 — 장착 보정 없이는 맵 좌표를 코봇 좌표로 환산할 수 없습니다.");
                return;
            }

            // ② 목표 pose 결정.
            //    wall_code 가 있으면 광축이 면 법선을 향하는 자세(TargetPoseBase)를, 없으면 현재 TCP 자세를 쓴다.
            var tcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool, _cts.Token);
            var target = t.TargetPoseBase ?? new[]
            {
                t.ApproachBaseMm[0], t.ApproachBaseMm[1], t.ApproachBaseMm[2],
                tcp[3], tcp[4], tcp[5],
            };

            // 자세를 바꾸는 이동이면 회전량을 먼저 알린다 — 손목이 크게 휘두르는 것을 모르고 누르지 않도록.
            if (t.TargetPoseBase is not null)
            {
                var swing = OrientationDeltaDeg(tcp, target);
                AppendLog($"자세 변화 {swing:0.0}° (현재 TCP → 면 법선 자세)");
                if (swing > LargeSwingDeg)
                    AppendLog($"※ 회전이 큽니다({swing:0.0}° > {LargeSwingDeg:0}°) — 주변 간섭을 확인하세요.");
            }

            // ③ 역기구학 사전 점검 — 도달 불가를 이동 명령 전에 잡는다.
            double[] joints;
            try
            {
                joints = await _cobot.Rpc.GetInverseKinForMoveAsync(target, Tool, user: 0, ct: _cts.Token);
            }
            catch (OperationCanceledException) { throw; }   // 페이지 이탈·취소는 바깥에서 처리
            catch (Exception ex)
            {
                Failure($"역기구학 실패 — 도달 불가 자세입니다(이동하지 않았습니다): {ex.Message}");
                AppendLog($"IK 실패: 목표 [{Fmt(target)}]");
                return;
            }

            // ④ 이동.
            var vel = Math.Clamp(VelPct, 1, MaxVelPct);
            var rc = await _cobot.Rpc.MoveLAsync(target, joints, tool: Tool, user: 0,
                vel: vel, acc: 0, ovl: 100, blendR: -1, ct: _cts.Token);

            AppendLog($"MoveL rc={rc} 목표 [{Fmt(target)}] tool=#{Tool} vel={vel:0}%");
            if (rc == 0) Success("접근점으로 이동했습니다 — 실제 용접선과의 오차를 육안·레이저로 확인하세요.");
            else Failure($"이동 실패 rc={rc}{FairinoErrorCodes.Suffix(rc)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failure($"이동 중 오류: {ex.Message}"); }
        finally
        {
            Busy = false;
            MoveToApproachCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task StopMotion()
    {
        try
        {
            var rc = await _cobot.StopMotionImmediateAsync(_cts.Token);
            AppendLog($"StopMotion rc={rc}");
            if (rc == 0) Success("코봇 모션을 정지했습니다."); else Failure($"정지 실패 rc={rc}{FairinoErrorCodes.Suffix(rc)}");
        }
        catch (Exception ex) { Failure($"정지 실패: {ex.Message}"); }
    }

    // ── 입력 변경 → 재계산·버튼 상태 ────────────────────────────────
    partial void OnSeamStartXChanged(double value) => Recompute();
    partial void OnSeamStartYChanged(double value) => Recompute();
    partial void OnSeamStartZChanged(double value) => Recompute();
    partial void OnSeamEndXChanged(double value) => Recompute();
    partial void OnSeamEndYChanged(double value) => Recompute();
    partial void OnSeamEndZChanged(double value) => Recompute();
    partial void OnHasSeamEndChanged(bool value) => Recompute();
    partial void OnNodeThetaDegChanged(double value)
    {
        OnPropertyChanged(nameof(FacingSourceText));
        Recompute();
    }
    partial void OnUseNodeThetaChanged(bool value)
    {
        OnPropertyChanged(nameof(FacingSourceText));
        Recompute();
    }
    partial void OnZDatumOffsetMmChanged(double value) => Recompute();
    partial void OnToolSpinDegChanged(double value) => Recompute();
    partial void OnUseWallNormalPoseChanged(bool value) => Recompute();

    partial void OnWallCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(WallCodeText));
        Recompute();
    }

    partial void OnStandoffMmChanged(double value)
    {
        Recompute();
        OnPropertyChanged(nameof(ReadinessText));
        MoveToApproachCommand.NotifyCanExecuteChanged();
    }

    partial void OnUseManualAmrPoseChanged(bool value)
    {
        OnPropertyChanged(nameof(AmrPoseText));
        OnPropertyChanged(nameof(LiftText));
        OnPropertyChanged(nameof(FacingSourceText));
        OnPropertyChanged(nameof(ReadinessText));
        Recompute();
        MoveToApproachCommand.NotifyCanExecuteChanged();
    }

    partial void OnManualAmrXmChanged(double value) => Recompute();
    partial void OnManualAmrYmChanged(double value) => Recompute();
    partial void OnManualAmrYawDegChanged(double value) => Recompute();
    partial void OnManualStrokeMmChanged(double value) => Recompute();

    partial void OnSafetyConfirmedChanged(bool value)
    {
        OnPropertyChanged(nameof(ReadinessText));
        MoveToApproachCommand.NotifyCanExecuteChanged();
    }

    partial void OnBusyChanged(bool value) => MoveToApproachCommand.NotifyCanExecuteChanged();

    partial void OnTargetChanged(SeamBaseTarget? value)
    {
        OnPropertyChanged(nameof(ReadinessText));
        MoveToApproachCommand.NotifyCanExecuteChanged();
    }

    // ── 표시 헬퍼 ───────────────────────────────────────────────────
    private static string Fmt(double[] p) => string.Join(", ", p.Select(v => v.ToString("0.0")));

    private static string Fmt3(double[] p) => string.Join(", ", p.Select(v => v.ToString("0.000")));

    /// <summary>
    /// 축 벡터(맵)를 사람이 읽는 방향으로 — "방위 130°·수평", "연직 아래" 처럼.
    /// pose 의 오일러각은 ry≈±90 근방에서 짐벌락으로 rx·rz 가 요동치므로, 자세 확인은 이 표기로 한다.
    /// </summary>
    private static string AxisText(double[]? v)
    {
        if (v is not { Length: 3 }) return "—";
        var elev = Math.Asin(Math.Clamp(v[2], -1.0, 1.0)) * 180.0 / Math.PI;
        if (elev > 85) return "연직 위(+Z)";
        if (elev < -85) return "연직 아래(−Z)";
        var az = Math.Atan2(v[1], v[0]) * 180.0 / Math.PI;
        return Math.Abs(elev) < 5
            ? $"방위 {az:0.0}°(수평)"
            : $"방위 {az:0.0}°·앙각 {elev:+0.0;-0.0}°";
    }

    /// <summary>두 pose 자세 사이의 회전각(도) — R = R_a⁻¹·R_b 의 회전각.</summary>
    private static double OrientationDeltaDeg(double[] a, double[] b)
    {
        var ra = FrameMath.PoseToMatrix(a);
        var rb = FrameMath.PoseToMatrix(b);
        // trace(Rᵀ_a·R_b) = Σ_i Σ_row Ra[row,i]·Rb[row,i]
        double trace = 0;
        for (var i = 0; i < 3; i++)
            for (var row = 0; row < 3; row++)
                trace += ra[row, i] * rb[row, i];
        var cos = Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    private void AppendLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        MoveLog = string.IsNullOrEmpty(MoveLog) ? stamped : $"{stamped}\n{MoveLog}";
    }

    private void Success(string msg) => Notify(msg, error: false);
    private void Failure(string msg) => Notify(msg, error: true);
    private void Notify(string msg, bool error) { Message = msg; IsError = error; }
}
