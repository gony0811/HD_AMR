using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HD.AMR.Desktop.ViewModels;

public static class OkErrConverters
{
    // 글씨색 — AppErrorBrush / AppGoodBrush 와 동일
    private static readonly IBrush Err = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x5B));
    private static readonly IBrush Ok = new SolidColorBrush(Color.FromRgb(0x47, 0xBC, 0x6C));

    /// <summary>true(오류)=빨강, false=초록.</summary>
    public static readonly IValueConverter Brush = new FuncValueConverter<bool, IBrush>(e => e ? Err : Ok);
}
