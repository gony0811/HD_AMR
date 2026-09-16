using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>안전 신호등 램프: 켜짐이면 색상, 꺼짐이면 어둡게.</summary>
public static class LampConverters
{
    private static readonly IBrush Off = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x4D));

    public static readonly IValueConverter Red =
        new FuncValueConverter<bool, IBrush>(on => on ? Brushes.Red : Off);
    public static readonly IValueConverter Yellow =
        new FuncValueConverter<bool, IBrush>(on => on ? Brushes.Gold : Off);
    public static readonly IValueConverter Green =
        new FuncValueConverter<bool, IBrush>(on => on ? Brushes.LimeGreen : Off);
}
