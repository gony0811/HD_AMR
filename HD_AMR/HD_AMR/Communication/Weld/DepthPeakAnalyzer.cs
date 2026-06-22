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
    public static PeakInfo Analyze(CameraFrame depth, RoiRect roi, WeldProgressAxis axis)
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
                profile[s] = scratch[scratch.Count / 2]; // median
                hasVal[s] = true;
            }
        }

        // 최소(=가장 가까운) 지점 탐색.
        int bestS = -1; double bestV = double.MaxValue; double sum = 0; int n = 0;
        for (int s = 0; s < sCount; s++)
        {
            if (!hasVal[s]) continue;
            sum += profile[s]; n++;
            if (profile[s] < bestV) { bestV = profile[s]; bestS = s; }
        }
        if (bestS < 0 || n == 0) return new PeakInfo { Found = false };

        double mean = sum / n;
        // 두드러짐(prominence) 기반 신뢰도: 평균 대비 얼마나 얕은지 + 결측률.
        double prominence = mean > 0 ? Math.Clamp((mean - bestV) / mean, 0, 1) : 0;
        double coverage = (double)n / sCount;
        double conf = Math.Clamp(0.5 * prominence + 0.5 * coverage, 0, 1);

        double progressPos = (horiz ? clamped.X : clamped.Y) + bestS;
        return new PeakInfo
        {
            Found = true,
            ProgressPos = progressPos,
            DepthValue = (int)Math.Round(bestV),
            Confidence = conf,
        };
    }
}
