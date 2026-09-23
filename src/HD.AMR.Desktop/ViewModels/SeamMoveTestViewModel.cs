using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 용접 위치 시험 — ACS 가 보낸 용접선 좌표(<c>seamStartW</c>, 맵 좌표)로 코봇 TOOL 이 실제로 가는지 확인한다.
///
/// 사슬: seam(맵 m) → z 기준 보정 → standoff 후퇴 → <see cref="SeamBaseTransform"/> → 코봇 BASE(mm) → MoveL.
/// 계산은 <see cref="SeamBaseTransform"/> 한 곳에 있고(단위 테스트 대상) 이 뷰모델은 입력·읽기·이동만 맡는다.
///
/// <b>안전.</b> 이 페이지는 코봇을 실제로 움직인다. 그래서:
///  · 이동 목표는 용접선 그 점이 아니라 벽에서 <see cref="StandoffMm"/> 물러난 <b>접근점</b>이다.
///  · 이동 직전에 AMR pose·스트로크를 다시 읽어 목표를 재계산한다(표시값이 낡아도 엉뚱한 곳으로 가지 않도록).
///  · AMR 이 정지해 있어야 하고, 안전 확인 체크와 역기구학 사전 점검을 통과해야 이동 버튼이 열린다.
///  · 수동 AMR pose(하드웨어 없이 계산 검증용)로는 이동하지 않는다 — 계산 전용이다.
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

    [ObservableProperty] private double _standoffMm = SeamBaseTransform.DefaultStandoffMm;
    [ObservableProperty] private int _tool = 1;
    [ObservableProperty] private double _velPct = 10;

    // 수동 AMR pose — 하드웨어 없이 계산 경로만 검증할 때. 이동은 막는다.
    [ObservableProperty] private bool _useManualAmrPose;
    [ObservableProperty] private double _manualAmrXm;
    [ObservableProperty] private double _manualAmrYm;
    [ObservableProperty] private double _manualAmrYawDeg;
    [ObservableProperty] private double _manualStrokeMm;

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
            OnPropertyChanged(nameof(MountText));
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

        OnPropertyChanged(nameof(AmrPoseText));
        OnPropertyChanged(nameof(LiftText));
        OnPropertyChanged(nameof(IsAmrStationary));
        OnPropertyChanged(nameof(ReadinessText));
        Recompute();
        MoveToApproachCommand.NotifyCanExecuteChanged();
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
                $"사용 T_A_B    : [{Fmt(t.MountUsed)}] (스트로크 {StrokeMm:0} mm 반영)" +
                (t.DirectionReason is { } r ? $"\n검사 방향 유도: {r}" : "");
            NotesText = string.Join("\n", t.Notes);
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
        WallFacingThetaRad: UseNodeTheta ? NodeThetaDeg * Math.PI / 180.0 : null);

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
            if (req.StandoffMm > 0) StandoffMm = req.StandoffMm;

            Recompute();
            Success($"액션 해석 완료 — jobRef={req.JobRef}, seamType={req.SeamType}, wall={req.DrawingPos.WallCode}. " +
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

            // ② 현재 TCP 자세 읽기 — 위치만 바꾸고 자세는 유지한다.
            var tcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool, _cts.Token);
            var target = new[]
            {
                t.ApproachBaseMm[0], t.ApproachBaseMm[1], t.ApproachBaseMm[2],
                tcp[3], tcp[4], tcp[5],
            };

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
    partial void OnNodeThetaDegChanged(double value) => Recompute();
    partial void OnUseNodeThetaChanged(bool value) => Recompute();
    partial void OnZDatumOffsetMmChanged(double value) => Recompute();

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

    private void AppendLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        MoveLog = string.IsNullOrEmpty(MoveLog) ? stamped : $"{stamped}\n{MoveLog}";
    }

    private void Success(string msg) => Notify(msg, error: false);
    private void Failure(string msg) => Notify(msg, error: true);
    private void Notify(string msg, bool error) { Message = msg; IsError = error; }
}
