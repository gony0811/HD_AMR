using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;
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
///  · 자세를 바꾸는 이동이라 이동 전 현재 TCP 와의 자세 차이를 계산해 로그에 남긴다(각도 자체로는 막지 않는다).
/// </summary>
public sealed partial class SeamMoveTestViewModel : ViewModelBase
{
    // 정지 판정 — MountCalibrationViewModel·QrLocalizationService 와 같은 기준.
    private const double MoveTolMm = 5.0;
    private const double MoveTolDeg = 0.2;
    private const int StationaryWindowMs = 2500;

    /// <summary>이동 속도 상한 [%] — 시험 이동은 느리게.</summary>
    private const double MaxVelPct = 30.0;

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

    // 자세 허용 범위(툴 장착 상태의 소프트리밋·특이점 여유) — 탐색·이동 전 판정의 기준.
    private PostureLimits _limits = PostureLimits.Default;
    private bool _limitsConfigured;
    private double[]? _joints;              // 현재 관절각 — 실시간 읽기와 경로 안전성 판정에 쓴다.
    private double[]? _toolCoord;           // 공구 #Tool 좌표계 오프셋 — J6 가 ±180 근방에 앉는 원인 확인용.
    private int _toolCoordId = -1;

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

    /// <summary>광축 둘레 추가 회전 [도] — 0° 기준은 <see cref="RollRefIndex"/> 가 정한다.</summary>
    [ObservableProperty] private double _toolSpinDeg;

    /// <summary>roll 0° 기준: 0=용접선 방향, 1=벽면 수평 고정, 2=맵 상방 고정.</summary>
    [ObservableProperty] private int _rollRefIndex;

    /// <summary>광축을 면 법선에서 상하로 기울인 각 [도] — 손목 특이점 회피용 자유도.</summary>
    [ObservableProperty] private double _tiltUpDeg;

    /// <summary>광축을 면 법선에서 좌우로 기울인 각 [도].</summary>
    [ObservableProperty] private double _tiltSideDeg;

    /// <summary>관절 이동(MoveJ)으로 갈지 — 직선 이동(MoveL)은 도중에 손목 특이점을 쓸고 지나갈 수 있다.</summary>
    [ObservableProperty] private bool _useJointMove = true;

    /// <summary>역기구학이 특이자세(rc=38)로 거부하면 목표 <b>자세</b>를 조금씩 틀어 재시도할지.
    /// 위치는 그대로 두고 광축 방향·roll 만 바꾼다.</summary>
    [ObservableProperty] private bool _nudgeOnIkFailure = true;

    // ── 자세 허용 범위 편집(툴 장착 상태) ───────────────────────────
    [ObservableProperty] private string _jointMinText = "";
    [ObservableProperty] private string _jointMaxText = "";
    [ObservableProperty] private double _wristMarginDeg = 15;
    [ObservableProperty] private double _elbowMarginDeg = 8;
    [ObservableProperty] private double _maxJointTravelDeg = 200;

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

    /// <summary>자세 탐색 결과 요약.</summary>
    [ObservableProperty] private string _planText = "‘자세 탐색’ 을 누르면 특이점·관절한계를 피하는 접근 자세를 찾습니다.";
    [ObservableProperty] private string _planNotes = "";
    private ApproachPlan? _plan;

    public bool HasPlanNotes => !string.IsNullOrEmpty(PlanNotes);

    partial void OnPlanNotesChanged(string value) => OnPropertyChanged(nameof(HasPlanNotes));

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

            // 자세 허용 범위 — 툴 장착 상태의 관절 소프트리밋·특이점 여유.
            var limitsSvc = scope.ServiceProvider.GetRequiredService<PostureLimitsService>();
            _limits = await limitsSvc.GetAsync();
            _limitsConfigured = await limitsSvc.IsConfiguredAsync();
            ApplyLimitsToInputs(_limits);
            OnPropertyChanged(nameof(LimitsText));

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
        OnPropertyChanged(nameof(CurrentPostureText));
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
            _joints = await _cobot.Rpc.GetActualJointPosAsync(ct: _cts.Token);
            if (_toolCoordId != Tool)
            {
                _toolCoord = Tool == 0 ? new double[6] : await _cobot.Rpc.GetToolCoordAsync(Tool, _cts.Token);
                _toolCoordId = Tool;
            }
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
            _joints = await _cobot.Rpc.GetActualJointPosAsync(ct: _cts.Token);
            _tcpAt = DateTime.UtcNow;
            _tcpFailStreak = 0;
            OnPropertyChanged(nameof(CurrentTcpText));
            OnPropertyChanged(nameof(CurrentPostureText));
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
            if (_mountAtHome.Length < 6) return "";
            double rx = _mountAtHome[3], ry = _mountAtHome[4], rz = _mountAtHome[5];

