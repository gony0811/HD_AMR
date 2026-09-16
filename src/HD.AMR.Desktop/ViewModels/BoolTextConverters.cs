using Avalonia.Data.Converters;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>불리언 → 표시 텍스트 컨버터 모음(XAML 에서 x:Static 으로 사용).</summary>
public static class BoolTextConverters
{
    /// <summary>true→"연결됨", false→"끊김".</summary>
    public static readonly IValueConverter Connection =
        new FuncValueConverter<bool, string>(v => v ? "연결됨" : "끊김");

    /// <summary>true→"연결", false→"미연결".</summary>
    public static readonly IValueConverter Connect =
        new FuncValueConverter<bool, string>(v => v ? "연결" : "미연결");

    /// <summary>true→"활성", false→"정지".</summary>
    public static readonly IValueConverter Stream =
        new FuncValueConverter<bool, string>(v => v ? "활성" : "정지");

    /// <summary>true→"ON", false→"OFF".</summary>
    public static readonly IValueConverter OnOff =
        new FuncValueConverter<bool, string>(v => v ? "ON" : "OFF");

    /// <summary>true→"수신", false→"끊김".</summary>
    public static readonly IValueConverter Recv =
        new FuncValueConverter<bool, string>(v => v ? "수신" : "끊김");
}
