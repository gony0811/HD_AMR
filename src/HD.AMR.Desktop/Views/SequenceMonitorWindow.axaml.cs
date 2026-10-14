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
    private readonly Grid _centeredHost;
    private readonly Canvas _centeredOverlay;
    private SequenceMonitorViewModel? _vm;

    public SequenceMonitorWindow()
    {
        InitializeComponent();
        _liveHost = this.FindControl<Grid>("LiveHost")!;
        _liveOverlay = this.FindControl<Canvas>("LiveOverlay")!;
        _flatHost = this.FindControl<Grid>("FlatHost")!;
        _flatOverlay = this.FindControl<Canvas>("FlatOverlay")!;
        _centeredHost = this.FindControl<Grid>("CenteredHost")!;
        _centeredOverlay = this.FindControl<Canvas>("CenteredOverlay")!;
        _liveHost.SizeChanged += (_, _) => DrawLiveRoi();
        _flatHost.SizeChanged += (_, _) => DrawFlat();
        _centeredHost.SizeChanged += (_, _) => DrawFlat();

        // 마우스 hover → 그 위치 깊이(mm). 라이브는 최신 프레임, 정지 화면은 그 순간 프레임에서 읽는다.
        AttachProbe(_liveHost, "LiveProbe", (u, v) => _vm?.ProbeLive(u, v));
        AttachProbe(this.FindControl<Grid>("FlatLiveHost")!, "FlatLiveProbe", (u, v) => _vm?.ProbeLive(u, v));
        AttachProbe(_flatHost, "FlatProbe", (u, v) => _vm?.ProbeFlat(u, v));
        AttachProbe(_centeredHost, "CenteredProbe", (u, v) => _vm?.ProbeCentered(u, v));
    }

    private void AttachProbe(Grid host, string canvasName, Func<double, double, int?> sample)
    {
        var canvas = this.FindControl<Canvas>(canvasName)!;
        var text = new TextBlock { Foreground = Brushes.White, FontSize = 11 };
        var label = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#CC000000")),
            CornerRadius = new Avalonia.CornerRadius(3),
            Padding = new Avalonia.Thickness(4, 2),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = text,
        };
        canvas.Children.Add(label);

        host.PointerMoved += (_, e) =>
        {
            var size = host.Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0) return;
            var p = e.GetPosition(host);
            var mm = sample(Math.Clamp(p.X / size.Width, 0, 1), Math.Clamp(p.Y / size.Height, 0, 1));
            text.Text = mm is { } m ? $"{m} mm" : "— mm";
            // 오른쪽·아래 가장자리에서는 라벨을 커서 반대편으로 붙여 잘리지 않게 한다.
            Canvas.SetLeft(label, p.X + 70 > size.Width ? p.X - 66 : p.X + 10);
            Canvas.SetTop(label, p.Y + 26 > size.Height ? p.Y - 22 : p.Y + 10);
            label.IsVisible = true;
        };
        host.PointerExited += (_, _) => label.IsVisible = false;
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

    private void DrawFlat()
    {
        FlatOverlayRenderer.Draw(_flatOverlay, _vm?.FlatSnapshot, _flatHost.Bounds.Size);
        FlatOverlayRenderer.DrawExpectedLaserArea(_centeredOverlay, _vm?.FlatSnapshot, _centeredHost.Bounds.Size);
    }

    private void OnCloseRequested() => Dispatcher.UIThread.Post(async () =>
    {
        await System.Threading.Tasks.Task.Delay(500);
        Close();
    });
}
