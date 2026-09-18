using Avalonia.Controls;

namespace HD.AMR.Desktop.Views;

public partial class HandEyeView : UserControl
{
    public HandEyeView()
    {
        InitializeComponent();
        // 자세 변경은 작업자가 조그로 한다 — 이 화면은 코봇을 스스로 움직이지 않는다.
        this.FindControl<Button>("JogPopupBtn")!.Click += (_, _) => JogWindow.Open();
    }
}
