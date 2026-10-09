using Avalonia.Data.Converters;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>상단 상태바 타워램프 대표 색(툴팁 문구용) — 정책상 세 색 중 하나만 ON 이지만 되읽기에
/// 일시 전부 OFF 가 보일 수 있어 None 포함. 경광등 그림은 색별 bool 로 따로 그린다.</summary>
public enum TowerLampColor { None, Red, Yellow, Green }

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

    /// <summary>true→"Enabled", false→"Disabled".</summary>
    public static readonly IValueConverter EnabledText =
        new FuncValueConverter<bool, string>(v => v ? "Enabled" : "Disabled");
}