            // BASE +Z 가 어디를 향하는지 — rx/ry 로만 정해진다. 지금까지 조그 대조는 X/Y 만 봤는데,
            // 코봇이 거꾸로(마스트에 매달려) 달려 있으면 rx=180 이어야 하고 그 경우 모든 높이가
            // 부호 반대로 계산된다. tz 만 맞춰 놓으면 숫자가 그럴듯해 보여 놓치기 쉽다.
            var upDown = Math.Abs(MapCalibration.NormalizeDeg(rx)) < 45 && Math.Abs(MapCalibration.NormalizeDeg(ry)) < 45
                ? "위(천장)"
                : Math.Abs(Math.Abs(MapCalibration.NormalizeDeg(rx)) - 180) < 45
                    ? "아래(바닥) — 코봇이 거꾸로 달린 전제"
                    : "옆(기울어 장착된 전제)";

            return $"이 값(rx={rx:0.#}°, ry={ry:0.#}°, rz={rz:0.#}°)대로면 조그 시 — " +
                   $"BASE +X → {BodyDir(rz)}, BASE +Y → {BodyDir(rz + 90)}, BASE +Z → {upDown} " +
                   "(AMR 기준 +X=전방·+Y=좌측). 세 축 모두 조그로 대조하세요 — 다르면 T_A_B 회전이 그만큼 틀린 것입니다. " +
                   "특히 +Z 는 지금까지 검증된 적이 없습니다.";
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
    /// <summary>roll 기준 설명 — 무엇이 영상의 회전을 정하는지.</summary>
    public string RollRefText => RollRefIndex switch
    {
        1 => "roll 기준: 벽면 수평 고정 — 용접선이 비스듬해도 영상 수평이 유지됩니다.",
        2 => "roll 기준: 맵 상방(+Z) 고정 — 영상의 한 축이 항상 '위' 를 향합니다(바닥·천장은 벽면 수평으로 대체).",
        _ => "roll 기준: 용접선 방향 — 용접선이 비스듬하면 영상도 그만큼 기웁니다(끝점 미입력 시 벽면 수평).",
    };

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
                      $"\n자세 확인(맵)  : 광축 {AxisText(t.LookDirMap)} / 툴X {AxisText(t.ToolXMap)} / 툴Y {AxisText(t.ToolYMap)}" +
                      (SeamBaseTransform.LookTiltDeg(t.SurfaceNormalMap, t.LookDirMap) > 0.05
                          ? $"\n광축 틸트      : 면 법선에서 {SeamBaseTransform.LookTiltDeg(t.SurfaceNormalMap, t.LookDirMap):0.0}° (상하 {TiltUpDeg:+0.0;-0.0;0}° / 좌우 {TiltSideDeg:+0.0;-0.0;0}°)"
                          : "")
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
        ToolSpinDeg: ToolSpinDeg,
        TiltUpDeg: TiltUpDeg,
        TiltSideDeg: TiltSideDeg,
        RollRef: (ToolRollRef)Math.Clamp(RollRefIndex, 0, 2));

    // ── 자세 허용 범위 · 특이점 회피 ────────────────────────────────

    /// <summary>저장된 허용 범위를 편집 입력으로 되돌린다.</summary>
    private void ApplyLimitsToInputs(PostureLimits l)
    {
        var n = l.Normalized();
        JointMinText = string.Join(", ", n.JointMinDeg.Select(v => v.ToString("0.#")));
        JointMaxText = string.Join(", ", n.JointMaxDeg.Select(v => v.ToString("0.#")));
        WristMarginDeg = n.WristMarginDeg;
        ElbowMarginDeg = n.ElbowMarginDeg;
        MaxJointTravelDeg = n.MaxJointTravelDeg;
    }

    public string LimitsText => _limitsConfigured
        ? $"자세 허용 범위: 저장값 사용 (손목 여유 |J5| ≥ {_limits.WristMarginDeg:0}°, 팔꿈치 |J3| ≥ {_limits.ElbowMarginDeg:0}°)"
        : "⚠ 자세 허용 범위 미설정 — 하드웨어 기본값(±175°)으로 판정합니다. 플랜지 툴이 닿는 각을 조그로 찾아 " +
          "관절 하한·상한을 좁혀 저장하세요. 그러지 않으면 '통과' 로 나와도 실제로는 툴이 간섭합니다.";

    /// <summary>현재 관절각이 허용 범위 안인지 — 이동 전에 눈으로 확인하는 줄.</summary>
    public string CurrentPostureText
    {
        get
        {
            if (_joints is not { Length: 6 } j) return "현재 관절각: 읽는 중…";
            var m = _limits.Evaluate(j);
            var mark = m.Feasible ? "OK" : "위반";
            // J6 는 광축 둘레 roll 이다 — spin 을 주면 그대로 J6 가 움직이므로, 지금 J6 가 ±180 근방이면
            // 한쪽으로는 spin 여유가 거의 없다. 공구 좌표계에 rz 가 실려 있으면 그만큼 J6 가 끌려간다.
            var n = _limits.Normalized();
            var spinRoom = $"  spin 여유  : J6 {j[5]:0.0}° → +{n.JointMaxDeg[5] - j[5]:0.0}° / −{j[5] - n.JointMinDeg[5]:0.0}°";
            var heightNote = _tcp is { Length: 6 } && _mountAtHome.Length > 2
                ? $"\n  높이 대조  : 위 '맵' z 가 바닥에서 잰 툴 높이와 같아야 합니다 — 다르면 T_A_B 의 tz 나 rx 가 틀렸습니다."
                : "";
            var toolNote = _toolCoord is { Length: 6 } tc
                ? $"\n  공구 #{Tool}  : [{string.Join(", ", tc.Select(v => v.ToString("0.#")))}] mm/도" +
                  (Math.Abs(Math.Abs(tc[5]) - 180.0) < 20.0
                      ? "  ← rz 가 180° 근방: 플랜지가 반 바퀴 돌아야 이 자세가 나옵니다(J6 가 ±180 으로 밀리는 원인)"
                      : "")
                : "";

            return $"현재 관절각 : [{string.Join(", ", j.Select(v => v.ToString("0.0")))}]°\n" +
                   $"  |J5| {m.WristDeg:0.0}° (손목 특이점까지) · |J3| {m.ElbowDeg:0.0}° (팔꿈치) · 여유 {m.MarginDeg:0.0}° [{mark}]\n" +
                   $"  가장 빠듯한 제약: {m.Limiting}\n" +
                   spinRoom + toolNote + heightNote;
        }
    }

    /// <summary>편집 입력 → <see cref="PostureLimits"/>. 형식이 틀리면 null.</summary>
    private PostureLimits? BuildLimits()
    {
        var min = ParseSix(JointMinText);
        var max = ParseSix(JointMaxText);
        if (min is null || max is null) return null;
        return new PostureLimits(min, max, WristMarginDeg, ElbowMarginDeg,
            _limits.ShoulderRadiusMinMm, MaxJointTravelDeg).Normalized();
    }

    private static double[]? ParseSix(string text)
    {
        var parts = (text ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) return null;
        var v = new double[6];
        for (var i = 0; i < 6; i++)
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v[i])) return null;
        return v;
    }

    [RelayCommand]
    private async Task SaveLimits()
    {
        var l = BuildLimits();
        if (l is null) { Failure("관절 하한·상한은 J1~J6 여섯 개의 숫자여야 합니다 (예: -175, -175, -160, -175, -175, -175)."); return; }
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PostureLimitsService>().SaveAsync(l);
            _limits = l;
            _limitsConfigured = true;
            OnPropertyChanged(nameof(LimitsText));
            OnPropertyChanged(nameof(CurrentPostureText));
            Success("자세 허용 범위를 저장했습니다 — 자세 탐색과 이동 전 점검이 이 값을 씁니다.");
        }
        catch (Exception ex) { Failure($"저장 실패: {ex.Message}"); }
    }

    /// <summary>현재 관절각을 그대로 "여기까지는 된다" 는 근거로 쓰도록, 한계를 현재 값 바깥으로 넓힌다.</summary>
    [RelayCommand]
    private void CaptureLimitFromCurrent()
    {
        if (_joints is not { Length: 6 } j) { Failure("현재 관절각을 읽지 못했습니다."); return; }
        var min = ParseSix(JointMinText) ?? PostureLimits.Default.JointMinDeg.ToArray();
        var max = ParseSix(JointMaxText) ?? PostureLimits.Default.JointMaxDeg.ToArray();
        for (var i = 0; i < 6; i++)
        {
            min[i] = Math.Min(min[i], j[i]);
            max[i] = Math.Max(max[i], j[i]);
        }
        JointMinText = string.Join(", ", min.Select(v => v.ToString("0.#")));
        JointMaxText = string.Join(", ", max.Select(v => v.ToString("0.#")));
        Success("현재 관절각을 포함하도록 범위를 넓혔습니다 — 툴이 닿기 직전 자세에서 누르고 저장하세요.");
    }

    /// <summary>
    /// 특이점·관절한계를 피하는 접근 자세 탐색. 검사 불변식("용접선이 광축 위 standoff 거리")은 유지한 채
    /// 틸트·spin·standoff 를 훑는다. 코봇 역기구학을 쓰므로 연결이 필요하지만 <b>이동은 하지 않는다</b>.
    /// </summary>
    [RelayCommand]
    private async Task PlanPosture()
    {
        if (!CobotConnected) { Failure("코봇 미연결 — 역기구학이 필요해 탐색할 수 없습니다."); return; }
        if (string.IsNullOrWhiteSpace(WallCode) || !UseWallNormalPose)
        {
            Failure("wall_code 를 고르고 '면 법선 자세로 이동' 을 켜야 자세 탐색이 의미가 있습니다.");
            return;
        }

        Busy = true;
        try
        {
            // 이상값(틸트 0) 기준 입력 — 후보는 여기서 파생된다.
            var input = BuildInput() with { TiltUpDeg = 0, TiltSideDeg = 0 };
            var from = _joints is { Length: 6 } ? _joints : null;
            var started = DateTime.UtcNow;

            var plan = await ApproachPlanner.PlanAsync(
                input, ApproachSearchSpace.Default, _limits,
                async (pose, ct) =>
                {
                    try { return await _cobot.Rpc.GetInverseKinForMoveAsync(pose, Tool, user: 0, ct: ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { return null; }          // 도달 불가 — 후보에서 제외
                },
                fromJointsDeg: from, ct: _cts.Token);

            _plan = plan;
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;

            if (plan.Candidate is { } c && plan.Feasible)
            {
                // 찾은 값을 입력에 반영 — 이후 계산·이동이 이 자세를 쓴다.
                TiltUpDeg = c.TiltUpDeg;
                TiltSideDeg = c.TiltSideDeg;
                ToolSpinDeg += c.SpinDeg;
                StandoffMm += c.StandoffDeltaMm;
                Recompute();
            }

            PlanText =
                $"후보 {plan.Tried}개 평가 / 도달 가능 {plan.Reachable}개 ({elapsed:0.0}초)\n" +
                (plan.Candidate is { } k
                    ? $"채택: 틸트 상하 {k.TiltUpDeg:+0.0;-0.0;0}° · 좌우 {k.TiltSideDeg:+0.0;-0.0;0}° · " +
                      $"spin {k.SpinDeg:+0.0;-0.0;0}° · standoff {k.StandoffDeltaMm:+0;-0;0}mm\n"
                    : "채택: 없음\n") +
                (plan.JointsDeg is { } jd
                    ? $"목표 관절각: [{string.Join(", ", jd.Select(v => v.ToString("0.0")))}]°\n"
                    : "") +
                (plan.Margin is { } m
                    ? $"여유 {m.MarginDeg:0.0}° (|J5| {m.WristDeg:0.0}° · |J3| {m.ElbowDeg:0.0}° · 이동량 {m.TravelDeg:0.0}°) — {m.Limiting}"
                    : "");

            PlanNotes = string.Join("\n", plan.Notes);

            if (plan.Feasible && plan.PathSafe) Success("쓸 수 있는 접근 자세를 찾았습니다.");
            else if (plan.Feasible) Failure("자세는 찾았지만 지금 자세에서 곧바로 가면 손목 특이점을 지납니다 — 홈 복귀 후 다시 시도하세요.");
            else Failure("허용 범위 안의 접근 자세가 없습니다 — 텔레스코픽 높이나 AMR 정차 위치를 바꿔야 합니다.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failure($"자세 탐색 실패: {ex.Message}"); }
        finally
        {
            Busy = false;
            MoveToApproachCommand.NotifyCanExecuteChanged();
        }
    }

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

            // 자세를 바꾸는 이동이면 회전량을 기록만 한다. 회전이 크다는 것 자체는 막을 근거가 아니다 —
            // 면 법선 자세로 가면 90° 넘는 회전이 정상이다. 실제 판정은 아래 ③' 자세 허용 범위가 한다.
            if (t.TargetPoseBase is not null)
                AppendLog($"자세 변화 {OrientationDeltaDeg(tcp, target):0.0}° (현재 TCP → 면 법선 자세)");

            // ③ 역기구학 사전 점검 — 도달 불가를 이동 명령 전에 잡는다.
            //    IK 는 tool·user 인자가 없어 컨트롤러의 '활성' 프레임 기준으로 해석된다. 활성 프레임이
            //    이동 프레임(tool=#N, user=0)과 다르면 같은 목표가 errcode 112/38 로 거부되므로,
            //    실패를 진단하려면 목표 pose 만으로는 부족하고 활성 프레임과 errcode 가 함께 있어야 한다.
            var (activeTool, activeUser) = await _cobot.Rpc.ResolveActiveFramesAsync(_cts.Token, strict: false);
            if (activeTool != Tool || activeUser != 0)
                AppendLog($"※ 활성 좌표계 공구 #{activeTool} / 작업물 #{activeUser} — 이동 기준(공구 #{Tool} / 작업물 0)과 " +
                          "다릅니다. IK 가 거부되면 '활성 좌표계 초기화' 를 먼저 누르세요.");

            //    IK 실패가 rc=38(특이자세)이면 위치가 아니라 자세 문제다 — 자세만 틀어 재시도한다.
            //    반환 joints 는 ±360 감김이 해제된 값이다(컨트롤러 IK 는 −3.6° 를 356.4° 로 주기도 한다).
            var from = await _cobot.Rpc.GetActualJointPosAsync(ct: _cts.Token);
            _joints = from;

            var (nudged, movedTarget, ikNote) = await TryIkWithNudgeAsync(
                target, t.PlanarDistanceMm, from, _cts.Token);
            if (nudged is null)
            {
                Failure($"쓸 수 있는 자세가 없어 이동하지 않았습니다. {ikNote}");
                AppendLog($"  활성 좌표계 #{activeTool}/#{activeUser}");
                return;
            }
            var joints = nudged;
            target = movedTarget;

            var margin = _limits.Evaluate(joints, t.PlanarDistanceMm, from);
            AppendLog($"목표 관절각 [{string.Join(", ", joints.Select(v => v.ToString("0.0")))}]° " +
                      $"여유 {margin.MarginDeg:0.0}° (|J5| {margin.WristDeg:0.0}°) — {margin.Limiting}");

            // ④ 이동. 관절 이동(MoveJ)은 각 축이 두 끝값 사이에서 단조로 변하므로, 양 끝이 허용 범위 안이고
            //    J5 부호가 같으면 경로 도중에 손목 특이점을 지나지 않는다. 직교 직선 이동(MoveL)은 자세를
            //    보간하다 J5 가 0 을 쓸고 지나갈 수 있어(rc=38·손목 급회전) 기본값이 아니다.
            var vel = Math.Clamp(VelPct, 1, MaxVelPct);
            int rc;

            if (UseJointMove)
            {
                var (pathOk, pathReason) = _limits.CheckJointPath(from, joints);
                if (!pathOk)
                {
                    Failure($"관절 이동 경로 거부 — {pathReason}");
                    AppendLog($"경로 거부: {pathReason}");
                    return;
                }

                rc = await _cobot.Rpc.MoveJAsync(joints, target, tool: Tool, user: 0,
                    vel: vel, acc: 0, ovl: 100, ct: _cts.Token);
                AppendLog($"MoveJ rc={rc} 목표 [{Fmt(target)}] tool=#{Tool} vel={vel:0}% " +
                          $"(관절 보간 — 경로는 직선이 아닙니다)");
            }
            else
            {
                rc = await _cobot.Rpc.MoveLAsync(target, joints, tool: Tool, user: 0,
                    vel: vel, acc: 0, ovl: 100, blendR: -1, ct: _cts.Token);
                AppendLog($"MoveL rc={rc} 목표 [{Fmt(target)}] tool=#{Tool} vel={vel:0}%");
            }

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

    /// <summary>
    /// 목표 자세를 조금씩 틀어 가며 역기구학이 풀리는 자세를 찾는다. <b>위치는 건드리지 않는다</b> —
    /// 광축 방향(상하·좌우 틸트)과 광축 둘레 roll(spin)만 바꾼다.
    ///
    /// 특이자세(rc=38)는 <b>위치가 아니라 자세</b> 때문에 나는 거부다. 그런데 'wall_code 미지정 +
    /// 면 법선 자세로 이동 끔' 상태에서는 목표 자세가 현재 TCP 자세로 고정돼 있어, 사용자가 자세를
    /// 바꿀 수단이 없다(자세 탐색은 wall_code 를 요구한다). 여기서 현재 자세를 기준으로 같은 탐색을 한다.
    ///
    /// 후보 격자·비용 순서는 <see cref="ApproachPlanner"/> 와 공유한다 — 덜 타협한 자세부터 시도한다.
    /// </summary>
    private async Task<(double[]? Joints, double[] Target, string Note)> TryIkWithNudgeAsync(
        double[] target, double planarRadiusMm, double[]? fromJoints, CancellationToken ct)
    {
        // 한 후보를 끝까지 판정한다 — 역기구학이 풀리는 것만으로는 부족하고, 나온 해가 관절한계·특이점
        // 여유까지 통과해야 쓸 수 있는 자세다. 둘 중 무엇이 걸렸는지 사유로 남긴다.
        //  Joints 는 역기구학이 푼 관절각(예외면 null), Margin 은 그 해의 판정. 둘 다 있어야 채택한다.
        //
        //  sweepConfigs: 같은 TCP 자세를 만드는 해 가지(branch)를 전부 훑는다. 컨트롤러 자동 선택(config=−1)
        //  은 현재 자세와의 연속성을 보장하지 않아 손목이 반 바퀴 뒤집힌 해를 주기도 한다 — 자세를 타협하기
        //  전에 '같은 자세의 다른 해' 부터 찾는 것이 순서다. 여유가 가장 큰 가지를 고른다.
        async Task<(double[]? Joints, PostureMargin? Margin, string Why)> TryAsync(double[] pose, bool sweepConfigs)
        {
            double[]? bestJ = null;
            PostureMargin? bestM = null;
            string firstWhy = "";

            var configs = sweepConfigs ? new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7 } : new[] { -1 };
            foreach (var cfg in configs)
            {
                double[] raw;
                try { raw = await _cobot.Rpc.GetInverseKinForMoveAsync(pose, Tool, user: 0, ct: ct, config: cfg); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (firstWhy.Length == 0) firstWhy = ex.Message; continue; }

                var j = _limits.NormalizeJoints(raw);
                var m = _limits.Evaluate(j, planarRadiusMm, fromJoints);
                if (bestM is null || m.MarginDeg > bestM.MarginDeg) (bestJ, bestM) = (j, m);
                if (m.Feasible)
                {
                    if (cfg >= 0) AppendLog($"IK 해 가지 config={cfg} 채택 — 자동 선택(−1)보다 나은 해를 찾았습니다.");
                    return (j, m, "");
                }
            }

            if (bestM is null) return (null, null, firstWhy.Length > 0 ? firstWhy : "역기구학 해 없음");
            return (bestJ, bestM, bestM.Limiting);
        }

        // ① 원래 자세 그대로 — 자세를 틀기 전에 해 가지를 전부 훑는다.
        var (joints0, margin0, why0) = await TryAsync(target, sweepConfigs: true);
        if (joints0 is not null && margin0 is { Feasible: true }) return (joints0, target, "");

        if (joints0 is null)
            AppendLog($"IK 실패: 목표 [{Fmt(target)}] tool=#{Tool}/user=0\n  └ {why0}");
        else
            AppendLog($"원래 자세 거부: 관절각 [{string.Join(", ", joints0.Select(v => v.ToString("0.0")))}]° " +
                      $"여유 {margin0!.MarginDeg:0.0}° (|J5| {margin0.WristDeg:0.0}° · 이동량 {margin0.TravelDeg:0.0}°) — {why0}");

        if (!NudgeOnIkFailure)
            return (null, target, margin0 is null
                ? "자세 재시도 꺼짐 — '자세를 틀어 재시도' 를 켜거나 wall_code 로 면 법선 자세를 쓰세요."
                : $"{why0}. '자세를 틀어 재시도' 를 켜면 자세를 바꿔 다시 찾습니다.");

        // ② 자세만 흔들어 재시도. standoff 축은 위치를 바꾸므로 제외한다.
        var space = ApproachSearchSpace.Default with { StandoffDeltaMm = new[] { 0.0 }, MaxEvaluations = 40 };
        var tried = 0;
        var best = margin0;                 // ①의 판정을 출발점으로 — 후보가 더 나으면 갱신된다
        var bestWhy = why0;

        foreach (var c in ApproachPlanner.Candidates(space))
        {
            if (c.TiltUpDeg == 0 && c.TiltSideDeg == 0 && c.SpinDeg == 0) continue;   // ①에서 이미 판정
            if (++tried > space.MaxEvaluations) break;
            ct.ThrowIfCancellationRequested();

            // 툴 프레임 기준 회전만 얹는다 — 병진 0 이라 위치는 보존된다.
            var probe = FrameMath.FromFrame(
                new[] { 0.0, 0.0, 0.0, c.TiltUpDeg, c.TiltSideDeg, c.SpinDeg }, target);

            var (joints, margin, why) = await TryAsync(probe, sweepConfigs: false);
            if (joints is null || margin is not { Feasible: true })
            {
                if (margin is not null && (best is null || margin.MarginDeg > best.MarginDeg))
                    (best, bestWhy) = (margin, why);
                continue;
            }

            var note = $"자세를 틀어 해를 찾았습니다 — 상하 {c.TiltUpDeg:+0.0;-0.0;0}° / 좌우 {c.TiltSideDeg:+0.0;-0.0;0}° / " +
                       $"spin {c.SpinDeg:+0.0;-0.0;0}° (후보 {tried}개 시도, 여유 {margin!.MarginDeg:0.0}°, |J5| {margin.WristDeg:0.0}°)";
            AppendLog($"※ {note}");
            AppendLog($"  틀어진 목표 pose [{Fmt(probe)}] — 검사 입사각이 그만큼 비스듬해집니다.");
            return (joints, probe, note);
        }

        return (null, target,
            $"자세를 {tried}가지로 틀어 봐도 쓸 수 있는 해가 없습니다 (가장 나은 것: {bestWhy}" +
            (best is not null ? $", 여유 {best.MarginDeg:0.0}°" : "") + "). " +
            "이 위치는 팔로 해결되지 않습니다 — 텔레스코픽 높이나 AMR 정차 위치를 바꾸세요.");
    }

    /// <summary>
    /// 활성 좌표계를 이동 기준(공구 <see cref="Tool"/> / 작업물 0)으로 되돌린다. 무변위 MoveJ 라 로봇은
    /// 움직이지 않는다. 이전 시퀀스가 반납하지 못한 작업물 프레임이 남아 있으면 IK 가 목표를 그 프레임
    /// 기준으로 오해석해 errcode 112/38 을 내는데, 그 상태에서 빠져나오는 표준 복구 경로다.
    /// </summary>
    [RelayCommand]
    private async Task ResetActiveFrame()
    {
        if (!CobotConnected) { Failure("코봇 미연결"); return; }
        Busy = true;
        try
        {
            var (beforeTool, beforeUser) = await _cobot.Rpc.ResolveActiveFramesAsync(_cts.Token, strict: false);
            var rc = await _cobot.Rpc.ResetActiveFrameAsync(Tool, 0, _cts.Token);
            AppendLog($"활성 좌표계 초기화 rc={rc}: 공구 #{beforeTool}/작업물 #{beforeUser} → 공구 #{Tool}/작업물 0");
            if (rc == 0) Success($"활성 좌표계를 공구 #{Tool} / 작업물 0 으로 맞췄습니다.");
            else Failure($"초기화 실패 rc={rc}{FairinoErrorCodes.Suffix(rc)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failure($"활성 좌표계 초기화 실패: {ex.Message}"); }
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
    partial void OnRollRefIndexChanged(int value) => Recompute();
    partial void OnTiltUpDegChanged(double value) => Recompute();
    partial void OnTiltSideDegChanged(double value) => Recompute();
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
