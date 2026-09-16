using Avalonia.Controls;

namespace HD.AMR.Desktop.Views;

public partial class CobotView : UserControl
{
    public CobotView()
    {
        InitializeComponent();
        // 조그를 별도 창으로 연다(원본 window.open('/jog') 대응). 같은 CobotService 싱글톤 공유.
        this.FindControl<Button>("JogPopupBtn")!.Click += (_, _) => JogWindow.Open();
    }
}
