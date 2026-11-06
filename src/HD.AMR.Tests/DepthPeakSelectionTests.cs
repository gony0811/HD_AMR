using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;

namespace HD.AMR.Tests;

/// <summary>
/// ROI 에 Peak 가 여럿 들어올 때 선호 위치(FOV 센터·센터링 후 예상 위치)에 가장 가까운 Peak 를 고르는지.
/// 실기 2026-11-06 19:18 PU: ROI 802px &gt; Peak 간격 → 재측정이 카메라에 더 가까운 옆 Peak 를 잡아 '이격 증가'로 오판.
/// </summary>
public class DepthPeakSelectionTests
{
    private const int W = 848, H = 480;
    private static readonly RoiRect Roi = new(22, 46, 802, 393);

    /// <summary>진행축(가로) 코루게이션: 마루(깊이 최소)가 <paramref name="p0"/> + k·<paramref name="period"/> px,
    /// 진폭 20mm, 오른쪽으로 갈수록 <paramref name="tiltMmPerPx"/> 만큼 가까워지는 기울기.</summary>
    private static CameraFrame Corrugation(double p0, double period, double tiltMmPerPx)
    {
        var px = new ushort[W * H];
        for (var x = 0; x < W; x++)
        {
            var d = 300 + 10 * (1 - Math.Cos(2 * Math.PI * (x - p0) / period)) - tiltMmPerPx * x;
            var v = (ushort)Math.Round(d);
            for (var y = 0; y < H; y++) px[y * W + x] = v;
        }
        var bytes = new byte[px.Length * 2];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
        return new CameraFrame(bytes, W, H, "depth16", DateTime.Now);
    }

    // 센터(424) 왼쪽 85px 에 마루, 오른쪽 옆 마루(+400px)가 기울기로 8mm 더 가까움.
    private static readonly CameraFrame Field = Corrugation(339, 400, 0.02);

    [Fact]
    public void NoPreference_KeepsClosestDepth()
    {
        var p = DepthPeakAnalyzer.Analyze(Field, Roi, WeldProgressAxis.Horizontal);
        Assert.True(p.Found);
        Assert.InRange(p.ProgressPos, 739 - 15, 739 + 15);
    }

    [Fact]
    public void PreferCenter_PicksNearestPeak_NotClosestDepth()
    {
        var p = DepthPeakAnalyzer.Analyze(Field, Roi, WeldProgressAxis.Horizontal, preferPos: W / 2.0);
        Assert.True(p.Found);
        Assert.Equal(2, p.PeakCount);
        Assert.InRange(p.ProgressPos, 339 - 15, 339 + 15);
    }

    [Fact]
    public void PreferExpected_PicksPeakNearExpected()
    {
        var p = DepthPeakAnalyzer.Analyze(Field, Roi, WeldProgressAxis.Horizontal, preferPos: 700);
        Assert.InRange(p.ProgressPos, 739 - 15, 739 + 15);
    }

    [Fact]
    public void Slope_WithoutLocalPeak_FallsBackToClosest()
    {
        // 마루 없이 오른쪽으로 계속 가까워지는 경사 → 국소 마루 0개, 종전대로 깊이 최소(오른쪽 끝).
        var p = DepthPeakAnalyzer.Analyze(Corrugation(0, 1e9, 0.05), Roi, WeldProgressAxis.Horizontal, preferPos: W / 2.0);
        Assert.True(p.Found);
        Assert.Equal(0, p.PeakCount);
        Assert.True(p.ProgressPos > Roi.Right - 20);
    }

    [Fact]
    public void LocalPeaks_IgnoresSmallRipple()
    {
        var prof = Enumerable.Range(0, 200).Select(i => 300 + 0.8 * Math.Sin(i * 0.7)).ToArray();
        var ok = Enumerable.Repeat(true, prof.Length).ToArray();
        Assert.Empty(DepthPeakAnalyzer.LocalPeaks(prof, ok, 3.0));
    }
}
