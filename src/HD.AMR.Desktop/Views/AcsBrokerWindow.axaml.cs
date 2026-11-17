using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HD.AMR.App.Service;
using HD.AMR.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// ACS MQTT 브로커 설정·연결 창 — 상단 상태바의 ACS 칩과 대시보드 ACS 카드의 [브로커 설정]에서 연다.
/// 한 번에 하나만 뜨고(이미 열려 있으면 앞으로), 메인 창을 owner 로 열어 종료 때 함께 닫힌다.
/// </summary>
public partial class AcsBrokerWindow : Window
{
    private static AcsBrokerWindow? _instance;
    private readonly AcsBrokerViewModel _vm;
    private readonly DispatcherTimer _timer;

    public AcsBrokerWindow()
    {
        InitializeComponent();
        _vm = new AcsBrokerViewModel(Program.Services.GetRequiredService<Vda5050AdapterService>());
        DataContext = _vm;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _vm.Refresh();
        Opened += (_, _) =>
        {
            _vm.Refresh();
            _timer.Start();
            var host = this.FindControl<TextBox>("HostBox");
            host?.Focus();
            host?.SelectAll();
        };
        Closed += (_, _) => { _timer.Stop(); _instance = null; };
    }

    public static void Open()
    {
        if (_instance is { IsVisible: true } w) { w.Activate(); return; }
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        _instance = new AcsBrokerWindow();
        _instance.Show(owner);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
