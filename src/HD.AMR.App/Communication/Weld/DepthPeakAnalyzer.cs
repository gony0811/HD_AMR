using System.Runtime.InteropServices;
using HD.AMR.App.Models;

namespace HD.AMR.App.Communication.Weld;

/// <summary>
/// Depth Map 에서 Peak(명세서 7장)를 검출한다. 위에서 촬영 시 Peak 는 카메라와 가장 가까워 depth 가
/// 작으므로, 진행 방향을 따라 cross-section median 1D 프로파일을 만들고 그 최소(local minimum) 지점을
/// Peak 로 본다. OpenCV 불필요(순수 배열 연산)라 모든 플랫폼에서 동작한다.
/// </summary>
public static class DepthPeakAnalyzer
{
    /// <param name="depth">"depth16" CameraFrame (little-endian uint16 mm, 0=무효).</param>
    /// <param name="roi">Peak 검출 ROI.</param>
    /// <param name="axis">진행축. Horizontal=x, Vertical=y.</param>
    /// <param name="promK">① 물리적 prominence 게이트. 슬라이스 median 분포의 (1.4826·MAD)에 곱하는 계수.
    /// 이보다 더 가까운(작은) 슬라이스는 비현실적 jump 로 보고 Peak 후보에서 제외한다.</param>
    /// <param name="iqrK">② cross-section 잡음 게이트. 슬라이스 IQR 분포의 (1.4826·MAD)에 곱하는 계수.
    /// 이보다 산포가 큰 슬라이스는 flying-pixel 의심으로 후보에서 제외한다.</param>
    /// <param name="minPromBandMm">prominence 게이트 최소 밴드(mm). MAD≈0(평탄면)일 때 진짜 비드(수 mm 돌출)를
    /// 잘못 제외하지 않도록 게이트를 최소 이만큼 median 아래로 둔다. 보수적으로 크게 잡는다.</param>
    /// <param name="minIqrBandMm">IQR 게이트 최소 밴드(mm). 위와 같은 취지로 잡음 상한의 하한을 둔다.</param>
    /// <param name="crossJumpMm">Peak 슬라이스 cross 구간 산출 시, 인접 픽셀 depth 차가 이보다 크면
    /// 비드 경계로 보고 선을 끊는다.</param>
    /// <param name="crossBandMm">cross 구간 산출 시 씨앗(최소) depth 대비 이보다 더 멀어지면 끊는다(상한).</param>
    /// <param name="preferPos">선호 진행축 좌표(프레임 픽셀). 주면 ROI 안의 Peak(국소 마루) 중 이 위치에 가장 가까운 것을 고른다
    /// (예: FOV 센터, 센터링 이동 후 예상 위치). ROI 가 Peak 간격보다 넓어 Peak 가 여럿 들어올 때 카메라에 더 가까운
    /// 옆 Peak 를 잡지 않게 한다. null 이거나 국소 마루를 못 찾으면 종전대로 가장 가까운(깊이 최소) 슬라이스.</param>
    /// <param name="minPeakProminenceMm">국소 마루 인정 기준 — 양쪽으로 이만큼 이상 멀어져야(깊이 증가) Peak 로 본다.</param>
    public static PeakInfo Analyze(CameraFrame depth, RoiRect roi, WeldProgressAxis axis,
        double promK = 4.0, double iqrK = 4.0, double minPromBandMm = 40.0, double minIqrBandMm = 40.0,
        double crossJumpMm = 25.0, double crossBandMm = 60.0,
        double? preferPos = null, double minPeakProminenceMm = 3.0)
    {
        if (depth.PixelFormat != "depth16") return new PeakInfo { Found = false };
        var clamped = roi.ClampTo(depth.Width, depth.Height);
        if (clamped is null) return new PeakInfo { Found = false };

        var px = MemoryMarshal.Cast<byte, ushort>(depth.Pixels);
        int W = depth.Width;
        bool horiz = axis == WeldProgressAxis.Horizontal;
        int sCount = horiz ? clamped.Width : clamped.Height;
        int crossCount = horiz ? clamped.Height : clamped.Width;

        var profile = new double[sCount];
        var iqr = new double[sCount];   // 슬라이스 cross-section 산포(잡음 척도)
        var hasVal = new bool[sCount];
        var scratch = new List<int>(crossCount);

        for (int s = 0; s < sCount; s++)
        {
            scratch.Clear();
            for (int c = 0; c < crossCount; c++)
            {
                int x = clamped.X + (horiz ? s : c);
                int y = clamped.Y + (horiz ? c : s);
                int idx = y * W + x;
                if ((uint)idx >= (uint)px.Length) continue;
                int mm = px[idx];
                if (mm > 0) scratch.Add(mm);
            }
            if (scratch.Count >= Math.Max(3, crossCount / 5))
            {
                scratch.Sort();
                int cnt = scratch.Count;
                profile[s] = scratch[cnt / 2];                      // median
                iqr[s] = scratch[(3 * cnt) / 4] - scratch[cnt / 4]; // Q3-Q1
                hasVal[s] = true;
            }
        }

        // 유효 슬라이스의 robust 분포(median·MAD)로 게이트 임계를 데이터에서 직접 잡는다(스케일 무관).
        var meds = new List<double>(sCount);
        var iqrs = new List<double>(sCount);
        for (int s = 0; s < sCount; s++)
            if (hasVal[s]) { meds.Add(profile[s]); iqrs.Add(iqr[s]); }
        if (meds.Count == 0) return new PeakInfo { Found = false };

        double medProfile = Median(meds);
        double madProfile = Mad(meds, medProfile);
        double medIqr = Median(iqrs);
        double madIqr = Mad(iqrs, medIqr);

        // ① 너무 가까우면(median 대비 비현실적으로 작으면) 불량 → 제외. 최소 밴드로 진짜 비드는 보존.
        double promGate = medProfile - Math.Max(promK * 1.4826 * madProfile, minPromBandMm);
        // ② cross-section 산포가 너무 크면(flying-pixel) 불량 → 제외.
        double iqrGate = medIqr + Math.Max(iqrK * 1.4826 * madIqr, minIqrBandMm);

        // 적격(게이트 통과) 슬라이스 중 최소(=가장 가까운) 지점 탐색.
        int bestS = -1; double bestV = double.MaxValue; int eligible = 0;
        for (int s = 0; s < sCount; s++)
        {
            if (!hasVal[s]) continue;
            if (profile[s] < promGate) continue;   // ① 비현실적으로 가까움
            if (iqr[s] > iqrGate) continue;          // ② 잡음 과다
            eligible++;
            if (profile[s] < bestV) { bestV = profile[s]; bestS = s; }
        }

        // 안전장치: 게이트로 후보가 0개면 기존 글로벌 최소로 폴백(신뢰도는 페널티).
        bool gated = bestS >= 0;
        if (!gated)
        {
            for (int s = 0; s < sCount; s++)
            {
                if (!hasVal[s]) continue;
                if (profile[s] < bestV) { bestV = profile[s]; bestS = s; }
            }
        }
        if (bestS < 0) return new PeakInfo { Found = false };

        // 선호 위치가 있으면 국소 마루 중 그 위치에 가장 가까운 것을 고른다.
        int peakCount = 0;
        if (gated && preferPos is { } pref)
        {
            var eligibleMask = new bool[sCount];
            for (int s = 0; s < sCount; s++)
                eligibleMask[s] = hasVal[s] && profile[s] >= promGate && iqr[s] <= iqrGate;
            var peaks = LocalPeaks(profile, eligibleMask, minPeakProminenceMm);
            peakCount = peaks.Count;
            if (peaks.Count > 0)
            {
                double prefS = pref - (horiz ? clamped.X : clamped.Y);
                bestS = peaks.OrderBy(s => Math.Abs(s - prefS)).First();
                bestV = profile[bestS];
            }
        }

        // 두드러짐(prominence) 기반 신뢰도: 평균 대비 얼마나 얕은지 + 결측률.
        double sum = 0; int n = meds.Count;
        foreach (var m in meds) sum += m;
        double mean = sum / n;
        double prominence = mean > 0 ? Math.Clamp((mean - bestV) / mean, 0, 1) : 0;
        double coverage = (double)n / sCount;
        double conf = Math.Clamp(0.5 * prominence + 0.5 * coverage, 0, 1);
        if (!gated) conf *= 0.5;   // 폴백이면 신뢰도 절반.

        double progressPos = (horiz ? clamped.X : clamped.Y) + bestS;

        // Peak 슬라이스에서 비드 cross 구간: depth 최소 픽셀을 씨앗으로 양쪽으로 뻗다가, 인접 픽셀과
        // 급격히 점프(>crossJumpMm)하거나 무효(0)/상한(>씨앗+crossBandMm)을 만나면 멈춘다.
        bool hasSpan = false; double crossStart = 0, crossEnd = 0;
        {
            int cross0 = horiz ? clamped.Y : clamped.X;
            var dline = new int[crossCount];
            int seed = -1, seedV = int.MaxValue;
            for (int c = 0; c < crossCount; c++)
            {
                int x = clamped.X + (horiz ? bestS : c);
                int y = clamped.Y + (horiz ? c : bestS);
                int idx = y * W + x;
                int mm = (uint)idx < (uint)px.Length ? px[idx] : 0;
                dline[c] = mm;
                if (mm > 0 && mm < seedV) { seedV = mm; seed = c; }
            }
            if (seed >= 0)
            {
                int lo = seed, hi = seed;
                for (int c = seed - 1; c >= 0; c--)
                {
                    if (dline[c] <= 0 || Math.Abs(dline[c] - dline[c + 1]) > crossJumpMm
                        || dline[c] > seedV + crossBandMm) break;
                    lo = c;
                }
                for (int c = seed + 1; c < crossCount; c++)
                {
                    if (dline[c] <= 0 || Math.Abs(dline[c] - dline[c - 1]) > crossJumpMm
                        || dline[c] > seedV + crossBandMm) break;
                    hi = c;
                }
                crossStart = cross0 + lo;
                crossEnd = cross0 + hi;
                hasSpan = hi > lo;
            }
        }

        return new PeakInfo
        {
            Found = true,
            ProgressPos = progressPos,
            DepthValue = (int)Math.Round(bestV),
            Confidence = conf,
            PeakCount = peakCount,
            HasCrossSpan = hasSpan,
            CrossStart = crossStart,
            CrossEnd = crossEnd,
        };
    }

