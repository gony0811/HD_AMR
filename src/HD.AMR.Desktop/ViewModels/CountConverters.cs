using Avalonia.Data.Converters;

namespace HD.AMR.Desktop.ViewModels;

public static class CountConverters
{
    /// <summary>컬렉션 Count(int) 가 0 이면 true(비어 있음 표시용).</summary>
    public static readonly IValueConverter IsZero =
        new FuncValueConverter<int, bool>(n => n == 0);
}
