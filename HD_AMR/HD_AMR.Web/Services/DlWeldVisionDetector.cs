using HD_AMR.Communication.Weld;
using HD_AMR.Models;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace HD_AMR.Web.Services;

/// <summary>
/// 학습된 YOLOv8-seg(.onnx)로 비드 마스크를 추론하고, 그 마스크를 <see cref="WeldMaskAnalyzer"/> 공유
/// 로직에 넘겨 고전 CV 와 <b>동일한</b> 중심선·위치오차 d·오버레이를 만드는 DL 검출기. 모델은 입력
/// 모달리티(IR/RGB)에 맞춰 <c>weld_seg_{ir|rgb}.onnx</c> 를 자동 선택한다(옵션 A). InferenceSession 은
/// 경로+수정시각으로 캐시하며 단일 운영자 전제로 추론을 직렬화한다.
/// </summary>
public sealed class DlWeldVisionDetector : IDlWeldVisionDetector
{
    private readonly WeldTrainingService _train;
    private readonly ILogger<DlWeldVisionDetector> _log;
    private readonly object _gate = new();
    private (string Path, DateTime Mtime, InferenceSession Session)? _cache;

    public DlWeldVisionDetector(WeldTrainingService train, ILogger<DlWeldVisionDetector> log)
    {
        _train = train;
        _log = log;
    }

    // ONNX 런타임 + OpenCvSharp 네이티브가 존재하는 플랫폼(Windows)에서만 등록되므로 항상 사용가능.
    // 실제 모델 파일 존재 여부는 호출 시점에 검사해 명확한 메시지로 안내한다.
    public bool IsAvailable => true;

    public WeldDetectionResult DetectWeld(
        CameraFrame frame, RoiRect weldRoi, WeldDetectionParams p,
        WeldReferenceMode referenceMode = WeldReferenceMode.FovCenter, double? peakReferencePos = null,
        double? peakProgressPos = null, string? peakLabel = null, RoiRect? peakRoi = null,
        double? peakCrossStart = null, double? peakCrossEnd = null)
    {
        var modality = p.Mode == WeldImageMode.Ir ? "ir" : "rgb";
        // 명시 경로가 있으면 그걸, 없으면(빈 문자열 포함) 모달리티로 자동 해석.
        var modelPath = string.IsNullOrWhiteSpace(p.DlModelPath) ? ResolveModelPath(modality) : p.DlModelPath;
        if (modelPath is null || !File.Exists(modelPath))
            return WeldDetectionResult.Fail(
                $"DL 모델이 없습니다: weld_seg_{modality}.onnx — 비전 학습에서 ② 학습 → ③ 내보내기({modality})를 먼저 완료하세요.");

        Mat? bgr = null, gray = null;
        try
        {
            (bgr, gray) = WeldFrameDecoder.Decode(frame, p.Mode);
            gray?.Dispose();
            if (bgr is null || bgr.Empty())
                return WeldDetectionResult.Fail("프레임 디코드 실패");

            var roi = weldRoi.ClampTo(bgr.Width, bgr.Height);
            if (roi is null)
                return WeldDetectionResult.Fail("Weld ROI 가 프레임 범위를 벗어났습니다.");

            lock (_gate)
            {
                var session = GetSession(modelPath);
                var (mask, count, _) = YoloSeg.Decode(session, bgr, (float)p.DlConfidence, 0.5f, (float)p.DlMaskThreshold);
                using (mask)
                {
                    if (count == 0)
                        return new WeldDetectionResult
                        {
                            Success = false,
                            Message = $"DL 검출 0건 — conf({p.DlConfidence:0.00})/mask({p.DlMaskThreshold:0.00}) 조정 또는 추가 학습 필요.",
                            OverlayJpeg = EncodeBareOverlay(bgr, roi),
                        };

                    // ROI 영역 마스크만 잘라 공유 분석기로 전달(전체→ROI-local).
                    using var roiMask = new Mat(mask, new Rect(roi.X, roi.Y, roi.Width, roi.Height)).Clone();
                    roiMask.GetArray(out byte[] m);
                    return WeldMaskAnalyzer.Analyze(bgr, m, roi, p, bgr.Width, bgr.Height,
                        referenceMode, peakReferencePos, peakProgressPos, peakLabel, peakRoi, peakCrossStart, peakCrossEnd);
                }
            }
        }
        catch (Exception ex)
        {
            return WeldDetectionResult.Fail($"DL 검출 오류: {ex.Message}");
        }
        finally
        {
            bgr?.Dispose();
        }
    }

    /// <summary>모달리티별 모델 경로. <c>weld_seg_{modality}.onnx</c> → 없으면 레거시 <c>weld_seg.onnx</c> 폴백.</summary>
    private string? ResolveModelPath(string modality)
    {
        var paths = _train.GetPathsAsync().GetAwaiter().GetResult();
        if (paths is null) return null;
        var byModality = Path.Combine(paths.Models, $"weld_seg_{modality}.onnx");
        if (File.Exists(byModality)) return byModality;
        var legacy = Path.Combine(paths.Models, "weld_seg.onnx");
        return File.Exists(legacy) ? legacy : byModality; // 없으면 기대 경로를 반환(호출부에서 존재검사)
    }

    private InferenceSession GetSession(string modelPath)
    {
        var mtime = File.GetLastWriteTimeUtc(modelPath);
        if (_cache is { } c && c.Path == modelPath && c.Mtime == mtime) return c.Session;
        _cache?.Session.Dispose();
        var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        var sess = new InferenceSession(modelPath, opts);
        _cache = (modelPath, mtime, sess);
        _log.LogInformation("DL 검출 ONNX 세션 로드: {Path}", modelPath);
        return sess;
    }

    // 검출 0건일 때도 ROI/센터선이 보이는 최소 오버레이(사용자가 무엇이 비었는지 확인).
    private static byte[]? EncodeBareOverlay(Mat bgr, RoiRect roi)
    {
        try
        {
            using var canvas = bgr.Clone();
            Cv2.Rectangle(canvas, new Rect(roi.X, roi.Y, roi.Width, roi.Height), new Scalar(0, 255, 255), 1);
            Cv2.ImEncode(".jpg", canvas, out byte[] buf, new[] { (int)ImwriteFlags.JpegQuality, 80 });
            return buf;
        }
        catch { return null; }
    }
}
