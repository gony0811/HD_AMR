using Avalonia.Data.Converters;

namespace HD.AMR.Desktop.ViewModels;

public static class TrainConverters
{
    /// <summary>Python 상태 종류(0 확인중/1 사용가능/2 ultralytics 미설치/3 Python 없음) → 배지 텍스트.</summary>
    public static readonly IValueConverter PyText = new FuncValueConverter<int, string>(k => k switch
    {
        1 => "사용 가능", 2 => "ultralytics 미설치", 3 => "Python 없음", _ => "확인 중…",
    });

    /// <summary>바이트 → "x.x KB".</summary>
    public static readonly IValueConverter Kb = new FuncValueConverter<long, string>(b => $"{b / 1024.0:0.0} KB");
}
