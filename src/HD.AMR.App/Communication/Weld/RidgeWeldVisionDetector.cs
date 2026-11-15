using HD.AMR.App.Models;
using OpenCvSharp;

namespace HD.AMR.App.Communication.Weld;

/// <summary>
/// 헤시안 ridge 필터(<see cref="RidgeFilter"/>) 기반 용접라인 검출기 — <b>학습 데이터 없이</b> 가는 선을 찾는다.
/// 이진화 대신 "가로지르는 방향으로만 곡률이 큰 픽셀"을 비드 후보로 마스크화하므로, 대비가 낮은 가는 비드와
/// 도트·점 반사가 섞인 IR 에서 고전 이진화(Param)보다 유리하다. 진행축과 다른 방향의 선(코로게이션 모서리)은
/// 방향 제한으로 걸러낸다. 마스크 → 중심선·d·오버레이는 공유 분석기(<see cref="WeldMaskAnalyzer"/>, RANSAC)에 위임.
/// </summary>
public sealed class RidgeWeldVisionDetector : IRidgeWeldVisionDetector
{
    public bool IsAvailable => true;

    public WeldDetectionResult DetectWeld(
        CameraFrame frame, RoiRect weldRoi, WeldDetectionParams p,
        WeldReferenceMode referenceMode = WeldReferenceMode.FovCenter, double? peakReferencePos = null,
        double? peakProgressPos = null, string? peakLabel = null, RoiRect? peakRoi = null,
        double? peakCrossStart = null, double? peakCrossEnd = null)
    {
        Mat? bgr = null, gray = null;
        try
        {
            (bgr, gray) = WeldFrameDecoder.Decode(frame, p.Mode);
            if (bgr is null || gray is null || gray.Empty())
                return WeldDetectionResult.Fail("프레임 디코드 실패");

            var roi = weldRoi.ClampTo(gray.Width, gray.Height);
            if (roi is null)
                return WeldDetectionResult.Fail("Weld ROI 가 프레임 범위를 벗어났습니다.");

            using var roiGray = new Mat(gray, new Rect(roi.X, roi.Y, roi.Width, roi.Height)).Clone();
            roiGray.GetArray(out byte[] g);

            bool horiz = p.ProgressAxis == WeldProgressAxis.Horizontal;
            var scales = RidgeFilter.Scales(p.RidgeSigmaMin, p.RidgeSigmaMax, p.RidgeScales);
            var resp = RidgeFilter.Response(g, roi.Width, roi.Height, scales, p.RidgePolarity, horiz, p.RidgeMaxAngleDeg);
            var m = RidgeFilter.Threshold(resp, roi.Width, roi.Height, horiz, p.RidgeThresholdK, p.RidgeRelative);

            return WeldMaskAnalyzer.Analyze(bgr, m, roi, p, gray.Width, gray.Height,
                referenceMode, peakReferencePos, peakProgressPos, peakLabel, peakRoi, peakCrossStart, peakCrossEnd);
        }
        catch (Exception ex)
        {
            return WeldDetectionResult.Fail($"Ridge 검출 오류: {ex.Message}");
        }
        finally
        {
            bgr?.Dispose();
            gray?.Dispose();
        }
    }
}
