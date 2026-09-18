using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// Z축 텔레스코픽 리프트(EBLUM 3채널 홀 동기 컨트롤러) 제어·모니터링.
///
/// <b>이 화면은 실제로 1m 행정의 리프트를 움직인다 — 그 위에 코봇이 올라가 있다.</b> 안전 설계:
///   · 상승/하강은 <b>누르고 있는 동안만</b> 동작한다(hold-to-move). 떼면 즉시 정지 명령.
///   · 서비스 쪽 데드맨이 유지 신호가 끊기면 자동 정지하므로, 화면이 멈추거나 페이지를 떠나도 계속 가지 않는다.
///   · 모든 동작 명령은 "동작 허용" 체크 후에만 활성화된다(실수 클릭 방지).
///   · <see cref="OnDeactivated"/>·<see cref="Dispose"/> 에서 정지한다.
/// 프로토콜 계층은 <see cref="TelescopicProtocol"/>, 통신·데드맨은 <see cref="TelescopicService"/>.
/// </summary>
public sealed partial class LiftViewModel : ViewModelBase
{
    private readonly TelescopicService _svc;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty] private bool _motionArmed;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private int _targetHeightMm;
    [ObservableProperty] private int _memorySlot = 1;

    public LiftViewModel(TelescopicService svc)
    {
        _svc = svc;
        TargetHeightMm = svc.Settings.MinHeightMm;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) =>
        {
            OnPropertyChanged(string.Empty);
            MoveToHeightCommand.NotifyCanExecuteChanged();
            MoveToMemoryCommand.NotifyCanExecuteChanged();
            SaveMemoryCommand.NotifyCanExecuteChanged();
            ResetCommand.NotifyCanExecuteChanged();
            ClearErrorCommand.NotifyCanExecuteChanged();
        };
    }

    public override void OnActivated() => _timer.Start();

    // 내비게이션이 OnDeactivated → Dispose(→ OnDeactivated) 로 두 번 호출한다. 멱등이어야 한다.
    public override void OnDeactivated()
    {
        _timer.Stop();
        _ = SafeStopAsync();   // 페이지를 떠나면 반드시 멈춘다.
    }

    public override void Dispose()
    {
        base.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    // ── 상태 표시 ───────────────────────────────────────────────────
    public bool IsConnected => _svc.IsConnected;
    public bool IsJogging => _svc.IsJogging;
    public string? LastError => _svc.LastError;
    public string PortText => $"{_svc.Settings.PortName} @ {_svc.Settings.BaudRate} 8N1";
    public string RangeText => $"운영 범위 {_svc.Settings.MinHeightMm}~{_svc.Settings.MaxHeightMm} mm";
    public string TrafficText => $"TX {_svc.LastTx ?? "—"}   RX {_svc.LastRx ?? "—"}";

    private TelescopicStatus? S => _svc.Latest;

    public bool HasStatus => S is not null;
    public string HeightText => S is { } s && s.HeightMm >= 0 ? $"{s.HeightMm} {s.UnitText}" : "—";
    public string ModeText => S?.ModeText ?? "—";
    public string ErrorText => S is { } s ? $"{s.ErrorCode:D2} · {s.ErrorText}" : "—";
    public bool IsHealthy => S is { IsHealthy: true };
    public bool HasFault => S is { IsHealthy: false };
    public bool IsActive => S is { Active: true };
    public bool IsUnlocked => S is { Unlocked: true };
    public bool IsMoving => S is { IsMoving: true };
    public string ActiveText => S is null ? "—" : S.Active ? "활성" : "슬립";
    public string LockText => S is null ? "—" : S.Unlocked ? "잠금 해제" : "잠김";

    /// <summary>사양서 14자 배치와 다른 응답이면 중간 필드를 신뢰할 수 없다는 표시.</summary>
    public bool LayoutMismatch => S is { ExactLayout: false };

    public string AgeText
    {
        get
        {
            if (_svc.LatestUtc is not { } t) return "상태 수신 대기 중…";
            var age = (DateTime.UtcNow - t).TotalSeconds;
            return age < 2 ? "마지막 갱신: 방금" : $"마지막 갱신: {age:0}초 전";
        }
    }

    public bool CanCommand => IsConnected && MotionArmed && !Busy;

    /// <summary>동작 버튼이 왜 비활성인지 — 회색 버튼만 두지 않는다.</summary>
    public string BlockedReason =>
        !IsConnected ? "컨트롤러에 연결되지 않았습니다 — 포트·TTL 변환 모듈·결선을 확인하세요."
        : !MotionArmed ? "'동작 허용'에 체크해야 리프트를 움직일 수 있습니다."
        : Busy ? "처리 중입니다…"
        : "";

    // ── 조그(누르고 있는 동안만) ────────────────────────────────────
    /// <summary>상승 버튼 누름 — 뗄 때까지 유지된다.</summary>
    public async Task JogUpPressedAsync()
    {
        if (!CanCommand) return;
        try { await _svc.BeginJogAsync(up: true, _cts.Token); Notify("상승 중 — 버튼을 떼면 정지합니다.", false); }
        catch (Exception ex) { Notify($"상승 실패: {ex.Message}", true); }
    }

    /// <summary>하강 버튼 누름.</summary>
    public async Task JogDownPressedAsync()
    {
        if (!CanCommand) return;
        try { await _svc.BeginJogAsync(up: false, _cts.Token); Notify("하강 중 — 버튼을 떼면 정지합니다.", false); }
        catch (Exception ex) { Notify($"하강 실패: {ex.Message}", true); }
    }

    /// <summary>버튼을 누르고 있는 동안 데드맨 갱신 — 뷰의 타이머가 부른다.</summary>
    public void JogHeld() => _svc.KeepJogAlive();

    /// <summary>버튼 뗌 / 포인터 이탈 / 창 비활성 — 즉시 정지.</summary>
    public async Task JogReleasedAsync()
    {
        try { await _svc.EndJogAsync(_cts.Token); }
        catch (Exception ex) { Notify($"정지 명령 실패: {ex.Message} — 물리 정지 버튼을 사용하세요.", true); }
    }

    // ── 단발 명령 ───────────────────────────────────────────────────
    /// <summary>정지 — 연결만 되어 있으면 '동작 허용' 없이도 언제나 눌린다.</summary>
    [RelayCommand]
    private async Task Stop()
    {
        try { await _svc.StopMotionAsync(_cts.Token); Notify("정지 명령을 보냈습니다.", false); }
        catch (Exception ex) { Notify($"정지 실패: {ex.Message} — 물리 정지 버튼을 사용하세요.", true); }
    }

    [RelayCommand(CanExecute = nameof(CanCommand))]
    private async Task MoveToHeight()
    {
        Busy = true;
        try
        {
            var ack = await _svc.MoveToHeightAsync(TargetHeightMm, _cts.Token);
            Notify(ack switch
            {
                true => $"{TargetHeightMm} mm 이동 명령을 수락했습니다.",
                false => "컨트롤러가 명령을 거부했습니다(실행 결과 1) — 활성·잠금 해제 상태인지 확인하세요.",
                null => "컨트롤러 응답을 해석하지 못했습니다 — 통신 상태를 확인하세요.",
            }, ack != true);
        }
        catch (ArgumentOutOfRangeException ex) { Notify(ex.Message, true); }
        catch (Exception ex) { Notify($"이동 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCommand))]
    private async Task MoveToMemory()
    {
        Busy = true;
        try
        {
            await _svc.MoveToMemoryAsync(MemorySlot, _cts.Token);
            Notify($"메모리 위치 {MemorySlot} 이동 명령을 보냈습니다.", false);
        }
        catch (Exception ex) { Notify($"메모리 이동 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCommand))]
    private async Task SaveMemory()
    {
        Busy = true;
        try
        {
            await _svc.SaveMemoryAsync(MemorySlot, _cts.Token);
            Notify($"현재 위치를 메모리 {MemorySlot} 에 저장했습니다(2초 유지).", false);
        }
        catch (Exception ex) { Notify($"메모리 저장 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCommand))]
    private async Task Reset()
    {
        Busy = true;
        try
        {
            await _svc.ResetAsync(_cts.Token);
            Notify("추진기 리셋이 완료되었습니다 — 운행 모드 '리셋 중' → '정지' 전환을 확인했습니다.", false);
        }
        catch (Exception ex) { Notify($"리셋 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    private bool CanClearError => IsConnected && !Busy;

    [RelayCommand(CanExecute = nameof(CanClearError))]
    private async Task ClearError()
    {
        Busy = true;
        try
        {
            var ok = await _svc.ClearErrorAsync(_cts.Token);
            Notify(ok ? "에러 코드를 클리어했습니다." : "클리어 응답(ClearErr OK)을 받지 못했습니다.", !ok);
        }
        catch (Exception ex) { Notify($"에러 클리어 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RefreshNow()
    {
        try
        {
            var s = await _svc.RefreshAsync(_cts.Token);
            Notify(s is null ? "상태 응답을 받지 못했습니다." : "상태를 갱신했습니다.", s is null);
        }
        catch (Exception ex) { Notify($"상태 조회 실패: {ex.Message}", true); }
    }

    private async Task SafeStopAsync()
    {
        try { await _svc.StopMotionAsync(CancellationToken.None); }
        catch { /* 페이지 이탈 중 — 서비스 데드맨이 최종 방어선이다. */ }
    }

    private void Notify(string msg, bool error) { Message = msg; IsError = error; }
}
