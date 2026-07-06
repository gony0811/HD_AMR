using HD_AMR.Models;
using OpenCvSharp;

namespace HD_AMR.Communication.Weld;

/// <summary>
/// 이진 비드 마스크(ROI-local)로부터 중심선·위치오차 d·비드 span·오버레이(JPEG)를 만드는 공유 분석기.
/// "마스크를 어떻게 얻느냐"(고전 CV 이진화 vs DL 세그멘테이션)와 무관하게, 마스크만 주어지면
/// 명세서 8장의 경계-중점 방식으로 동일하게 결과를 낸다. 고전(<see cref="WeldVisionDetector"/>)과
/// DL 검출기가 이 클래스를 공유해 결과·오버레이의 일관성을 보장한다.
/// </summary>
public static class WeldMaskAnalyzer
{
    /// <summary>자홍 Peak 틱의 진행축-수직 반길이(px). ROI 크기와 무관한 고정 길이.</summary>
    private const int PeakTickHalf = 40;

    /// <summary>
    /// ROI-local 이진 마스크 <paramref name="m"/>(row-major, roi.Width×roi.Height, 비드=nonzero)을 분석한다.
    /// </summary>
    /// <param name="bgr">전체 프레임 BGR(오버레이 캔버스). Dispose 는 호출자 책임.</param>
    /// <param name="m">ROI-local 마스크 바이트(비드=nonzero, 배경=0).</param>
    /// <param name="roi">전체 프레임 좌표의 Weld ROI(이미 clamp 됨).</param>
    /// <param name="fullW">전체 프레임 폭(기준선/센터 계산).</param>
    /// <param name="fullH">전체 프레임 높이.</param>
    public static WeldDetectionResult Analyze(
        Mat bgr, byte[] m, RoiRect roi, WeldDetectionParams p, int fullW, int fullH,
        WeldReferenceMode referenceMode = WeldReferenceMode.FovCenter, double? peakReferencePos = null,
        double? peakProgressPos = null, string? peakLabel = null, RoiRect? peakRoi = null,
        double? peakCrossStart = null, double? peakCrossEnd = null)
    {
        // 경계 스캔 → centerline
        bool horiz = p.ProgressAxis == WeldProgressAxis.Horizontal;
        int sCount = horiz ? roi.Width : roi.Height;
        int crossCount = horiz ? roi.Height : roi.Width;
        var centerCross = new double[sCount];
        var runStart = new int[sCount];   // 슬라이스 비드 run 시작(ROI cross 좌표)
        var runLen = new int[sCount];     // 그 run 길이(=비드 폭). 라벨 초안 마스크용.
        var valid = new bool[sCount];
        int validCount = 0;

        for (int s = 0; s < sCount; s++)
        {
            // 열(또는 행)에서 마스크가 '연속으로 가장 긴 구간'(=비드 본체)을 찾고 그 중점을 쓴다.
            // 첫~끝 span 방식과 달리, 위/아래로 떨어진 반사 점 하나에 중점이 끌려가지 않는다.
            int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
            for (int c = 0; c < crossCount; c++)
            {
                int x = horiz ? s : c;
                int y = horiz ? c : s;
                if (m[y * roi.Width + x] > 0)
                {
                    if (curStart < 0) { curStart = c; curLen = 0; }
                    curLen++;
                    if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
                }
                else { curStart = -1; curLen = 0; }
            }
            if (bestStart >= 0)
            {
                centerCross[s] = bestStart + (bestLen - 1) / 2.0;
                runStart[s] = bestStart;
                runLen[s] = bestLen;
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
                OverlayJpeg = EncodeOverlay(bgr, roi, p, null, double.NaN, double.NaN, double.NaN, peakProgressPos, peakLabel, peakRoi, peakCrossStart, peakCrossEnd),
            };

        // 이동평균 smoothing
        if (p.SmoothingWindow >= 2)
            SmoothValid(centerCross, valid, p.SmoothingWindow);

        // 기준선(d 의 기준): Peak 모드면 Peak 중심선, 아니면 FOV(전체 화면) 가로/세로 센터선(회색 점선).
        double crossOffset = horiz ? roi.Y : roi.X;
        double refPos = referenceMode == WeldReferenceMode.PeakLine && peakReferencePos is { } pr
            ? pr
            : (horiz ? fullH : fullW) / 2.0;

        // 비드 중심선을 '직선'으로: 유효 (s, cross) 점들에 최소제곱 직선 피팅(cross ≈ a·s + b).
        var (a, b, fitOk) = FitLine(centerCross, valid, sCount);

        // 측정 진행축 위치 = 자홍 Peak 선 위치(캡처 시 전달됨). 없으면 ROI 중앙. full→ROI-local 변환·클램프.
        double targetS = peakProgressPos is { } pp0 && !double.IsNaN(pp0)
            ? pp0 - (horiz ? roi.X : roi.Y)
            : sCount / 2.0;
        targetS = Math.Clamp(targetS, 0, Math.Max(0, sCount - 1));

        // 그 진행 위치에서 직선의 cross 값 = 비드 중심(빨간 점) = Peak 선 ∩ 초록 중심선 교차점.
        // 피팅 실패 시 median/주변평균으로 폴백.
        double weldCenterRoi;
        if (fitOk)
        {
            weldCenterRoi = a * targetS + b;
        }
        else
        {
            var centerVals = new List<double>(validCount);
            for (int s = 0; s < sCount; s++) if (valid[s]) centerVals.Add(centerCross[s]);
            if (centerVals.Count > 0)
            {
                centerVals.Sort();
                int mid = centerVals.Count / 2;
                weldCenterRoi = centerVals.Count % 2 == 1
                    ? centerVals[mid]
                    : (centerVals[mid - 1] + centerVals[mid]) / 2.0;
            }
            else weldCenterRoi = SampleAround(centerCross, valid, (int)targetS, 5);
        }
        double weldCenterFull = crossOffset + weldCenterRoi;
        double d = weldCenterFull - refPos;

        // 초록 중심선 = 피팅 직선의 두 끝점(유효 s 범위) → 최대한 일직선. 피팅 실패 시 슬라이스 중점으로 폴백.
        int sFirst = -1, sLast = -1;
        for (int s = 0; s < sCount; s++) if (valid[s]) { if (sFirst < 0) sFirst = s; sLast = s; }
        var pts = new List<PixelPoint>(fitOk ? 2 : validCount);
        if (fitOk && sFirst >= 0)
        {
            foreach (int s in new[] { sFirst, sLast })
            {
                double cross = a * s + b;
                pts.Add(horiz ? new PixelPoint(roi.X + s, roi.Y + cross) : new PixelPoint(roi.X + cross, roi.Y + s));
            }
        }
        else
        {
            for (int s = 0; s < sCount; s++)
            {
                if (!valid[s]) continue;
                pts.Add(horiz ? new PixelPoint(roi.X + s, roi.Y + centerCross[s]) : new PixelPoint(roi.X + centerCross[s], roi.Y + s));
            }
        }

        // 비드 폭 span(라벨 초안 마스크용)은 실제 마스크 run 그대로 유지.
        var spans = new List<BeadSpan>(validCount);
        int crossOffsetI = horiz ? roi.Y : roi.X;
        for (int s = 0; s < sCount; s++)
        {
            if (!valid[s]) continue;
            int progFull = (horiz ? roi.X : roi.Y) + s;
            int cs = crossOffsetI + runStart[s];
            int ce = crossOffsetI + runStart[s] + runLen[s] - 1;
            spans.Add(new BeadSpan(progFull, cs, ce));
        }

        // 빨간 비드 중심점(측정 지점)의 full-image 좌표 — 오버레이·깊이 샘플링·스케일 환산용.
        double targetProgFull = (horiz ? roi.X : roi.Y) + targetS;
        var overlay = EncodeOverlay(bgr, roi, p, pts, refPos, weldCenterFull, targetProgFull, peakProgressPos, peakLabel, peakRoi, peakCrossStart, peakCrossEnd);

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
            BeadSpans = spans,
        };
    }

