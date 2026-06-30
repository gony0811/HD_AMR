using System.Runtime.InteropServices;
using HD_AMR.Models;

namespace HD_AMR.Communication.Weld;

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
    public static PeakInfo Analyze(CameraFrame depth, RoiRect roi, WeldProgressAxis axis,
        double promK = 4.0, double iqrK = 4.0, double minPromBandMm = 40.0, double minIqrBandMm = 40.0)
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

        // 두드러짐(prominence) 기반 신뢰도: 평균 대비 얼마나 얕은지 + 결측률.
        double sum = 0; int n = meds.Count;
        foreach (var m in meds) sum += m;
        double mean = sum / n;
        double prominence = mean > 0 ? Math.Clamp((mean - bestV) / mean, 0, 1) : 0;
        double coverage = (double)n / sCount;
        double conf = Math.Clamp(0.5 * prominence + 0.5 * coverage, 0, 1);
        if (!gated) conf *= 0.5;   // 폴백이면 신뢰도 절반.

        double progressPos = (horiz ? clamped.X : clamped.Y) + bestS;
        return new PeakInfo
        {
            Found = true,
            ProgressPos = progressPos,
            DepthValue = (int)Math.Round(bestV),
            Confidence = conf,
        };
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
