using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class VisionTrainingView : UserControl
{
    public VisionTrainingView()
    {
        InitializeComponent();
        // 외부 검증용 이미지 업로드(원본 InputFile 대응) — StorageProvider 로 파일을 읽어 VM 에 넘긴다.
        this.FindControl<Button>("UploadBtn")!.Click += async (_, _) =>
        {
            if (DataContext is not VisionTrainingViewModel vm) return;
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "검증용 이미지 선택",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
            });
            if (picked.Count == 0) return;
            await using var s = await picked[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            if (ms.Length > 30L * 1024 * 1024) return;   // 최대 30MB
            await vm.InferUploadAsync(picked[0].Name, ms.ToArray());
        };
    }
}
