using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;

namespace HD.AMR.Tests;

public class RidgeFilterTests
{
    private const int W = 320, H = 200;

    // 완만한 배경 + 가로 가는 선(y≈lineY, 폭 3px) + 세로 굵은 밝은 띠(코로게이션 반사 유사) + 도트.
    private static byte[] Scene(int lineY = 100, bool brightLine = true, bool vertical = true, bool dots = true)
    {
        var g = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                g[y * W + x] = (byte)(80 + x * 30 / W);
        for (int x = 0; x < W; x++)
            for (int dy = -1; dy <= 1; dy++)
                g[(lineY + dy) * W + x] = (byte)(brightLine ? 140 : 30);
        if (vertical)
            for (int y = 0; y < H; y++)
                for (int x = 200; x < 204; x++)
                    g[y * W + x] = 230;
        if (dots)
        {
            var rnd = new Random(3);
            for (int i = 0; i < 600; i++)
            {
                int cx = rnd.Next(2, W - 2), cy = rnd.Next(2, H - 2);
                if (Math.Abs(cy - lineY) < 6) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        g[(cy + dy) * W + cx + dx] = 200;
            }
        }
        return g;
    }

    private static byte[] Mask(byte[] g, WeldRidgePolarity pol = WeldRidgePolarity.Both, double k = 3.0)
    {
        var resp = RidgeFilter.Response(g, W, H, RidgeFilter.Scales(1.5, 4, 3), pol, horizontal: true, maxAngleDeg: 25);
        return RidgeFilter.Threshold(resp, W, H, horizontal: true, k);
    }

    private static (int OnLine, int Off) Count(byte[] m, int lineY, int band = 4)
    {
        int on = 0, off = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (m[y * W + x] != 0) { if (Math.Abs(y - lineY) <= band) on++; else off++; }
        return (on, off);
    }

    // 분석기(WeldMaskAnalyzer)와 같은 방식으로 슬라이스별 run 을 후보로 만들어 RANSAC.
    private static RobustLineFit.Result FitFromMask(byte[] m)
    {
        var cands = new List<RobustLineFit.Candidate>();
        for (int x = 0; x < W; x++)
        {
            int start = -1;
            for (int y = 0; y <= H; y++)
            {
                bool on = y < H && m[y * W + x] != 0;
                if (on && start < 0) start = y;
                if (!on && start >= 0)
                {
                    if (y - start >= 2) cands.Add(new RobustLineFit.Candidate(x, start + (y - start - 1) / 2.0));
                    start = -1;
                }
            }
        }
        return RobustLineFit.Fit(cands);
    }

    [Fact]
    public void 도트와_세로띠가_있어도_RANSAC_으로_가로선을_찾는다()
    {
        var m = Mask(Scene());
        var (on, _) = Count(m, 100);
        Assert.True(on > W * 0.8, $"선 위 픽셀 {on}");

        var fit = FitFromMask(m);
        Assert.True(fit.Ok);
        Assert.Equal(100, fit.B + fit.A * (W / 2.0), 0);
        Assert.True(Math.Abs(fit.A) < 0.01, $"기울기 {fit.A}");
        Assert.True(fit.InlierSlices > W * 0.8, $"inlier 열 {fit.InlierSlices}");

        // 세로 띠 내부(선과 먼 곳)는 방향 제한으로 응답 없음.
        int vert = 0;
        for (int y = 0; y < H; y++) if (Math.Abs(y - 100) > 6) for (int x = 199; x < 205; x++) if (m[y * W + x] != 0) vert++;
        Assert.True(vert < 20, $"세로 띠 응답 {vert}");
    }

    [Fact]
    public void 극성_필터()
    {
        var dark = Scene(brightLine: false, vertical: false, dots: false);
        Assert.True(Count(Mask(dark, WeldRidgePolarity.Dark), 100).OnLine > W * 0.8);
        // 밝은 선만 찾으면 어두운 선의 중심 행(y=100)은 비어야 한다(양옆 곁가지 응답은 있을 수 있음).
        var bm = Mask(dark, WeldRidgePolarity.Bright);
        Assert.True(Enumerable.Range(0, W).Count(x => bm[100 * W + x] != 0) < W * 0.05);
        Assert.True(Count(Mask(dark, WeldRidgePolarity.Both), 100).OnLine > W * 0.8);
    }

    [Fact]
    public void 세로_진행축이면_세로선을_찾는다()
    {
        var g = Scene(vertical: true, dots: false);
        var resp = RidgeFilter.Response(g, W, H, RidgeFilter.Scales(1.5, 4, 3), WeldRidgePolarity.Bright, horizontal: false, maxAngleDeg: 25);
        var m = RidgeFilter.Threshold(resp, W, H, horizontal: false, 2.0);
        int nearVert = 0, nearHoriz = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (m[y * W + x] == 0) continue;
                if (x >= 194 && x <= 209) nearVert++;
                else if (Math.Abs(y - 100) <= 4) nearHoriz++;
            }
        Assert.True(nearVert > H * 0.8, $"세로 {nearVert}");
        Assert.True(nearHoriz < nearVert * 0.1, $"가로 {nearHoriz}");
    }

    [Fact]
    public void 평탄한_영상은_마스크가_거의_없다()
    {
        var g = new byte[W * H];
        for (int i = 0; i < g.Length; i++) g[i] = (byte)(100 + (i % W) * 20 / W);
        var m = Mask(g);
        Assert.True(m.Count(v => v != 0) < W * H * 0.01);
    }

    [Fact]
    public void 스케일_목록()
    {
        Assert.Equal(new[] { 2.0 }, RidgeFilter.Scales(2, 2, 3));
        var s = RidgeFilter.Scales(1, 4, 3);
        Assert.Equal(3, s.Length);
        Assert.Equal(1, s[0], 6);
        Assert.Equal(2, s[1], 6);
        Assert.Equal(4, s[2], 6);
    }
}
