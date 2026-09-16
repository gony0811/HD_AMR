using Avalonia.Data.Converters;
using Avalonia.Media;
using HD.AMR.App.Communication.Vision;

namespace HD.AMR.Desktop.ViewModels;

public static class VisionConverters
{
    private static readonly IBrush Tx = new SolidColorBrush(Color.FromArgb(60, 0x0D, 0x6E, 0xFD));
    private static readonly IBrush Rx = new SolidColorBrush(Color.FromArgb(60, 0x19, 0x87, 0x54));
    private static readonly IBrush Err = new SolidColorBrush(Color.FromArgb(70, 0xDC, 0x35, 0x45));

    /// <summary>로그 방향 → 행 배경.</summary>
    public static readonly IValueConverter RowBrush =
        new FuncValueConverter<LogDirection, IBrush>(d => d switch
        {
            LogDirection.Tx => Tx,
            LogDirection.Rx => Rx,
            LogDirection.Error => Err,
            _ => Brushes.Transparent,
        });
}
