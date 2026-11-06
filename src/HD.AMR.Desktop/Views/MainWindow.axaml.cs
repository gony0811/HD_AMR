using Avalonia.Controls;
using Avalonia.Threading;
using HD.AMR.App.Service.Sequence;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly BeadManualLabelHub? _beadHub;
    private BeadManualLabelWindow? _beadWindow;

    public MainWindow()
    {
        InitializeComponent();

        // 시퀀스(수동 실행·ACS 자동 모두)가 수동 비드 입력을 요청하면 어느 화면에 있든 팝업을 띄운다.
        _beadHub = Program.Services?.GetService<BeadManualLabelHub>();
        if (_beadHub is not null)
        {
            _beadHub.Changed += OnBeadRequestChanged;
            Closed += (_, _) => _beadHub.Changed -= OnBeadRequestChanged;
        }
    }

    private void OnBeadRequestChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_beadHub?.Pending is not { } req) return;
        if (_beadWindow is { } w && w.RequestId == req.Id) { w.Activate(); return; }

        _beadWindow?.Close();
        _beadWindow = new BeadManualLabelWindow(req, _beadHub);
        _beadWindow.Closed += (s, _) => { if (ReferenceEquals(_beadWindow, s)) _beadWindow = null; };
        _beadWindow.Show(this);
        _beadWindow.Activate();
    });
}
