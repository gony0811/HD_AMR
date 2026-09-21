using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// 별도 조그 팝업 창. 기존 JogPopup.razor(window.open) 대응 — 같은 CobotService 싱글톤을 공유해
/// 동일 로봇을 제어하며, 안전 헤더 포함(ShowSafetyHeader=true). 티칭 중 띄우므로 작업물 프레임
/// 자동 초기화는 끈다(AutoResetBaseFrame=false).
/// </summary>
public partial class JogWindow : Window
{
    private static JogWindow? _instance;
    private readonly JogRibbonViewModel _vm;
    private readonly DispatcherTimer _timer;

    public JogWindow()
    {
        InitializeComponent();
        var sp = Program.Services;
        _vm = new JogRibbonViewModel(sp.GetRequiredService<CobotService>(), sp.GetRequiredService<SequenceRunGate>(),
            showSafetyHeader: true, autoResetBaseFrame: false);
        DataContext = _vm;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _vm.RefreshState();
        Opened += (_, _) => { _vm.RefreshState(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _vm.StopAll(); _instance = null; };
    }

    /// <summary>단일 인스턴스로 열기 — 이미 열려 있으면 앞으로 가져온다(원본 window.open 이름 재사용과 동일).
    /// 반드시 MainWindow 를 owner 로 연다 — 소유되지 않은 최상위 창은 메인 창 종료 후에도 남아
    /// OnLastWindowClose 종료를 막는다(잔존 프로세스 원인).</summary>
    public static void Open()
    {
        if (_instance is { IsVisible: true } w) { w.Activate(); return; }
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        _instance = new JogWindow();
        _instance.Show(owner);
    }
}
