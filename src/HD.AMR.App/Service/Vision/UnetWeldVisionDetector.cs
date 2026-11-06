using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// U-Net 세그멘테이션(<c>weld_unet_{ir|rgb}.onnx</c>) 기반 용접라인 검출기. Weld ROI 를 그레이로 잘라
/// <see cref="UnetSegmenter"/> 로 픽셀별 비드 마스크를 얻고, 공유 분석기(<see cref="WeldMaskAnalyzer"/>, RANSAC)로
/// 중심선·d·오버레이를 만든다. 학습 PNG 와 같은 해석을 위해 그레이는 항상 BGR→GRAY 로 만든다(HSV 모드 무관).
/// 세션은 경로+수정시각으로 캐시하고 추론을 직렬화한다(단일 운영자 전제).
/// </summary>
public sealed class UnetWeldVisionDetector : IUnetWeldVisionDetector
{
    private readonly WeldTrainingService _train;
    private readonly ILogger<UnetWeldVisionDetector> _log;
    private readonly object _gate = new();
    private (string Path, DateTime Mtime, InferenceSession Session)? _cache;

    public UnetWeldVisionDetector(WeldTrainingService train, ILogger<UnetWeldVisionDetector> log)
    {
        _train = train;
        _log = log;
    }

    public bool IsAvailable => true;

    public WeldDetectionResult DetectWeld(
        CameraFrame frame, RoiRect weldRoi, WeldDetectionParams p,
        WeldReferenceMode referenceMode = WeldReferenceMode.FovCenter, double? peakReferencePos = null,
        double? peakProgressPos = null, string? peakLabel = null, RoiRect? peakRoi = null,
        double? peakCrossStart = null, double? peakCrossEnd = null)
    {
        var modality = p.Mode == WeldImageMode.Ir ? "ir" : "rgb";
        // 명시 경로(Weld.Unet.ModelPath)가 있으면 그걸(파일명만이면 models 폴더 기준), 없으면 모달리티로 자동 해석.
        bool explicitPath = !string.IsNullOrWhiteSpace(p.UnetModelPath);
        var modelPath = explicitPath ? ResolveExplicitPath(p.UnetModelPath!) : ResolveModelPath(modality);
        if (modelPath is null || !File.Exists(modelPath))
            return WeldDetectionResult.Fail(explicitPath
                ? $"U-Net 모델 파일이 없습니다: {modelPath ?? p.UnetModelPath} — 저장된 모델 경로(Weld.Unet.ModelPath)를 확인하세요(빈값=자동)."
                : "U-Net 모델이 지정되지 않았습니다 — 용접 추적에서 U-Net 모델을 선택하거나 파라미터 Weld.Unet.ModelPath 에 파일명을 입력하세요.");

        Mat? bgr = null, gray = null;
        try
        {
            (bgr, gray) = WeldFrameDecoder.Decode(frame, p.Mode);
            gray?.Dispose();
            gray = null;
            if (bgr is null || bgr.Empty())
                return WeldDetectionResult.Fail("프레임 디코드 실패");

            var roi = weldRoi.ClampTo(bgr.Width, bgr.Height);
            if (roi is null)
                return WeldDetectionResult.Fail("Weld ROI 가 프레임 범위를 벗어났습니다.");

            using var roiBgr = new Mat(bgr, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
            using var roiGray = new Mat();
            Cv2.CvtColor(roiBgr, roiGray, ColorConversionCodes.BGR2GRAY);
            roiGray.GetArray(out byte[] g);

            byte[] m;
            lock (_gate)
                m = UnetSegmenter.Segment(GetSession(modelPath), g, roi.Width, roi.Height, (float)p.UnetThreshold);

            return WeldMaskAnalyzer.Analyze(bgr, m, roi, p, bgr.Width, bgr.Height,
                referenceMode, peakReferencePos, peakProgressPos, peakLabel, peakRoi, peakCrossStart, peakCrossEnd);
        }
        catch (Exception ex)
        {
            return WeldDetectionResult.Fail($"U-Net 검출 오류: {ex.Message}");
        }
        finally
        {
            bgr?.Dispose();
        }
    }

    private string? ResolveModelPath(string modality)
    {
        var paths = _train.GetPathsAsync().GetAwaiter().GetResult();
        return paths is null ? null : Path.Combine(paths.Models, WeldModelNames.UnetExportName(modality));
    }

    /// <summary>명시 경로 해석 — 절대경로면 그대로, 파일명/상대경로면 학습 models 폴더 기준. 앞뒤 공백·따옴표 제거.</summary>
    private string? ResolveExplicitPath(string raw)
    {
        var path = raw.Trim().Trim('"').Trim();
        if (Path.IsPathFullyQualified(path)) return path;
        var paths = _train.GetPathsAsync().GetAwaiter().GetResult();
        return paths is null ? path : Path.Combine(paths.Models, path);
    }

    private InferenceSession GetSession(string modelPath)
    {
        var mtime = File.GetLastWriteTimeUtc(modelPath);
        if (_cache is { } c && c.Path == modelPath && c.Mtime == mtime) return c.Session;
        _cache?.Session.Dispose();
        var sess = new InferenceSession(modelPath, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        if (!UnetSegmenter.LooksLikeUnet(sess))
        {
            sess.Dispose();
            _cache = null;
            throw new InvalidOperationException($"U-Net 형식(입력 1채널, 출력 1개)의 모델이 아닙니다: {Path.GetFileName(modelPath)}");
        }
        _cache = (modelPath, mtime, sess);
        _log.LogInformation("U-Net ONNX 세션 로드: {Path}", modelPath);
        return sess;
    }
}
