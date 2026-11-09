using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 비드 검출 모델(.onnx) 파일명 규칙. 내보내기는 <c>weld_yolo_{ir|rgb}.onnx</c>(YOLO) / <c>weld_unet_{ir|rgb}.onnx</c>(U-Net)로
/// 만들고, 사용자는 파일명에 날짜·특징을 덧붙여(예: <c>[4점제외]weld_yolo_ir_261106_밝기증강on.onnx</c>) 여러 버전을 보관한다.
/// 종류 구분은 이름 <b>어디든</b> 들어 있는 표식으로 한다. 기존 YOLO 이름 <c>weld_seg</c> 도 계속 YOLO 로 인식한다.
/// </summary>
public static class WeldModelNames
{
    public const string YoloTag = "weld_yolo";
    public const string LegacyYoloTag = "weld_seg";   // 이전 YOLO 내보내기 이름 — 기존 파일 호환
    public const string UnetTag = "weld_unet";
    /// <summary>중심선 히트맵 모델(weld_heatmap.py) — 검출 방식은 U-Net 과 같은 칸을 쓴다(입력 채널로 자동 구분).</summary>
    public const string HeatmapTag = "weld_heatmap";

    /// <summary>YOLO 내보내기 파일명.</summary>
    public static string YoloExportName(string modality) => $"{YoloTag}_{modality}.onnx";

    /// <summary>U-Net 내보내기 파일명.</summary>
    public static string UnetExportName(string modality) => $"{UnetTag}_{modality}.onnx";

    /// <summary>중심선 히트맵 내보내기 파일명.</summary>
    public static string HeatmapExportName(string modality) => $"{HeatmapTag}_{modality}.onnx";

    /// <summary>'자동' 선택 시 YOLO 후보(우선순위): weld_yolo_{m} → weld_seg_{m} → weld_seg.onnx(레거시).</summary>
    public static IReadOnlyList<string> YoloAutoCandidates(string modality) =>
        [YoloExportName(modality), $"{LegacyYoloTag}_{modality}.onnx", $"{LegacyYoloTag}.onnx"];

    /// <summary>U-Net 칸 모델 파일인지(이름 어디든 weld_unet 또는 weld_heatmap).</summary>
    public static bool IsUnet(string path) => Contains(path, UnetTag) || Contains(path, HeatmapTag);

    /// <summary>중심선 히트맵 모델 파일인지(이름 어디든 weld_heatmap). U-Net 칸 모델의 한 종류.</summary>
    public static bool IsHeatmap(string path) => Contains(path, HeatmapTag);

    /// <summary>모델 드롭다운에 보일 이름 — 히트맵은 U-Net 칸에 섞여 나오므로 표식을 붙여 구분한다.</summary>
    public static string DisplayLabel(string path)
        => IsHeatmap(path) ? $"🔥 {Path.GetFileName(path)} (히트맵)" : Path.GetFileName(path);

    /// <summary>
    /// 방식별 모델 목록이 비었을 때의 안내 문구. 목록은 학습 models 폴더의 .onnx 중 파일명 표식으로 거르므로,
    /// 파일이 그 폴더에 없거나(다른 PC에서 학습·내보내기만 하고 복사 안 함) 이름에 표식이 없으면 비어 보인다.
    /// </summary>
    public static string EmptyListHint(WeldDetectionMethod method, string? modelsDir)
    {
        var where = string.IsNullOrWhiteSpace(modelsDir) ? "학습 models 폴더(캡처 폴더 미설정)" : modelsDir;
        return method == WeldDetectionMethod.Unet
            ? $"U-Net·히트맵 모델이 없습니다 — {where} 에 이름에 '{UnetTag}' 또는 '{HeatmapTag}' 가 들어간 .onnx 를 넣고 ⟳ 를 누르세요."
            : $"YOLO 모델이 없습니다 — {where} 에 이름에 '{YoloTag}' 또는 '{LegacyYoloTag}' 가 들어간 .onnx 를 넣고 ⟳ 를 누르세요. (히트맵 모델은 'U-Net·히트맵' 방식에서 고릅니다)";
    }

    /// <summary>
    /// YOLO 모델 파일인지 — 이름 어디든 weld_yolo/weld_seg (U-Net 표식이 있으면 제외). 학습 run 의 YOLO 산출물
    /// <c>runs/hd/weights/best.onnx</c> 도 YOLO 로 본다.
    /// </summary>
    public static bool IsYolo(string path)
    {
        if (IsUnet(path)) return false;
        if (Contains(path, YoloTag) || Contains(path, LegacyYoloTag)) return true;
        var norm = path.Replace('\\', '/');
        return norm.EndsWith("/runs/hd/weights/best.onnx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 파일명에서 모델 종류. weld_unet → U-Net, weld_yolo/weld_seg → DL(YOLO). 표식이 없으면 <paramref name="implied"/>
    /// (어느 파라미터에 들어 있었는지로 추정).
    /// </summary>
    public static WeldDetectionMethod KindOf(string path, WeldDetectionMethod implied)
        => IsUnet(path) ? WeldDetectionMethod.Unet : IsYolo(path) ? WeldDetectionMethod.Dl : implied;

    /// <summary>파일명에서 모달리티("ir"/"rgb"). 영문자에 붙지 않은 ir·rgb 토큰만 인정(예: _ir_, _rgb.). 없으면 null.</summary>
    public static string? ModalityOf(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(path),
            "(?<![a-z])(ir|rgb)(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>모달리티에 맞는 입력 모드. RGB 는 현재가 RGB HSV 면 유지, 아니면 RGB Grayscale.</summary>
    public static WeldImageMode ModeFor(string modality, WeldImageMode current)
        => modality == "ir" ? WeldImageMode.Ir
            : current == WeldImageMode.RgbHsv ? WeldImageMode.RgbHsv : WeldImageMode.RgbGrayscale;

    private static bool Contains(string path, string tag)
        => Path.GetFileName(path).Contains(tag, StringComparison.OrdinalIgnoreCase);
}
