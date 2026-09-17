using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>안전 신호등 램프: 켜짐이면 색상, 꺼짐이면 어둡게.</summary>
public static class LampConverters
{
    // HD_ACS 팔레트: Off=AppBorderBrush, 빨강=AppDangerHoverBrush, 노랑=AppWarnFgBrush, 초록=AppGoodBrush
    private static readonly IBrush Off = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly IBrush RedOn = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
    private static readonly IBrush YellowOn = new SolidColorBrush(Color.FromRgb(0xE6, 0xC3, 0x4A));
    private static readonly IBrush GreenOn = new SolidColorBrush(Color.FromRgb(0x47, 0xBC, 0x6C));

    public static readonly IValueConverter Red =
        new FuncValueConverter<bool, IBrush>(on => on ? RedOn : Off);
    public static readonly IValueConverter Yellow =
        new FuncValueConverter<bool, IBrush>(on => on ? YellowOn : Off);
    public static readonly IValueConverter Green =
        new FuncValueConverter<bool, IBrush>(on => on ? GreenOn : Off);
}
