using System.Globalization;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service;

/// <summary>
/// 용접 추적 검출 방식과 Ridge 검출 파라미터의 DB(Parameter) 영속화 매핑. 값은 화면(Desktop/Web 용접 추적)이
/// 저장하고, <see cref="WeldTrackingService"/> 가 기동·변경 시 읽어 싱글톤 상태에 적용한다 — 시퀀스도 같은 서비스를
/// 쓰므로 저장 즉시(재시작 없이) 반영된다. 순수 매핑만 두어 DB 없이 단위 테스트한다.
/// </summary>
public static class WeldDetectionSettingsStore
{
    public const string MethodKey = "Weld.Method";
    public const string RidgeSigmaMinKey = "Weld.Ridge.SigmaMin";
    public const string RidgeSigmaMaxKey = "Weld.Ridge.SigmaMax";
    public const string RidgeScalesKey = "Weld.Ridge.Scales";
    public const string RidgePolarityKey = "Weld.Ridge.Polarity";
    public const string RidgeMaxAngleDegKey = "Weld.Ridge.MaxAngleDeg";
    public const string RidgeThresholdKKey = "Weld.Ridge.ThresholdK";
    public const string RidgeRelativeKey = "Weld.Ridge.Relative";

    /// <summary>DL 모델 경로(빈값=자동 <c>weld_seg_{ir|rgb}.onnx</c>). 전체 경로 또는 models 폴더 기준 파일명.
    /// Web 모델 드롭다운·Parameter 화면이 쓰고, 여기서는 읽기(복원)만 한다 — <see cref="ToValues"/> 에는 없다.</summary>
    public const string DlModelPathKey = "Weld.Dl.ModelPath";

    /// <summary>U-Net 모델 경로(빈값=자동 <c>weld_unet_{ir|rgb}.onnx</c>). DL 모델 경로와 같은 규칙 — 화면 드롭다운·Parameter 화면이 쓰고
    /// 여기서는 읽기(복원)만 한다.</summary>
    public const string UnetModelPathKey = "Weld.Unet.ModelPath";

    /// <summary>이 저장소가 읽는 모든 키(기동 복원·변경 이벤트 필터용).</summary>
    public static readonly string[] Keys =
    [
        MethodKey, RidgeSigmaMinKey, RidgeSigmaMaxKey, RidgeScalesKey, RidgePolarityKey,
        RidgeMaxAngleDegKey, RidgeThresholdKKey, RidgeRelativeKey, DlModelPathKey, UnetModelPathKey,
    ];

    public static bool IsKey(string name) => Array.IndexOf(Keys, name) >= 0;

    /// <summary>키별 설명(Parameter 화면 표시용).</summary>
    public static string Describe(string key) => key switch
    {
        MethodKey => "비드 검출 방식 (Dl/Param/Ridge/Unet)",
        RidgeSigmaMinKey => "Ridge σ 최소(px) — 비드 반폭 정도",
        RidgeSigmaMaxKey => "Ridge σ 최대(px) — 낮추면 넓은 하이라이트(코로게이션 능선) 응답 감소",
        RidgeScalesKey => "Ridge σ 스케일 개수(1~6)",
        RidgePolarityKey => "Ridge 선 극성 (Both/Bright/Dark)",
        RidgeMaxAngleDegKey => "Ridge 선 방향 허용각(진행축 대비 °)",
        RidgeThresholdKKey => "Ridge 전역 잡음 하한 k (중앙값 + k·MAD)",
        RidgeRelativeKey => "Ridge 슬라이스 상대 임계(0~1)",
        DlModelPathKey => "비드 검출 DL(YOLO) 모델 경로 (빈값=자동 weld_yolo_{ir|rgb}.onnx → weld_seg_{ir|rgb}.onnx, 파일명만 쓰면 models 폴더 기준)",
        UnetModelPathKey => "비드 검출 U-Net 모델 경로 (빈값=자동 weld_unet_{ir|rgb}.onnx, 파일명만 쓰면 models 폴더 기준)",
        _ => key,
    };

    /// <summary>현재 상태 → 저장할 (키, 값) 목록. 숫자는 InvariantCulture.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ToValues(WeldDetectionMethod method, WeldDetectionParams p) =>
    [
        new(MethodKey, method.ToString()),
        new(RidgeSigmaMinKey, F(p.RidgeSigmaMin)),
        new(RidgeSigmaMaxKey, F(p.RidgeSigmaMax)),
        new(RidgeScalesKey, p.RidgeScales.ToString(CultureInfo.InvariantCulture)),
        new(RidgePolarityKey, p.RidgePolarity.ToString()),
        new(RidgeMaxAngleDegKey, F(p.RidgeMaxAngleDeg)),
        new(RidgeThresholdKKey, F(p.RidgeThresholdK)),
        new(RidgeRelativeKey, F(p.RidgeRelative)),
    ];

    /// <summary>
    /// 저장값을 <paramref name="p"/> 에 적용하고 검출 방식을 돌려준다(키 없음/파싱 실패면 null). 없거나 잘못된 값은
    /// 건너뛰어 현재(코드 기본) 값을 유지하고, 범위 밖 값은 유효 범위로 자른다.
    /// </summary>
    public static WeldDetectionMethod? Apply(Func<string, string?> get, WeldDetectionParams p)
    {
        if (D(get(RidgeSigmaMinKey)) is { } smin) p.RidgeSigmaMin = Math.Clamp(smin, 0.5, 20);
        if (D(get(RidgeSigmaMaxKey)) is { } smax) p.RidgeSigmaMax = Math.Clamp(smax, 0.5, 20);
        if (p.RidgeSigmaMax < p.RidgeSigmaMin) p.RidgeSigmaMax = p.RidgeSigmaMin;
        if (int.TryParse(get(RidgeScalesKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            p.RidgeScales = Math.Clamp(n, 1, 6);
        if (Enum.TryParse<WeldRidgePolarity>(get(RidgePolarityKey), true, out var pol) && Enum.IsDefined(pol))
            p.RidgePolarity = pol;
        if (D(get(RidgeMaxAngleDegKey)) is { } ang) p.RidgeMaxAngleDeg = Math.Clamp(ang, 0, 90);
        if (D(get(RidgeThresholdKKey)) is { } k) p.RidgeThresholdK = Math.Clamp(k, 0, 50);
        if (D(get(RidgeRelativeKey)) is { } rel) p.RidgeRelative = Math.Clamp(rel, 0, 1);
        // 키가 있으면(빈 문자열 포함 = 자동) 그대로 적용. 앞뒤 공백·따옴표는 직접 입력 실수로 보고 제거.
        if (get(DlModelPathKey) is { } path) p.DlModelPath = path.Trim().Trim('"').Trim();
        if (get(UnetModelPathKey) is { } upath) p.UnetModelPath = upath.Trim().Trim('"').Trim();

        return Enum.TryParse<WeldDetectionMethod>(get(MethodKey), true, out var m) && Enum.IsDefined(m) ? m : null;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static double? D(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}