    /// <summary>
    /// 유효 (s, cross) 점들에 최소제곱 직선(cross = a·s + b)을 피팅한다. 비드 중심선을 '일직선'으로
    /// 그리고, 임의의 진행 위치에서 중심 cross 를 안정적으로 얻기 위함. 점&lt;2 또는 특이(수직)면 ok=false.
    /// </summary>
    private static (double A, double B, bool Ok) FitLine(double[] cross, bool[] valid, int n)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int m = 0;
        for (int s = 0; s < n; s++)
        {
            if (!valid[s]) continue;
            sx += s; sy += cross[s]; sxx += (double)s * s; sxy += (double)s * cross[s]; m++;
        }
        if (m < 2) return (0, 0, false);
        double denom = m * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-6) return (0, 0, false);
        double a = (m * sxy - sx * sy) / denom;
        double b = (sy - a * sx) / m;
        return (a, b, true);
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

    /// <summary>centerline 에서 진행축 위치 <paramref name="pp"/> 에 가장 가까운 점의 cross 좌표를 찾는다.
    /// (진행축=가로면 cross=Y, 세로면 cross=X). 점이 없으면 null.</summary>
    private static double? CrossAtProgress(IReadOnlyList<PixelPoint>? centerline, double pp, bool horiz)
    {
        if (centerline is null || centerline.Count == 0) return null;
        double best = double.MaxValue, cross = 0;
        foreach (var pt in centerline)
        {
            double prog = horiz ? pt.X : pt.Y;
            double dd = Math.Abs(prog - pp);
            if (dd < best) { best = dd; cross = horiz ? pt.Y : pt.X; }
        }
        return cross;
    }

    private static byte[]? EncodeOverlay(
        Mat bgr, RoiRect roi, WeldDetectionParams p,
        IReadOnlyList<PixelPoint>? centerline, double refPos, double weldCenterFull, double targetProgFull,
        double? peakProgressPos = null, string? peakLabel = null, RoiRect? peakRoi = null,
        double? peakCrossStart = null, double? peakCrossEnd = null)
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

            // 타깃 비드 중심점(빨강) = Peak 선(진행축=targetProgFull) ∩ 초록 중심선 교차점 + d 텍스트
            if (!double.IsNaN(weldCenterFull) && !double.IsNaN(refPos) && !double.IsNaN(targetProgFull))
            {
                int tx = horiz ? (int)targetProgFull : (int)weldCenterFull;
                int ty = horiz ? (int)weldCenterFull : (int)targetProgFull;
                Cv2.Circle(canvas, new Point(tx, ty), 5, new Scalar(0, 0, 255), -1);
                double d = weldCenterFull - refPos;
                Cv2.PutText(canvas, $"d={d:0.0}px", new Point(roi.X, Math.Max(12, roi.Y - 6)),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 1);
            }

            // Peak 위치(자홍색 진행축-수직 선 + 라벨 P1/P2)
            // 1순위: depth 기반 비드 cross 구간[peakCrossStart..peakCrossEnd]만큼 그린다(급변점에서 끊김).
            //        초록 중심선이 틀려도 영향받지 않고 실제 측정 비드 위에 정확히 그려진다.
            // 2순위(구간 없음): 중심선 위 고정 길이(±PeakTickHalf) 틱으로 폴백.
            if (peakProgressPos is { } pp && !double.IsNaN(pp))
            {
                var magenta = new Scalar(255, 0, 255);
                var pr = peakRoi ?? roi;
                bool hasSpan = peakCrossStart is { } cs0 && peakCrossEnd is { } ce0
                    && !double.IsNaN(cs0) && !double.IsNaN(ce0) && Math.Abs(ce0 - cs0) >= 1.0;

                if (horiz)
                {
                    int x = (int)pp;
                    int lo = Math.Max(0, pr.Y), hiB = Math.Min(canvas.Height - 1, pr.Bottom);
                    int y0, y1;
                    if (hasSpan)
                    {
                        y0 = Math.Clamp((int)Math.Min(peakCrossStart!.Value, peakCrossEnd!.Value), lo, hiB);
                        y1 = Math.Clamp((int)Math.Max(peakCrossStart!.Value, peakCrossEnd!.Value), lo, hiB);
                    }
                    else
                    {
                        double cc = CrossAtProgress(centerline, pp, horiz) ?? (pr.Y + pr.Height / 2.0);
                        y0 = Math.Clamp((int)(cc - PeakTickHalf), lo, hiB);
                        y1 = Math.Clamp((int)(cc + PeakTickHalf), lo, hiB);
                    }
                    Cv2.Line(canvas, new Point(x, y0), new Point(x, y1), magenta, 1);
                    if (!string.IsNullOrEmpty(peakLabel))
                        Cv2.PutText(canvas, peakLabel, new Point(x + 3, Math.Max(12, y0 - 4)),
                            HersheyFonts.HersheySimplex, 0.5, magenta, 1);
                }
                else
                {
                    int y = (int)pp;
                    int lo = Math.Max(0, pr.X), hiR = Math.Min(canvas.Width - 1, pr.Right);
                    int x0, x1;
                    if (hasSpan)
                    {
                        x0 = Math.Clamp((int)Math.Min(peakCrossStart!.Value, peakCrossEnd!.Value), lo, hiR);
                        x1 = Math.Clamp((int)Math.Max(peakCrossStart!.Value, peakCrossEnd!.Value), lo, hiR);
                    }
                    else
                    {
                        double cc = CrossAtProgress(centerline, pp, horiz) ?? (pr.X + pr.Width / 2.0);
                        x0 = Math.Clamp((int)(cc - PeakTickHalf), lo, hiR);
                        x1 = Math.Clamp((int)(cc + PeakTickHalf), lo, hiR);
                    }
                    Cv2.Line(canvas, new Point(x0, y), new Point(x1, y), magenta, 1);
                    if (!string.IsNullOrEmpty(peakLabel))
                        Cv2.PutText(canvas, peakLabel, new Point(x1 + 3, Math.Max(12, y - 4)),
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
}
