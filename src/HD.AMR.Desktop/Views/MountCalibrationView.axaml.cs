using Avalonia.Controls;

namespace HD.AMR.Desktop.Views;

public partial class MountCalibrationView : UserControl
{
    public MountCalibrationView()
    {
        InitializeComponent();
        // JogWindow.Open() 은 정적이라 커맨드가 없다 — CobotView/CameraView 와 동일하게 코드비하인드에서 연결.
        this.FindControl<Button>("JogPopupBtn")!.Click += (_, _) => JogWindow.Open();
    }
}
