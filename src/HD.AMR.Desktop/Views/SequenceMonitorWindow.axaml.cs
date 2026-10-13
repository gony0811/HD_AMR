using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class SequenceMonitorWindow : Window
{
    // Peak ROI = 청록, Bead ROI = 노랑 — 용접 추적 화면과 같은 색.
    private static readonly IBrush PeakRoiBrush = new SolidColorBrush(Color.Parse("#00C6E0"));
    private static readonly IBrush BeadRoiBrush = new SolidColorBrush(Color.Parse("#FFD000"));

    private readonly Grid _liveHost;
    private readonly Canvas _liveOverlay;
    private readonly Grid _flatHost;
    private readonly Canvas _flatOverlay;
    private SequenceMonitorViewModel? _vm;

    public SequenceMonitorWindow()
    {
        InitializeComponent();
        _liveHost = this.FindControl<Grid>("LiveHost")!;
        _liveOverlay = this.FindControl<Canvas>("LiveOverlay")!;
        _flatHost = this.FindControl<Grid>("FlatHost")!;
        _flatOverlay = this.FindControl<Canvas>("FlatOverlay")!;
        _liveHost.SizeChanged += (_, _) => DrawLiveRoi();
        _flatHost.SizeChanged += (_, _) => DrawFlat();
    }

    public SequenceMonitorWindow(SequenceMonitorViewModel vm) : this()
    {
        DataContext = vm;
        _vm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        // 마지막 스텝(모니터 닫기)이 닫기를 요청하면 잠시 후 자동으로 닫는다(마지막 로그 가독).
        vm.CloseRequested += OnCloseRequested;
        Closed += (_, _) =>
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.Dispose();
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SequenceMonitorViewModel.LiveRoi) or nameof(SequenceMonitorViewModel.CameraKind))
            DrawLiveRoi();
        else if (e.PropertyName is nameof(SequenceMonitorViewModel.FlatSnapshot))
            DrawFlat();
    }

    private void DrawLiveRoi()
    {
        if (_vm is null) return;
        var brush = _vm.CameraKind == MonitorCameraKind.Bead ? BeadRoiBrush : PeakRoiBrush;
        FlatOverlayRenderer.DrawRoi(_liveOverlay, _vm.LiveRoi, _vm.LiveFrameW, _vm.LiveFrameH, _liveHost.Bounds.Size, brush);
    }

    private void DrawFlat() => FlatOverlayRenderer.Draw(_flatOverlay, _vm?.FlatSnapshot, _flatHost.Bounds.Size);

    private void OnCloseRequested() => Dispatcher.UIThread.Post(async () =>
    {
        await System.Threading.Tasks.Task.Delay(500);
        Close();
    });
}