    /// <summary>비드선 따라 재측정한 Peak — 진행 좌표·그 자리 깊이(mm)·양쪽 중 낮은 쪽 두드러짐(mm).</summary>
    public readonly record struct LinePeak(double ProgressPos, double DepthMm, double ProminenceMm);

    /// <summary>
    /// <b>비드선을 따라</b> Peak 를 다시 잰다. <see cref="Analyze"/> 는 ROI cross 폭 전체의 슬라이스 중앙값으로 Peak 를 잡아,
    /// 근처의 코로게이션 교차부(노드) 같은 다른 구조물에 끌려 비드선 위 마루 꼭대기에서 수 cm 벗어날 수 있다(저장 데이터
    /// 143번: 25mm). 여기서는 비드 중심선(직선, 양 끝 밖은 외삽) 위의 깊이만 읽어 <paramref name="startPos"/> ±
    /// <paramref name="windowPx"/> 안에서 카메라에 가장 가까운 점을 찾는다 — 그 점이 비드선과 마루 꼭대기의 교점이다.
    ///
    /// 창 안의 유효 깊이가 절반 미만이거나, 찾은 점이 양쪽으로 <paramref name="minProminenceMm"/> 이상 솟아 있지 않으면
    /// (창 끝 경사·평탄면) 확신할 수 없으므로 null — 호출측은 원래 Peak 를 그대로 쓴다.
    /// </summary>
    /// <param name="depth">"depth16" 프레임.</param>
    /// <param name="lineA">비드 중심선 위 한 점(depth 픽셀).</param>
    /// <param name="lineB">비드 중심선 위 다른 한 점(depth 픽셀). 진행축 좌표가 <paramref name="lineA"/> 와 달라야 한다.</param>
    /// <param name="axis">진행축.</param>
    /// <param name="startPos">현재 Peak 진행 좌표(depth 픽셀).</param>
    /// <param name="windowPx">탐색 반경(depth 픽셀).</param>
    /// <param name="bandPx">선 위 각 점에서 깊이 중앙값을 낼 정사각 반폭(px). 금속광택 결측 대비.</param>
    public static LinePeak? RefineAlongLine(CameraFrame depth, PixelPoint lineA, PixelPoint lineB, WeldProgressAxis axis,
        double startPos, double windowPx, double minProminenceMm = 3.0, int bandPx = 3)
    {
        if (depth.PixelFormat != "depth16" || windowPx < 3) return null;
        bool horiz = axis == WeldProgressAxis.Horizontal;
        double sA = horiz ? lineA.X : lineA.Y, cA = horiz ? lineA.Y : lineA.X;
        double sB = horiz ? lineB.X : lineB.Y, cB = horiz ? lineB.Y : lineB.X;
        if (Math.Abs(sB - sA) < 1e-6) return null;
        double slope = (cB - cA) / (sB - sA);

        var px = MemoryMarshal.Cast<byte, ushort>(depth.Pixels);
        int W = depth.Width, H = depth.Height;
        int n = horiz ? W : H, cMax = horiz ? H : W;
        int s0 = Math.Max(0, (int)Math.Floor(startPos - windowPx)), s1 = Math.Min(n - 1, (int)Math.Ceiling(startPos + windowPx));
        if (s1 - s0 < 4) return null;

        var raw = new double[s1 - s0 + 1];
        var vals = new List<int>((2 * bandPx + 1) * (2 * bandPx + 1));
        int validCount = 0;
        for (int s = s0; s <= s1; s++)
        {
            int c = (int)Math.Round(cA + slope * (s - sA));
            vals.Clear();
            if (c >= -bandPx && c < cMax + bandPx)
                for (int dc = -bandPx; dc <= bandPx; dc++)
                    for (int ds = -bandPx; ds <= bandPx; ds++)
                    {
                        int ss = s + ds, cc = c + dc;
                        if (ss < 0 || cc < 0 || ss >= n || cc >= cMax) continue;
                        int idx = horiz ? cc * W + ss : ss * W + cc;
                        if ((uint)idx < (uint)px.Length && px[idx] > 0) vals.Add(px[idx]);
                    }
            if (vals.Count > 0) { vals.Sort(); raw[s - s0] = vals[vals.Count / 2]; validCount++; }
        }
        if (validCount < raw.Length / 2) return null;

        // 이동평균(±4, 유효값만) — 슬라이스 잡음으로 가짜 극소가 생기지 않게.
        var sm = new double[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            double sum = 0; int k = 0;
            for (int j = Math.Max(0, i - 4); j <= Math.Min(raw.Length - 1, i + 4); j++)
                if (raw[j] > 0) { sum += raw[j]; k++; }
            sm[i] = k > 0 ? sum / k : 0;
        }

        int best = -1; double bestV = double.MaxValue;
        for (int i = 0; i < sm.Length; i++)
            if (sm[i] > 0 && sm[i] < bestV) { bestV = sm[i]; best = i; }
        if (best < 0) return null;

        // 양쪽으로 실제로 멀어져야(솟은 마루) 인정 — 창 끝에서 계속 가까워지는 경사는 꼭대기가 아니다.
        double leftMax = 0, rightMax = 0;
        for (int i = 0; i < best; i++) if (sm[i] > 0) leftMax = Math.Max(leftMax, sm[i]);
        for (int i = best + 1; i < sm.Length; i++) if (sm[i] > 0) rightMax = Math.Max(rightMax, sm[i]);
        double prom = Math.Min(leftMax - bestV, rightMax - bestV);
        if (prom < minProminenceMm) return null;

        // 평평한 꼭대기는 최소값과 거의 같은(0.5mm 이내) 구간의 중앙을 쓴다.
        int lo = best, hi = best;
        while (lo > 0 && sm[lo - 1] > 0 && sm[lo - 1] <= bestV + 0.5) lo--;
        while (hi < sm.Length - 1 && sm[hi + 1] > 0 && sm[hi + 1] <= bestV + 0.5) hi++;
        return new LinePeak(s0 + (lo + hi) / 2.0, bestV, prom);
    }

