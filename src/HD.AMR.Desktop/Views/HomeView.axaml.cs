using Avalonia.Controls;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class HomeView : UserControl
{
    private HomeViewModel? _vm;
    private BeadManualLabelWindow? _beadTest;

    public HomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.BeadPopupRequested -= OpenBeadTest;
            _vm = DataContext as HomeViewModel;
            if (_vm is not null) _vm.BeadPopupRequested += OpenBeadTest;
        };
    }

    // 수동 비드 입력 테스트 팝업 — 이미 열려 있으면 앞으로. 메인 창을 owner 로 연다(종료 시 함께 닫힘).
    private void OpenBeadTest()
    {
        if (_beadTest is { IsVisible: true } w) { w.Activate(); return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        _beadTest = new BeadManualLabelWindow(Program.Services);
        _beadTest.Closed += (_, _) => _beadTest = null;
        _beadTest.Show(owner);
    }
}
