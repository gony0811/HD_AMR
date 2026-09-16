using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 자립형 코봇 조그 리본. 기존 Shared/CobotJogRibbon.razor 이식. CobotService 하나에 의존하며
/// 자체 busy/취소 게이트를 가진다. Cobot 페이지(내장)와 조그 팝업 창이 공유한다.
/// 조그 패드는 원본 SVG 화살표 아트 대신 기능 동등한 버튼 그리드로 구현.
/// </summary>
public sealed partial class JogRibbonViewModel : ObservableObject
{
    private readonly CobotService _svc;
    private readonly SequenceRunGate _gate;

    public bool ShowSafetyHeader { get; }
    private readonly bool _autoResetBaseFrame;

    private bool _busy;
    private bool _holding;
    private JogFrame _holdFrame;
    private CancellationTokenSource? _opCts;

    public JogRibbonViewModel(CobotService svc, SequenceRunGate gate,
        bool showSafetyHeader = true, bool autoResetBaseFrame = false)
    {
        _svc = svc;
        _gate = gate;
        ShowSafetyHeader = showSafetyHeader;
        _autoResetBaseFrame = autoResetBaseFrame;
    }

    // 진입 시 1회: 활성 작업물 좌표계 잔류 자동 정규화(무변위). 조건 미충족 시 조용히 건너뜀.
    public async void OnActivatedOnce()
    {
        if (!_autoResetBaseFrame || !_svc.IsConnected) return;
        if (_svc.State is not { User: > 0 }) return;
        if (!_gate.TryEnter()) return;
        try
        {
            var rc = await _svc.Rpc.ResetActiveFrameAsync(JogTool, 0);
            Notify(rc == 0
                ? "활성 작업물 좌표계 잔류 감지 — 베이스(0)로 자동 복귀했습니다."
                : $"활성 좌표계 자동 복귀 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — '활성 좌표계 초기화' 버튼으로 수동 복귀하세요.", rc != 0);
        }
        catch (Exception ex) { Notify($"활성 좌표계 자동 복귀 중 오류: {ex.Message}", true); }
        finally { _gate.Exit(); }
    }

