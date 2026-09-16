using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HD.AMR.Desktop.ViewModels;

public static class SeqConverters
{
    private static readonly IBrush Wait = new SolidColorBrush(Color.FromRgb(0x6C, 0x75, 0x7D));
    private static readonly IBrush Running = new SolidColorBrush(Color.FromRgb(0x0D, 0x6E, 0xFD));
    private static readonly IBrush Done = new SolidColorBrush(Color.FromRgb(0x19, 0x87, 0x54));
    private static readonly IBrush Fail = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(0xB0, 0x8A, 0x00));

    /// <summary>단계 상태 종류(0 대기/1 실행/2 완료/3 실패/4 경고) → 배지 색.</summary>
    public static readonly IValueConverter StatusBrush =
        new FuncValueConverter<int, IBrush>(k => k switch
        {
            1 => Running, 2 => Done, 3 => Fail, 4 => Warn, _ => Wait,
        });
}

public static class SeqRowConverters
{
    private static readonly IBrush Current = new SolidColorBrush(Color.FromArgb(40, 0x0D, 0x6E, 0xFD));

    /// <summary>현재 실행 중인 행 강조.</summary>
    public static readonly IValueConverter CurrentBrush =
        new FuncValueConverter<bool, IBrush>(on => on ? Current : Brushes.Transparent);
}
