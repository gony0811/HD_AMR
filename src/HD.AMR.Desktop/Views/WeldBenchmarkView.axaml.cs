using Avalonia.Controls;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class WeldBenchmarkView : UserControl
{
    public WeldBenchmarkView()
    {
        InitializeComponent();
        // 격자(*) 칸이 내용 크기로 줄지 않게 페이지 폭을 min(창 폭, MaxWidth) 로 고정(왼쪽 정렬 유지).
        var pageRoot = this.FindControl<StackPanel>("PageRoot")!;
        SizeChanged += (_, e) => pageRoot.Width = Math.Min(e.NewSize.Width, pageRoot.MaxWidth);
        this.FindControl<Button>("PickValDirBtn")!.Click += async (_, _) =>
        {
            if (DataContext is not WeldBenchmarkViewModel vm) return;
            var path = await FolderPicker.PickAsync(this, "검증 폴더 선택", vm.ValDir ?? vm.TrainDirs.LastOrDefault());
            if (path is not null) await vm.SetValDirAsync(path);
        };
    }
}
