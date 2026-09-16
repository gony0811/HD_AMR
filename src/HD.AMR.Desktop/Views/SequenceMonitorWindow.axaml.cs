using Avalonia.Controls;
using Avalonia.Threading;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class SequenceMonitorWindow : Window
{
    public SequenceMonitorWindow()
    {
        InitializeComponent();
    }

    public SequenceMonitorWindow(SequenceMonitorViewModel vm) : this()
    {
        DataContext = vm;
        // 마지막 스텝(모니터 닫기)이 닫기를 요청하면 잠시 후 자동으로 닫는다(마지막 로그 가독).
        vm.CloseRequested += OnCloseRequested;
        Closed += (_, _) => { vm.CloseRequested -= OnCloseRequested; vm.Dispose(); };
    }

    private void OnCloseRequested() => Dispatcher.UIThread.Post(async () =>
    {
        await System.Threading.Tasks.Task.Delay(500);
        Close();
    });
}
