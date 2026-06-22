using System.Runtime.InteropServices;
using HD_AMR.Models;
using OpenCvSharp;

namespace HD_AMR.Communication.Weld;

/// <summary>
/// OpenCvSharp(네이티브 OpenCV) 기반 용접라인 검출기. 명세서 8장 전략을 따른다:
/// 비드를 단일 선으로 찾지 않고, ROI 내부에서 비드 후보 mask 의 양쪽 경계를 스캔해
/// 중점(midpoint)으로 centerline 을 만들고, 기준선(FOV 중심/Peak)과의 위치 오차 d(픽셀)를 계산한다.
/// 결과 overlay(JPEG)도 함께 생성해 UI 가 그대로 표시할 수 있게 한다.
/// </summary>
public sealed class WeldVisionDetector : IWeldVisionDetector
{
    public bool IsAvailable => true;

    public WeldDetectionResult DetectWeld(
        CameraFrame frame, RoiRect weldRoi, WeldDetectionParams p,
        WeldReferenceMode referenceMode = WeldReferenceMode.FovCenter, double? peakReferencePos = null,
        double? peakProgressPos = null, string? peakLabel = null)
    {
        Mat? bgr = null, gray = null;
        try
        {
            (bgr, gray) = Decode(frame, p.Mode);
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

            // 5) 경계 스캔 → centerline
            mask.GetArray(out byte[] m);
            bool horiz = p.ProgressAxis == WeldProgressAxis.Horizontal;
            int sCount = horiz ? roi.Width : roi.Height;
            int crossCount = horiz ? roi.Height : roi.Width;
            var centerCross = new double[sCount];
            var valid = new bool[sCount];
            int validCount = 0;

            for (int s = 0; s < sCount; s++)
            {
                int first = -1, last = -1;
                for (int c = 0; c < crossCount; c++)
                {
                    int x = horiz ? s : c;
                    int y = horiz ? c : s;
                    if (m[y * roi.Width + x] > 0) { if (first < 0) first = c; last = c; }
                }
                if (first >= 0)
                {
                    centerCross[s] = (first + last) / 2.0;
                    valid[s] = true;
                    validCount++;
                }
            }

            double coverage = sCount > 0 ? (double)validCount / sCount : 0;
            if (validCount < 3 || coverage < 0.15)
                return new WeldDetectionResult
                {
                    Success = false,
                    Message = $"비드 후보 부족(coverage={coverage:P0}) — ROI/파라미터를 조정하세요.",
                    OverlayJpeg = EncodeOverlay(bgr, roi, p, null, double.NaN, double.NaN, peakProgressPos, peakLabel),
                };

            // 6) 이동평균 smoothing
            if (p.SmoothingWindow >= 2)
                SmoothValid(centerCross, valid, p.SmoothingWindow);

            // 7) 기준선/중심/d 계산 (cross 좌표는 full-image 기준으로 변환)
            double crossOffset = horiz ? roi.Y : roi.X;
            // 기준선: Peak 모드면 Peak 중심선, 아니면 FOV(전체 화면) 가로/세로 센터선(회색 점선과 동일).
            double refPos = referenceMode == WeldReferenceMode.PeakLine && peakReferencePos is { } pr
                ? pr
                : (horiz ? gray.Height : gray.Width) / 2.0;

            // 비드 중심 = 유효 centerline 전체의 평균(mean). 직선 비드에 강건(끝점 튐에 둔감).
            double sumC = 0; int nC = 0;
            for (int s = 0; s < sCount; s++) if (valid[s]) { sumC += centerCross[s]; nC++; }
            double targetS = sCount / 2.0; // 깊이 샘플링용 진행축 기준(ROI 중앙)
            double weldCenterRoi = nC > 0 ? sumC / nC : SampleAround(centerCross, valid, (int)targetS, 5);
            double weldCenterFull = crossOffset + weldCenterRoi;
            double d = weldCenterFull - refPos;

            // centerline 점들(full-image)
            var pts = new List<PixelPoint>(validCount);
            for (int s = 0; s < sCount; s++)
            {
                if (!valid[s]) continue;
                double x = horiz ? roi.X + s : roi.X + centerCross[s];
                double y = horiz ? roi.Y + centerCross[s] : roi.Y + s;
                pts.Add(new PixelPoint(x, y));
            }

            var overlay = EncodeOverlay(bgr, roi, p, pts, refPos, weldCenterFull, peakProgressPos, peakLabel);

            // 타깃 지점의 비드/기준 픽셀 좌표(full-image) — 깊이 샘플링·스케일 환산용.
            double targetProgFull = (horiz ? roi.X : roi.Y) + targetS;
            var weldPt = horiz ? new PixelPoint(targetProgFull, weldCenterFull) : new PixelPoint(weldCenterFull, targetProgFull);
            var refPt = horiz ? new PixelPoint(targetProgFull, refPos) : new PixelPoint(refPos, targetProgFull);

            return new WeldDetectionResult
            {
                Success = true,
                Confidence = Math.Clamp(coverage, 0, 1),
                Centerline = pts,
                ReferencePos = refPos,
                WeldCenterAtTarget = weldCenterFull,
                DPixel = d,
                WeldPoint = weldPt,
                RefPoint = refPt,
                OverlayJpeg = overlay,
            };
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

    /// <summary>유효 구간에 대해 이동평균. 결측 구간은 건너뛰고 주변 유효값만 평균.</summary>
    private static void SmoothValid(double[] v, bool[] valid, int window)
    {
        var src = (double[])v.Clone();
        int half = Math.Max(1, window / 2);
        for (int i = 0; i < v.Length; i++)
        {
            if (!valid[i]) continue;
            double sum = 0; int n = 0;
            for (int j = i - half; j <= i + half; j++)
                if (j >= 0 && j < v.Length && valid[j]) { sum += src[j]; n++; }
            if (n > 0) v[i] = sum / n;
        }
    }

    /// <summary>인덱스 주변 ±r 의 유효값 평균(타깃 지점 노이즈 완화). 없으면 가장 가까운 유효값.</summary>
    private static double SampleAround(double[] v, bool[] valid, int idx, int r)
    {
        double sum = 0; int n = 0;
        for (int j = idx - r; j <= idx + r; j++)
            if (j >= 0 && j < v.Length && valid[j]) { sum += v[j]; n++; }
        if (n > 0) return sum / n;
        // fallback: 가장 가까운 유효값
        for (int off = 0; off < v.Length; off++)
        {
            int a = idx - off, b = idx + off;
            if (a >= 0 && valid[a]) return v[a];
            if (b < v.Length && valid[b]) return v[b];
        }
        return idx;
    }

    private static byte[]? EncodeOverlay(
        Mat bgr, RoiRect roi, WeldDetectionParams p,
        IReadOnlyList<PixelPoint>? centerline, double refPos, double weldCenterFull,
        double? peakProgressPos = null, string? peakLabel = null)
    {
        try
        {
            using var canvas = bgr.Clone();
            bool horiz = p.ProgressAxis == WeldProgressAxis.Horizontal;

            // FOV x,y 센터선(회색 점선) — 화면 기하중심
            DrawCenterCross(canvas);

            // ROI(노랑)
            Cv2.Rectangle(canvas, new Rect(roi.X, roi.Y, roi.Width, roi.Height), new Scalar(0, 255, 255), 1);

            // 기준선(하늘색) — FOV 센터선(회색)과 겹치면(=FOV 기준) 생략해 이중선 방지.
            double fovCenter = (horiz ? canvas.Height : canvas.Width) / 2.0;
            if (!double.IsNaN(refPos) && Math.Abs(refPos - fovCenter) > 1.0)
            {
                if (horiz) Cv2.Line(canvas, new Point(roi.X, (int)refPos), new Point(roi.Right, (int)refPos), new Scalar(255, 255, 0), 1);
                else Cv2.Line(canvas, new Point((int)refPos, roi.Y), new Point((int)refPos, roi.Bottom), new Scalar(255, 255, 0), 1);
            }

            // centerline(초록)
            if (centerline is { Count: > 1 })
            {
                var poly = new List<Point>(centerline.Count);
                foreach (var pt in centerline) poly.Add(new Point((int)pt.X, (int)pt.Y));
                Cv2.Polylines(canvas, new[] { poly }, false, new Scalar(0, 230, 0), 2);
            }

            // 타깃 비드 중심점(빨강) + d 텍스트
            if (!double.IsNaN(weldCenterFull) && !double.IsNaN(refPos))
            {
                int tx = horiz ? roi.X + roi.Width / 2 : (int)weldCenterFull;
                int ty = horiz ? (int)weldCenterFull : roi.Y + roi.Height / 2;
                Cv2.Circle(canvas, new Point(tx, ty), 4, new Scalar(0, 0, 255), -1);
                double d = weldCenterFull - refPos;
                Cv2.PutText(canvas, $"d={d:0.0}px", new Point(roi.X, Math.Max(12, roi.Y - 6)),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 1);
            }

            // Peak 위치(자홍색 진행축-수직 선 + 라벨 P1/P2)
            if (peakProgressPos is { } pp && !double.IsNaN(pp))
            {
                var magenta = new Scalar(255, 0, 255);
                if (horiz)
                {
                    int x = (int)pp;
                    Cv2.Line(canvas, new Point(x, roi.Y), new Point(x, roi.Bottom), magenta, 1);
                    if (!string.IsNullOrEmpty(peakLabel))
                        Cv2.PutText(canvas, peakLabel, new Point(x + 3, Math.Max(12, roi.Y + 14)),
                            HersheyFonts.HersheySimplex, 0.5, magenta, 1);
                }
                else
                {
                    int y = (int)pp;
                    Cv2.Line(canvas, new Point(roi.X, y), new Point(roi.Right, y), magenta, 1);
                    if (!string.IsNullOrEmpty(peakLabel))
                        Cv2.PutText(canvas, peakLabel, new Point(roi.X + 3, Math.Max(12, y - 4)),
                            HersheyFonts.HersheySimplex, 0.5, magenta, 1);
                }
            }

            Cv2.ImEncode(".jpg", canvas, out byte[] buf, new[] { (int)ImwriteFlags.JpegQuality, 80 });
            return buf;
        }
        catch { return null; }
    }

    /// <summary>FOV 기하중심을 지나는 세로/가로 센터선(회색 점선)을 그린다.</summary>
    private static void DrawCenterCross(Mat canvas)
    {
        int cx = canvas.Width / 2;
        int cy = canvas.Height / 2;
        var color = new Scalar(170, 170, 170);
        DrawDashedLine(canvas, new Point(cx, 0), new Point(cx, canvas.Height), color);
        DrawDashedLine(canvas, new Point(0, cy), new Point(canvas.Width, cy), color);
    }

    /// <summary>점선 그리기(OpenCV 기본 미지원). dash/gap 픽셀 길이로 분할해 그린다.</summary>
    private static void DrawDashedLine(Mat img, Point a, Point b, Scalar color, int dash = 8, int gap = 6)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        double ux = dx / len, uy = dy / len;
        for (double t = 0; t < len; t += dash + gap)
        {
            var p1 = new Point((int)(a.X + ux * t), (int)(a.Y + uy * t));
            double t2 = Math.Min(t + dash, len);
            var p2 = new Point((int)(a.X + ux * t2), (int)(a.Y + uy * t2));
            Cv2.Line(img, p1, p2, color, 1);
        }
    }

    /// <summary>CameraFrame → (BGR overlay 용, Gray 작업용) Mat. 호출자가 Dispose.</summary>
    private static (Mat? bgr, Mat? gray) Decode(CameraFrame f, WeldImageMode mode)
    {
        switch (f.PixelFormat)
        {
            case "mjpg":
            {
                var bgr = Cv2.ImDecode(f.Pixels, ImreadModes.Color);
                if (bgr.Empty()) return (null, null);
                return (bgr, ToWorkGray(bgr, mode));
            }
            case "rgb24":
            {
                using var rgb = WrapBytes(f.Height, f.Width, MatType.CV_8UC3, f.Pixels);
                var bgr = new Mat();
                Cv2.CvtColor(rgb, bgr, ColorConversionCodes.RGB2BGR);
                return (bgr, ToWorkGray(bgr, mode));
            }
            case "ir8":
            {
                var gray = WrapBytes(f.Height, f.Width, MatType.CV_8UC1, f.Pixels);
                var bgr = new Mat();
                Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
                return (bgr, gray);
            }
            case "ir16":
            {
                using var m16 = WrapBytes(f.Height, f.Width, MatType.CV_16UC1, f.Pixels);
                var gray = new Mat();
                Cv2.Normalize(m16, gray, 0, 255, NormTypes.MinMax, (int)MatType.CV_8UC1);
                var bgr = new Mat();
                Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
                return (bgr, gray);
            }
            default:
                return (null, null);
        }
    }

    /// <summary>원시 픽셀 바이트를 Mat 으로 래핑(새 버퍼에 복사). 버전 간 안전한 방식.</summary>
    private static Mat WrapBytes(int rows, int cols, MatType type, byte[] data)
    {
        var mat = new Mat(rows, cols, type);
        long bytes = (long)mat.Total() * mat.ElemSize();
        int n = (int)Math.Min(bytes, data.Length);
        Marshal.Copy(data, 0, mat.Data, n);
        return mat;
    }

    private static Mat ToWorkGray(Mat bgr, WeldImageMode mode)
    {
        var gray = new Mat();
        if (mode == WeldImageMode.RgbHsv)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            var ch = Cv2.Split(hsv);
            try { ch[1].CopyTo(gray); }   // 채도(S) 채널 — 변색된 비드 분리에 유리
            finally { foreach (var c in ch) c.Dispose(); }
        }
        else
        {
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        }
        return gray;
    }
}
