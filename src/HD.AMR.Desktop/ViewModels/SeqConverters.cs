using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HD.AMR.Desktop.ViewModels;

public static class SeqConverters
{
    // HD_ACS 작업 상태색과 동일(Themes/Brushes.axaml 의 Fill/Danger/WarnBorder 계열)
    private static readonly IBrush Wait = new SolidColorBrush(Color.FromRgb(0x5D, 0x6D, 0x7E));
    private static readonly IBrush Running = new SolidColorBrush(Color.FromRgb(0x29, 0x80, 0xB9));
    private static readonly IBrush Done = new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60));
    private static readonly IBrush Fail = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(0xB7, 0x95, 0x0B));

    /// <summary>단계 상태 종류(0 대기/1 실행/2 완료/3 실패/4 경고) → 배지 색.</summary>
    public static readonly IValueConverter StatusBrush =
        new FuncValueConverter<int, IBrush>(k => k switch
        {
            1 => Running, 2 => Done, 3 => Fail, 4 => Warn, _ => Wait,
        });
}

public static class SeqRowConverters
{
    private static readonly IBrush Current = new SolidColorBrush(Color.FromArgb(0x1A, 0x29, 0x80, 0xB9));

    /// <summary>현재 실행 중인 행 강조.</summary>
    public static readonly IValueConverter CurrentBrush =
        new FuncValueConverter<bool, IBrush>(on => on ? Current : Brushes.Transparent);
}