    /// <summary>
    /// 적격 슬라이스 프로파일에서 국소 마루(깊이 국소 최소) 위치들. 이동평균으로 다듬은 뒤, 양쪽으로 깊이가
    /// <paramref name="minProminenceMm"/> 이상 커졌다가 다시 내려오기 전까지(=골짜기까지) 그 구간에서 가장 가까운 점을 하나씩 뽑는다.
    /// ROI 끝으로 갈수록 계속 가까워지는 경사(끝 쪽이 잘린 마루)는 한쪽이 솟지 않으므로 후보에서 빠진다.
    /// </summary>
    internal static List<int> LocalPeaks(double[] profile, bool[] eligible, double minProminenceMm)
    {
        int n = profile.Length;
        // 적격 슬라이스만 이어 붙인 서열(인덱스 보존) — 무효 슬라이스는 건너뛴다.
        var idx = new List<int>(n);
        for (int s = 0; s < n; s++) if (eligible[s]) idx.Add(s);
        var result = new List<int>();
        if (idx.Count < 3) return result;

        // 이동평균(±k) — 슬라이스 median 잡음(≈1mm)으로 가짜 극소가 생기지 않게.
        int k = Math.Max(2, idx.Count / 80);
        var sm = new double[idx.Count];
        for (int i = 0; i < idx.Count; i++)
        {
            int a = Math.Max(0, i - k), b = Math.Min(idx.Count - 1, i + k);
            double sum = 0;
            for (int j = a; j <= b; j++) sum += profile[idx[j]];
            sm[i] = sum / (b - a + 1);
        }

        // 히스테리시스 극값 탐색: 직전 극대에서 minProminence 이상 내려가면 '마루 구간' 진입, 그 구간 최소에서
        // minProminence 이상 다시 올라가면 마루 확정. 첫 마루는 왼쪽에서도 그만큼 솟아 있어야 한다.
        double runMax = sm[0];
        bool inPeak = false;
        int minI = -1;
        double minV = double.MaxValue;
        for (int i = 0; i < sm.Length; i++)
        {
            double v = sm[i];
            if (!inPeak)
            {
                if (v > runMax) runMax = v;
                if (v <= runMax - minProminenceMm) { inPeak = true; minI = i; minV = v; }
            }
            else
            {
                if (v < minV) { minV = v; minI = i; }
                else if (v >= minV + minProminenceMm)
                {
                    result.Add(idx[minI]);
                    inPeak = false;
                    runMax = v;
                }
            }
        }
        return result;
    }

    /// <summary>리스트의 중앙값(복사 후 정렬, 원본 불변).</summary>
    private static double Median(List<double> v)
    {
        var t = new List<double>(v);
        t.Sort();
        int m = t.Count / 2;
        return t.Count % 2 == 1 ? t[m] : (t[m - 1] + t[m]) / 2.0;
    }

    /// <summary>median 절대편차(MAD) = median(|xᵢ - med|).</summary>
    private static double Mad(List<double> v, double med)
    {
        var d = new List<double>(v.Count);
        foreach (var x in v) d.Add(Math.Abs(x - med));
        return Median(d);
    }
}