    /// <summary>부모(페이지/창)의 폴링 타이머에서 호출 — 상태 파생 속성 새로고침.</summary>
    public void RefreshState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsStateConnected));
        OnPropertyChanged(nameof(IsServoEnabled));
        OnPropertyChanged(nameof(ServoText));
        OnPropertyChanged(nameof(ActiveFrameText));
        OnPropertyChanged(nameof(HasActiveFrame));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
    }

    // ── 상태(안전 헤더) ──
    public bool IsConnected => _svc.IsConnected;
    public bool IsStateConnected => _svc.IsStateConnected;
    public bool IsServoEnabled => _svc.IsServoEnabled;
    public string ServoText => _svc.IsServoEnabled ? "서보 ON" : "서보 OFF";
    public bool HasActiveFrame => _svc.State is { };
    public string ActiveFrameText => _svc.State is { } fs ? $"활성 툴 #{fs.Tool} / 작업물 #{fs.User}" : "";
    public bool HasError => _svc.State is { ErrorCode: not 0 };
    public string ErrorText => _svc.State is { ErrorCode: not 0 } st ? $"오류 코드 {st.ErrorCode} — '오류 해제' 필요" : "";

    // ── 조그 파라미터 ──
    [ObservableProperty] private int _jogModeIndex;   // 0 미세 증분, 1 연속(누름)
    [ObservableProperty] private int _jogFrameIndex = 1; // 0 관절,1 베이스,2 툴,3 작업물 (기본 베이스)
    [ObservableProperty] private int _jogTool;
    [ObservableProperty] private int _jogUser;
    [ObservableProperty] private int _jogVel = 20;
    [ObservableProperty] private double _jogMaxMm = 20;
    [ObservableProperty] private double _jogMaxDeg = 15;
    [ObservableProperty] private double _jogHoldMaxMm = 100;
    [ObservableProperty] private double _jogHoldMaxDeg = 90;
    [ObservableProperty] private int _assumedUser;

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    public bool IsHoldMode => JogModeIndex == 1;
    public bool IsJointFrame => JogFrameIndex == 0;
    public bool IsWorkpieceFrame => JogFrameIndex == 3;

    partial void OnJogModeIndexChanged(int value) { OnPropertyChanged(nameof(IsHoldMode)); }
    partial void OnJogFrameIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsJointFrame));
        OnPropertyChanged(nameof(IsWorkpieceFrame));
    }

    private JogFrame Frame => JogFrameIndex switch { 0 => JogFrame.Joint, 2 => JogFrame.Tool, 3 => JogFrame.Workpiece, _ => JogFrame.Base };
    private bool AxisIsRotational(int axis) => IsJointFrame || axis >= 3;
    private double StepDis(int axis) => AxisIsRotational(axis) ? JogMaxDeg : JogMaxMm;

    /// <summary>증분 조그 1스텝. 파라미터 "axis,sign" (예: "2,-1").</summary>
    [RelayCommand]
    private async Task JogStep(string spec)
    {
        if (JogModeIndex != 0) return;               // 연속 모드에선 클릭 무시
        if (!_svc.IsConnected) { Notify("연결되지 않았습니다.", true); return; }
        var parts = spec.Split(',');
        int axis = int.Parse(parts[0]);
        int sign = int.Parse(parts[1]);
        var dis = StepDis(axis) * sign;
        var unit = AxisIsRotational(axis) ? "°" : "mm";
        await Run($"조그 축{axis}{(sign > 0 ? "+" : "-")} {Math.Abs(dis)}{unit}", async ct =>
        {
            if (Frame == JogFrame.Joint)
            {
                var joints = await _svc.Rpc.GetActualJointPosAsync(ct: ct);
                joints[axis] += dis;
                return await _svc.Rpc.MoveJAsync(joints, new double[6], tool: JogTool, vel: JogVel, ct: ct);
            }
            var curJoints = await _svc.Rpc.GetActualJointPosAsync(ct: ct);
            var anchor = await _svc.Rpc.GetTcpPoseInBaseAsync(JogTool, ct);
            var offset = new double[6];
            offset[axis] = dis;
            int flag = Frame == JogFrame.Tool ? 2 : 1;
            int user = Frame == JogFrame.Workpiece ? JogUser : 0;
            return await _svc.Rpc.MoveLAsync(anchor, jointPos: curJoints, tool: JogTool, user: user,
                                             vel: JogVel, offsetFlag: flag, offsetPos: offset, ct: ct);
        });
    }

    /// <summary>누름 연속 조그 시작(포인터 다운). "axis,sign".</summary>
    public async void HoldStart(int axis, int sign)
    {
        if (JogModeIndex != 1) return;
        if (!_svc.IsConnected || _busy || _holding) return;
        _holding = true;
        _holdFrame = Frame;
        var max = AxisIsRotational(axis) ? JogHoldMaxDeg : JogHoldMaxMm;
        try
        {
            var rc = await _svc.Rpc.StartJogAsync(Frame, axis + 1, sign > 0 ? 1 : 0, max, JogVel);
            if (rc != 0) { _holding = false; Notify($"연속 조그 시작 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}", true); }
        }
        catch (Exception ex) { _holding = false; Notify($"연속 조그 시작 실패: {ex.Message}", true); }
    }

    /// <summary>누름 연속 조그 정지(포인터 업/이탈).</summary>
    public async void HoldStop()
    {
        if (!_holding) return;
        var frame = _holdFrame;
        _holding = false;
        try { await _svc.StopJogAsync(frame); }
        catch (Exception ex) { Notify($"연속 조그 정지 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private async Task EmergencyStop()
    {
        _opCts?.Cancel();
        _holding = false;
        try
        {
            await _svc.ImmStopJogImmediateAsync();
            var rc = await _svc.StopMotionImmediateAsync();
            Notify($"긴급 정지 (rc={rc}){FairinoErrorCodes.Suffix(rc)}", rc != 0);
        }
        catch (Exception ex) { Notify($"긴급 정지 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private Task ResetError() => Run("오류 해제", ct => _svc.ResetErrorAsync(ct));

    [RelayCommand]
    private async Task ToggleServo()
    {
        var target = !_svc.IsServoEnabled;
        try
        {
            var rc = await _svc.SetServoEnableAsync(target);
            Notify($"서보 {(target ? "활성화" : "비활성화")} (rc={rc}){FairinoErrorCodes.Suffix(rc)}", rc != 0);
        }
        catch (Exception ex) { Notify($"서보 전환 실패: {ex.Message}", true); }
        RefreshState();
    }

    [RelayCommand]
    private Task ResetToBaseFrame() => Run("활성 좌표계 초기화(베이스)", ct => _svc.Rpc.ResetActiveFrameAsync(JogTool, 0, ct));

    [RelayCommand]
    private void ApplyAssumedUser()
    {
        _svc.Rpc.SetAssumedActiveUser(AssumedUser);
        Notify($"컨트롤러 활성 작업물 프레임을 #{AssumedUser}로 설정(클라이언트 추적값).", false);
    }

    private async Task Run(string label, Func<CancellationToken, Task<int>> action)
    {
        if (_busy) return;
        _busy = true;
        Message = null;
        _opCts?.Dispose();
        _opCts = new CancellationTokenSource();
        try
        {
            var rc = await action(_opCts.Token);
            Notify($"{label} 완료 (rc={rc}){FairinoErrorCodes.Suffix(rc)}", rc != 0);
        }
        catch (OperationCanceledException) { Notify($"{label} 취소됨 (긴급 정지)", true); }
        catch (Exception ex) { Notify($"{label} 실패: {ex.Message}", true); }
        finally { _busy = false; }
    }

    private void Notify(string msg, bool error) { Message = msg; IsError = error; }

    public void StopAll()
    {
        if (_holding && _svc.IsConnected) _ = _svc.ImmStopJogImmediateAsync();
        _holding = false;
        _opCts?.Cancel();
        _opCts?.Dispose();
    }
}
