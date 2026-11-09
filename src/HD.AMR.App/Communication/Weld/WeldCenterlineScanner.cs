using HD.AMR.App.Models;

namespace HD.AMR.App.Communication.Weld;

/// <summary>
/// 이진 비드 마스크 → 비드 중심선(경계-중점 + 직선 피팅). <see cref="WeldMaskAnalyzer"/> 에서 OpenCV 와 무관한
/// 계산만 떼어 낸 순수 함수 — 운영 검출(Windows)과 macOS 의 추론·검증이 같은 방식으로 중심선을 얻는다.
/// 진행축 슬라이스마다 마스크의 가장 긴 연속 구간(=비드 본체) 중점을 구하고, 최소제곱 또는 RANSAC 으로 직선화한다.
/// </summary>
public static class WeldCenterlineScanner
{
    /// <param name="m">ROI-local 마스크(row-major, roi.Width×roi.Height, 비드=nonzero).</param>
    public static CenterlineScan Scan(byte[] m, RoiRect roi, WeldDetectionParams p)
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
        bool ransac = p.LineFit == WeldLineFitMode.Ransac;
        // RANSAC 후보: 슬라이스별 모든 run(최장 run 만이 아니라 반사로 끊긴 비드 조각까지). Tag = runs 인덱스.
        var cands = ransac ? new List<RobustLineFit.Candidate>() : null;
        var runs = ransac ? new List<(int Start, int Len)>() : null;
        int minRun = Math.Max(1, p.RansacMinRunPx);

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
                else
                {
                    if (curLen >= minRun) AddCand(s, curStart, curLen);
                    curStart = -1; curLen = 0;
                }
            }
            if (curStart >= 0 && curLen >= minRun) AddCand(s, curStart, curLen);
            if (bestStart >= 0)
            {
                centerCross[s] = bestStart + (bestLen - 1) / 2.0;
                runStart[s] = bestStart;
                runLen[s] = bestLen;
                valid[s] = true;
                validCount++;
            }
        }

        void AddCand(int sl, int start, int len)
        {
            if (cands is null || runs is null) return;
            cands.Add(new RobustLineFit.Candidate(sl, start + (len - 1) / 2.0, runs.Count));
            runs.Add((start, len));
        }

        double coverage = sCount > 0 ? (double)validCount / sCount : 0;
        if (validCount < 3 || coverage < 0.15)
            return new CenterlineScan(false, horiz, sCount, coverage, coverage, centerCross, valid, runStart, runLen, validCount, 0, 0, false);

        // RANSAC: 가장 많은 슬라이스를 일직선으로 지나는 run 들을 골라 centerCross/valid/run 을 그것으로 교체.
        // 실패하면 기존 최장 run + 최소제곱 경로로 폴백.
        double confidence = coverage;
        double ra = 0, rb = 0;
        bool ransacOk = false;
        if (ransac && cands is { Count: >= 2 } && runs is not null)
        {
            var fit = RobustLineFit.Fit(cands, p.RansacInlierPx, p.RansacMaxAngleDeg);
            if (fit.Ok)
            {
                ransacOk = true; ra = fit.A; rb = fit.B;
                Array.Clear(valid);
                validCount = 0;
                foreach (var (sl, c) in fit.Chosen)
                {
                    var (st, ln) = runs[c.Tag];
                    centerCross[sl] = c.Cross; runStart[sl] = st; runLen[sl] = ln;
                    valid[sl] = true; validCount++;
                }
                confidence = (double)fit.InlierSlices / sCount;
            }
        }

        // 이동평균 smoothing(최소제곱 경로 전용 — RANSAC 은 이미 직선 inlier 만 남김)
        if (!ransacOk && p.SmoothingWindow >= 2)
            SmoothValid(centerCross, valid, p.SmoothingWindow);

        // 비드 중심선을 '직선'으로: 유효 (s, cross) 점들에 최소제곱 직선 피팅(cross ≈ a·s + b).
        var (a, b, fitOk) = ransacOk ? (ra, rb, true) : FitLine(centerCross, valid, sCount);
        return new CenterlineScan(true, horiz, sCount, coverage, confidence, centerCross, valid, runStart, runLen, validCount, a, b, fitOk);
    }

    /// <summary>중심선 점(전체 프레임 좌표). 직선 피팅이 되면 유효 구간 양 끝 2점, 아니면 슬라이스 중점들.</summary>
    public static List<PixelPoint> Points(CenterlineScan sc, RoiRect roi)
    {
        int sFirst = -1, sLast = -1;
        for (int s = 0; s < sc.SCount; s++) if (sc.Valid[s]) { if (sFirst < 0) sFirst = s; sLast = s; }
        var pts = new List<PixelPoint>(sc.FitOk ? 2 : sc.ValidCount);
        if (sc.FitOk && sFirst >= 0)
        {
            foreach (int s in new[] { sFirst, sLast })
            {
                double cross = sc.A * s + sc.B;
                pts.Add(sc.Horizontal ? new PixelPoint(roi.X + s, roi.Y + cross) : new PixelPoint(roi.X + cross, roi.Y + s));
            }
        }
        else
        {
            for (int s = 0; s < sc.SCount; s++)
            {
                if (!sc.Valid[s]) continue;
                pts.Add(sc.Horizontal ? new PixelPoint(roi.X + s, roi.Y + sc.CenterCross[s]) : new PixelPoint(roi.X + sc.CenterCross[s], roi.Y + s));
            }
        }

        return pts;
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

}

/// <summary>중심선 스캔 결과(ROI-local, s = 진행축 인덱스, cross = 수직 좌표). 직선: cross = A·s + B.</summary>
public sealed record CenterlineScan(bool Ok, bool Horizontal, int SCount, double Coverage, double Confidence,
    double[] CenterCross, bool[] Valid, int[] RunStart, int[] RunLen, int ValidCount, double A, double B, bool FitOk);
