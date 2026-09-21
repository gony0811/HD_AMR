using Avalonia.Data.Converters;
using Avalonia.Media;
using HD.AMR.App.Models;

namespace HD.AMR.Desktop.ViewModels;

public static class LaserConverters
{
    // 채움형 태그(흰 글씨) — AppGoodFillBrush / ACS 대기 회색
    private static readonly IBrush On = new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60));
    private static readonly IBrush Off = new SolidColorBrush(Color.FromRgb(0x5D, 0x6D, 0x7E));

    public static readonly IValueConverter DimIfOff = new FuncValueConverter<bool, double>(on => on ? 1.0 : 0.45);
    public static readonly IValueConverter OnOffBrush = new FuncValueConverter<bool, IBrush>(on => on ? On : Off);
    public static readonly IValueConverter MeasText = new FuncValueConverter<bool, string>(on => on ? "측정중" : "범위밖");
    public static readonly IValueConverter PoseText = new FuncValueConverter<bool, string>(v => v ? "계산됨" : "대기");
    public static readonly IValueConverter RefText = new FuncValueConverter<bool, string>(v => v ? "기준 적용중" : "미설정");
    public static readonly IValueConverter RobotText = new FuncValueConverter<bool, string>(v => v ? "로봇 연결됨" : "로봇 끊김");
    public static readonly IValueConverter NonZero = new FuncValueConverter<int, bool>(n => n > 0);
    public static readonly IValueConverter DeltaX = new FuncValueConverter<HeadCalibrationEntry?, string>(h => h is null ? "" : (h.MeasuredX - h.CurrentX).ToString("+0.00;-0.00"));
    public static readonly IValueConverter DeltaY = new FuncValueConverter<HeadCalibrationEntry?, string>(h => h is null ? "" : (h.MeasuredY - h.CurrentY).ToString("+0.00;-0.00"));

    public static readonly IValueConverter CalSummary = new FuncValueConverter<LaserHeadCalibrationResult?, string>(r =>
        r is null ? "" :
        $"단위 mm(툴 좌표) · 프로브 {r.LevelIterations}회 · 최종 잔여 기울기 {r.FinalTiltDeg:F3}° (Rx={r.BaselineRxDeg:F3}°, Ry={r.BaselineRyDeg:F3}°) · 평균거리 {r.MeanDistanceMm:F1}mm · 삼각형 면적 {r.TriangleAreaMm2:F0}mm²"
        + (r.SignCheckPassed ? " · 부호 검증 통과" : "")
        + (r.XAxisFlipped || r.YAxisFlipped ? $" · ⚠ 축 반전 발생(X:{r.XAxisFlipped}, Y:{r.YAxisFlipped})" : ""));

    public static readonly IValueConverter TcpSummary = new FuncValueConverter<TcpTouchCalibrationResult?, string>(r =>
        r is null ? "" : $"플랜지 좌표 mm · 잔차 RMS {r.RmsMm:F3}mm (최대 {r.MaxAbsMm:F3}mm)");
}
