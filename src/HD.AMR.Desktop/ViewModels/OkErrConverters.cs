using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HD.AMR.Desktop.ViewModels;

public static class OkErrConverters
{
    private static readonly IBrush Err = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
    private static readonly IBrush Ok = new SolidColorBrush(Color.FromRgb(0x19, 0x87, 0x54));

    /// <summary>true(오류)=빨강, false=초록.</summary>
    public static readonly IValueConverter Brush = new FuncValueConverter<bool, IBrush>(e => e ? Err : Ok);
}
