using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using HD.AMR.App.Service.Sequence;
using HD.AMR.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.Views;

public partial class SequenceView : UserControl
{
    private SequenceMonitorWindow? _monitor;

    public SequenceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is SequenceViewModel vm)
            vm.MonitorRequested += OpenMonitor;
    }

    // 시퀀스 실행 시 별도 모니터 창을 연다(원본 window.open 대응). 이미 열려 있으면 앞으로 가져온다.
    private void OpenMonitor()
    {
        if (_monitor is { } w && w.IsVisible) { w.Activate(); return; }

        var monitorSvc = Program.Services.GetRequiredService<SequenceMonitorService>();
        _monitor = new SequenceMonitorWindow(new SequenceMonitorViewModel(monitorSvc));
        _monitor.Closed += (_, _) => _monitor = null;

        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null) _monitor.Show(owner);
        else _monitor.Show();
    }
}
