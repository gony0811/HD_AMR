using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 코봇 제어. 기존 Cobot.razor 이식 — 안전 바(서보/E-STOP/오류해제), 좌표계 설정, MoveL 이동,
/// 조그 리본(<see cref="Jog"/>), 작업물 좌표계 3점 교시, 오프셋 이동.
/// </summary>
public sealed partial class CobotViewModel : ViewModelBase
{
    private readonly CobotService _svc;
    private readonly DispatcherTimer _timer;
    private bool _busy;
    private CancellationTokenSource? _opCts;

    public JogRibbonViewModel Jog { get; }

    public CobotViewModel(CobotService svc, SequenceRunGate gate)
    {
        _svc = svc;
        Jog = new JogRibbonViewModel(svc, gate, showSafetyHeader: false, autoResetBaseFrame: true);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { RefreshState(); Jog.RefreshState(); };
    }

    public override void OnActivated()
    {
        RefreshState();
        _timer.Start();
        Jog.OnActivatedOnce();
    }

    public override void OnDeactivated()
    {
        _timer.Stop();
        Jog.StopAll();
        if (_svc.IsConnected) _ = _svc.StopMotionImmediateAsync();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsStateConnected));
        OnPropertyChanged(nameof(IsServoEnabled));
        OnPropertyChanged(nameof(ServoText));
        OnPropertyChanged(nameof(HasActiveFrame));
        OnPropertyChanged(nameof(ActiveFrameText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
    }

    // ── 상태 ──
    public bool IsConnected => _svc.IsConnected;
    public bool IsStateConnected => _svc.IsStateConnected;
    public bool IsServoEnabled => _svc.IsServoEnabled;
    public string ServoText => _svc.IsServoEnabled ? "서보 ON" : "서보 OFF";
    public bool HasActiveFrame => _svc.State is { };
    public string ActiveFrameText => _svc.State is { } fs ? $"활성 툴 #{fs.Tool} / 작업물 #{fs.User}" : "";
    public bool HasError => _svc.State is { ErrorCode: not 0 };
    public string ErrorText => _svc.State is { ErrorCode: not 0 } st ? $"오류 코드 {st.ErrorCode} — '오류 해제' 필요" : "";

    // ── 좌표계 변경 ──
    [ObservableProperty] private int _frameId;
    public Pose6 Frame { get; } = new();

    // ── 이동 ──
    public Pose6 Target { get; } = new();
    [ObservableProperty] private int _moveTool = 1;
    [ObservableProperty] private int _vel = 20;

    // ── 작업물 교시 ──
    [ObservableProperty] private int _wobjId;
    [ObservableProperty] private int _wobjMethodIndex;     // 0 원점-X-Z, 1 원점-X-XY평면
    [ObservableProperty] private int _teachModeIndex;      // 0 조그-캡처, 1 수동입력
    public Pose6[] Points { get; } = { new(), new(), new() };
    public bool[] Captured { get; } = { false, false, false };
    [ObservableProperty] private string _capture0Text = "미캡처";
    [ObservableProperty] private string _capture1Text = "미캡처";
    [ObservableProperty] private string _capture2Text = "미캡처";
    [ObservableProperty] private string? _wobjResultText;
    [ObservableProperty] private string? _wobjHintText;
    public bool IsJogTeach => TeachModeIndex == 0;
    partial void OnTeachModeIndexChanged(int value) => OnPropertyChanged(nameof(IsJogTeach));

    // ── 오프셋 이동 ──
    [ObservableProperty] private int _offTool = 1;
    [ObservableProperty] private int _offUser;
    public Pose6 Offset { get; } = new();
    [ObservableProperty] private int _offVel = 20;
    [ObservableProperty] private int _offModeIndex;        // 0 원점 기준(절대), 1 현재 기준(증분)

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    // ── 안전 바 ──
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
    private async Task EmergencyStop()
    {
        _opCts?.Cancel();
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

    // ── 좌표계 ──
    [RelayCommand]
    private Task LoadToolCoord() => Run($"공구 #{FrameId} 불러오기", async ct =>
    {
        var c = await _svc.Rpc.GetToolCoordAsync(FrameId, ct);
        Frame.FromArray(c);
        return 0;
    });

    [RelayCommand]
    private Task LoadWObjCoord() => Run($"작업물 #{FrameId} 불러오기", async ct =>
    {
        var c = await _svc.Rpc.GetWObjCoordAsync(FrameId, ct);
        Frame.FromArray(c);
        return 0;
    });

    [RelayCommand]
    private Task SetToolCoord() => Run("공구 좌표계 설정", ct => _svc.Rpc.SetToolCoordAsync(FrameId, Frame.ToArray(), ct: ct));

    [RelayCommand]
    private Task SetWObjCoord() => Run("사용자 좌표계 설정", ct => _svc.Rpc.SetWObjCoordAsync(FrameId, Frame.ToArray(), ct: ct));

    // ── 이동 ──
    [RelayCommand]
    private Task MoveL() => Run("MoveL", ct => _svc.Rpc.MoveLAsync(Target.ToArray(), tool: MoveTool, user: 0, vel: Vel, ct: ct));

    [RelayCommand]
    private Task UseCurrentPose() => Run("현재 포즈 조회", async ct =>
    {
        var pose = await _svc.Rpc.GetTcpPoseInBaseAsync(MoveTool, ct);
        Target.FromArray(pose);
        return 0;
    });

    // ── 작업물 교시 ──
    /// <summary>점 N 캡처 — 현재 TCP 의 <b>베이스 기준</b> 포즈(교시 워크플로 공구 기준)를 앱에 보관한다.
    /// 컨트롤러 3점 버퍼(SetWObjCoordPoint)는 기록 시점의 활성 작업물 프레임 기준이라 점 사이에 프레임이
    /// 바뀌면(작업물 원점 이동 후 점1 캡처 → 베이스 조그 → 점2·3) 점1이 (0,0,0)으로 남아 엉뚱한 프레임이
    /// 등록되고 원점 이동이 112 를 냈다. 버퍼를 쓰지 않고 등록도 클라이언트 계산으로 한다.</summary>
    [RelayCommand]
    private Task CapturePoint(string pointNumStr)
    {
        int pointNum = int.Parse(pointNumStr);
        Captured[pointNum - 1] = false;
        SetCaptureText(pointNum, "미캡처");
        return Run($"점{pointNum} 캡처", async ct =>
        {
            var pose = await _svc.Rpc.GetTcpPoseInBaseAsync(OffTool, ct);
            Points[pointNum - 1].FromArray(pose);
            Captured[pointNum - 1] = true;
            var p = Points[pointNum - 1];
            SetCaptureText(pointNum, $"캡처됨 ({p.V0:0.0}, {p.V1:0.0}, {p.V2:0.0})");
            return 0;
        });
    }

    private void SetCaptureText(int pointNum, string text)
    {
        switch (pointNum) { case 1: Capture0Text = text; break; case 2: Capture1Text = text; break; case 3: Capture2Text = text; break; }
    }

    [RelayCommand]
    private Task RegisterWObj() => Run("작업물 좌표계 등록", async ct =>
    {
        if (IsJogTeach && Captured.Any(c => !c))
            throw new InvalidOperationException("점1·점2·점3을 모두 캡처한 뒤 등록하세요.");
        var pose = await _svc.Rpc.RegisterWObjFromPointsAsync(WobjId,
            Points[0].ToArray(), Points[1].ToArray(), Points[2].ToArray(), WobjMethodIndex, ct: ct);
        WobjResultText = $"등록됨 #{WobjId} : {string.Join(", ", pose.Select(v => v.ToString("0.0")))}";

        // 편의: 오프셋 이동을 '방금 등록한 좌표계 원점(절대)'으로 미리 채운다. 회전은 점1 캡처 당시 공구 자세를
        // 새 프레임 기준으로 환산한 값 — [0,0,0]으로 두면 공구가 프레임 축과 정렬돼 자세에 따라 도달 불가(112)가 난다.
        OffUser = WobjId;
        OffModeIndex = 0;
        var p1InFrame = FrameMath.ToFrame(Points[0].ToArray(), pose);
        Offset.FromArray(new[] { 0.0, 0.0, 0.0, Math.Round(p1InFrame[3], 2), Math.Round(p1InFrame[4], 2), Math.Round(p1InFrame[5], 2) });
        WobjHintText = $"오프셋 이동을 원점(절대, user #{WobjId}) + 점1 캡처 자세(dRx {Offset.V3:0.0}, dRy {Offset.V4:0.0}, dRz {Offset.V5:0.0})로 채웠습니다. " +
                       "'오프셋 이동'을 누르면 점1 위치·자세로 갑니다.";
        return 0;
    });

    // ── 오프셋 이동 ──
    [RelayCommand]
    private Task MoveByOffset() => Run("오프셋 이동", async ct =>
    {
        if (OffModeIndex == 0)
            return await _svc.Rpc.MoveLAsync(Offset.ToArray(), tool: OffTool, user: OffUser, vel: OffVel,
                                             acc: 100, ovl: 100, blendR: -1, ct: ct);
        var current = await _svc.Rpc.GetTcpPoseInBaseAsync(OffTool, ct);
        return await _svc.Rpc.MoveByOffsetAsync(current, OffUser, Offset.ToArray(), tool: OffTool, vel: OffVel, ct: ct);
    });

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
}

/// <summary>6-DOF 포즈 입력(X,Y,Z,Rx,Ry,Rz) — NumericUpDown 바인딩용 관찰 가능 래퍼.</summary>
public sealed partial class Pose6 : ObservableObject
{
    [ObservableProperty] private double _v0;
    [ObservableProperty] private double _v1;
    [ObservableProperty] private double _v2;
    [ObservableProperty] private double _v3;
    [ObservableProperty] private double _v4;
    [ObservableProperty] private double _v5;

    public double[] ToArray() => new[] { V0, V1, V2, V3, V4, V5 };

    public void FromArray(double[] a)
    {
        if (a.Length > 0) V0 = a[0];
        if (a.Length > 1) V1 = a[1];
        if (a.Length > 2) V2 = a[2];
        if (a.Length > 3) V3 = a[3];
        if (a.Length > 4) V4 = a[4];
        if (a.Length > 5) V5 = a[5];
    }
}
