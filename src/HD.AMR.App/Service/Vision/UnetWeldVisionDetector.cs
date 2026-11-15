using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// U-Net 세그멘테이션(<c>weld_unet_{ir|rgb}.onnx</c>) 또는 중심선 히트맵(<c>weld_heatmap_{ir|rgb}.onnx</c>,
/// <see cref="HeatmapSegmenter"/> — 입력 채널 수로 자동 구분) 기반 용접라인 검출기. Weld ROI 를 그레이로 잘라
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
        // 모델은 반드시 지정(Weld.Unet.ModelPath, 파일명만이면 models 폴더 기준). 빈값 자동 선택은 하지 않는다.
        if (string.IsNullOrWhiteSpace(p.UnetModelPath))
            return WeldDetectionResult.Fail("U-Net 모델 없음 — 용접 추적 화면에서 U-Net 모델을 선택하거나 파라미터 Weld.Unet.ModelPath 를 지정하세요.");
        var modelPath = ResolveExplicitPath(p.UnetModelPath);
        if (modelPath is null || !File.Exists(modelPath))
            return WeldDetectionResult.Fail($"U-Net 모델 파일이 없습니다: {modelPath ?? p.UnetModelPath} — 파라미터 Weld.Unet.ModelPath 를 확인하세요.");

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

            // CLAHE 모델(이름에 _clahe)이면 전체 프레임에 먼저 건 뒤 ROI 를 자른다(타일 격자가 학습과 같아야 함).
            using var claheInput = WeldModelNames.HasClahe(modelPath) ? ImageClaheMat.Apply(bgr) : null;
            using var roiBgr = new Mat(claheInput ?? bgr, new Rect(roi.X, roi.Y, roi.Width, roi.Height));

            byte[] m;
            lock (_gate)
            {
                var session = GetSession(modelPath);
                if (HeatmapSegmenter.LooksLikeHeatmap(session))
                {
                    // 중심선 히트맵(weld_heatmap): 3채널 RGB 입력, 학습 배율로 줄여 추론 → 슬라이스별 최댓값 띠 마스크.
                    using var roiRgb = new Mat();
                    Cv2.CvtColor(roiBgr, roiRgb, ColorConversionCodes.BGR2RGB);
                    var rgb = new byte[roi.Width * roi.Height * 3];
                    System.Runtime.InteropServices.Marshal.Copy(roiRgb.Data, rgb, 0, rgb.Length);
                    m = HeatmapSegmenter.RidgeMask(session, rgb, roi.Width, roi.Height, bgr.Width,
                        p.ProgressAxis == WeldProgressAxis.Horizontal, (float)p.UnetThreshold);
                }
                else
                {
                    using var roiGray = new Mat();
                    Cv2.CvtColor(roiBgr, roiGray, ColorConversionCodes.BGR2GRAY);
                    roiGray.GetArray(out byte[] g);
                    m = UnetSegmenter.Segment(session, g, roi.Width, roi.Height, (float)p.UnetThreshold);
                }
            }

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
        if (!UnetSegmenter.LooksLikeUnet(sess) && !HeatmapSegmenter.LooksLikeHeatmap(sess))
        {
            sess.Dispose();
            _cache = null;
            throw new InvalidOperationException(
                $"U-Net(입력 1채널) 또는 중심선 히트맵(입력 3채널) 형식의 모델이 아닙니다: {Path.GetFileName(modelPath)}");
        }
        _cache = (modelPath, mtime, sess);
        _log.LogInformation("U-Net ONNX 세션 로드: {Path}", modelPath);
        return sess;
    }
}
