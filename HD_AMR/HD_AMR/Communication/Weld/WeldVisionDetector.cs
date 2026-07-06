using HD_AMR.Models;
using OpenCvSharp;

namespace HD_AMR.Communication.Weld;

/// <summary>
/// OpenCvSharp(네이티브 OpenCV) 기반 용접라인 검출기(고전 CV). 명세서 8장 전략을 따른다:
/// 비드를 단일 선으로 찾지 않고, ROI 내부에서 비드 후보 mask 의 양쪽 경계를 스캔해
/// 중점(midpoint)으로 centerline 을 만들고, 기준선(FOV 중심/Peak)과의 위치 오차 d(픽셀)를 계산한다.
/// 이 클래스는 <b>마스크 생성</b>(이진화·모폴로지)만 담당하고, 마스크→중심선·d·오버레이 산출은
/// <see cref="WeldMaskAnalyzer"/> 공유 로직에 위임한다(DL 검출기와 결과·오버레이 일관성 유지).
/// </summary>
public sealed class WeldVisionDetector : IWeldVisionDetector
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

            // 1) 대비강화
            if (p.UseClahe)
            {
                using var clahe = Cv2.CreateCLAHE(Math.Max(0.1, p.ClaheClip), new Size(8, 8));
                clahe.Apply(roiGray, roiGray);
            }
            // 2) 블러
            if (p.BlurKernel >= 3)
            {
                int k = p.BlurKernel % 2 == 1 ? p.BlurKernel : p.BlurKernel + 1;
                Cv2.GaussianBlur(roiGray, roiGray, new Size(k, k), 0);
            }
            // 3) 이진화/엣지 → mask
            using var mask = new Mat();
            BuildMask(roiGray, mask, p);
            // 4) morphology
            if (p.MorphKernel >= 3)
            {
                int k = p.MorphKernel % 2 == 1 ? p.MorphKernel : p.MorphKernel + 1;
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
                Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            }

            // 5~) 마스크 → 중심선·d·오버레이(공유 분석기).
            mask.GetArray(out byte[] m);
            return WeldMaskAnalyzer.Analyze(bgr, m, roi, p, gray.Width, gray.Height,
                referenceMode, peakReferencePos, peakProgressPos, peakLabel, peakRoi, peakCrossStart, peakCrossEnd);
        }
        catch (Exception ex)
        {
            return WeldDetectionResult.Fail($"검출 오류: {ex.Message}");
        }
        finally
        {
            bgr?.Dispose();
            gray?.Dispose();
        }
    }

    private static void BuildMask(Mat roiGray, Mat mask, WeldDetectionParams p)
    {
        switch (p.Threshold)
        {
            case WeldThresholdMethod.Adaptive:
                int block = p.AdaptiveBlockSize % 2 == 1 ? p.AdaptiveBlockSize : p.AdaptiveBlockSize + 1;
                block = Math.Max(3, block);
                Cv2.AdaptiveThreshold(roiGray, mask, 255, AdaptiveThresholdTypes.GaussianC,
                    p.Invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary, block, p.AdaptiveC);
                break;
            case WeldThresholdMethod.Canny:
                Cv2.Canny(roiGray, mask, p.CannyLow, p.CannyHigh);
                break;
            default: // Otsu
                Cv2.Threshold(roiGray, mask, 0, 255,
                    ThresholdTypes.Otsu | (p.Invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary));
                break;
        }
    }
}
