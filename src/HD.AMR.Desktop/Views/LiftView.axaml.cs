using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// Z축 리프트 제어 뷰. 상승/하강은 <b>누르고 있는 동안만</b> 동작하는 hold-to-move 라
/// 커맨드가 아니라 포인터 이벤트로 연결한다(JogRibbonView 의 Hold 모드와 같은 방식).
///
/// 누르고 있는 동안 타이머가 데드맨을 갱신하고, 떼기·포인터 이탈·취소·창 비활성 어디서든 정지한다.
/// 서비스 쪽 데드맨이 최종 방어선이므로 이 이벤트를 하나 놓쳐도 리프트는 멈춘다.
/// </summary>
public partial class LiftView : UserControl
{
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private LiftViewModel? Vm => DataContext as LiftViewModel;

    public LiftView()
    {
        InitializeComponent();
        _holdTimer.Tick += (_, _) => Vm?.JogHeld();

        Wire(this.FindControl<Button>("JogUpBtn")!, up: true);
        Wire(this.FindControl<Button>("JogDownBtn")!, up: false);

        // 페이지가 화면에서 빠지면 무조건 정지 — 창 전환·탭 이동 포함.
        DetachedFromVisualTree += (_, _) => Release();
    }

    private void Wire(Button button, bool up)
    {
        button.AddHandler(PointerPressedEvent, async (_, _) =>
        {
            if (Vm is not { } vm) return;
            _holdTimer.Start();
            if (up) await vm.JogUpPressedAsync();
            else await vm.JogDownPressedAsync();
        }, RoutingStrategies.Tunnel);

        button.AddHandler(PointerReleasedEvent, (_, _) => Release(), RoutingStrategies.Tunnel);
        button.PointerExited += (_, _) => Release();
        button.PointerCaptureLost += (_, _) => Release();
    }

    private void Release()
    {
        _holdTimer.Stop();
        if (Vm is { } vm) _ = vm.JogReleasedAsync();
    }
}
