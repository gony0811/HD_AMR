using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;

namespace HD.AMR.Tests;

/// <summary>
/// ⑦ 비드선 따라 Peak 재측정(<see cref="DepthPeakAnalyzer.RefineAlongLine"/>).
/// 저장 데이터 143번: 화면 전체 cross 중앙값 Peak 가 근처 코로게이션 교차부(노드)에 끌려 비드선 위 꼭대기와 25mm 어긋났다.
/// </summary>
public class DepthPeakAlongBeadTests
{
    private const int W = 848, H = 480;

    private static CameraFrame Depth(Func<int, int, double> mm)
    {
        var px = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) px[y * W + x] = (ushort)Math.Max(0, Math.Round(mm(x, y)));
        var bytes = new byte[px.Length * 2];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
        return new CameraFrame(bytes, W, H, "depth16", DateTime.Now);
    }

    /// <summary>세로 마루(꼭대기 x=ridgeX, 반폭 40px, 20mm 솟음) — 진행축 가로.</summary>
    private static double Ridge(double x, double ridgeX)
        => Math.Abs(x - ridgeX) < 40 ? -20 * (1 - Math.Pow((x - ridgeX) / 40, 2)) : 0;

    [Fact]
    public void 노드에_끌린_Peak를_비드선_위_마루_꼭대기로_옮긴다()
    {
        // 비드선 y=300 위의 마루 꼭대기는 x=440. 위쪽 절반 이상(y<260)에 노드가 x=405 에서 더 높이 솟아 있다.
        var d = Depth((x, y) => 300 + Ridge(x, 440) + (y < 260 ? Ridge(x, 405) * 1.5 : 0));
        var full = DepthPeakAnalyzer.Analyze(d, new RoiRect(20, 20, 808, 440), WeldProgressAxis.Horizontal, preferPos: 424);
        Assert.InRange(full.ProgressPos, 400, 430);   // 전체 중앙값 Peak 는 노드 쪽으로 밀린다(재현)

        var lp = DepthPeakAnalyzer.RefineAlongLine(d, new PixelPoint(0, 300), new PixelPoint(847, 300),
            WeldProgressAxis.Horizontal, full.ProgressPos, 60)!.Value;
        Assert.InRange(lp.ProgressPos, 438, 442);
        Assert.Equal(280, lp.DepthMm, 0);
        Assert.True(lp.ProminenceMm > 10);
    }

    [Fact]
    public void 기운_비드선과_세로_진행축도_선_위만_읽는다()
    {
        // 진행축 세로, 비드선 x = 300 + 0.2·y. 가로 마루 꼭대기 y=260 — 비드선 밖(x>600)에는 y=200 에 더 높은 구조물.
        var d = Depth((x, y) => 300 + Ridge(y, 260) + (x > 600 ? Ridge(y, 200) * 2 : 0));
        var lp = DepthPeakAnalyzer.RefineAlongLine(d, new PixelPoint(300, 0), new PixelPoint(396, 480),
            WeldProgressAxis.Vertical, 225, 60)!.Value;
        Assert.InRange(lp.ProgressPos, 258, 262);
    }

    [Fact]
    public void 평탄면이면_확신이_없어_null()
    {
        var d = Depth((_, _) => 300);
        Assert.Null(DepthPeakAnalyzer.RefineAlongLine(d, new PixelPoint(0, 240), new PixelPoint(847, 240),
            WeldProgressAxis.Horizontal, 424, 60));
    }

    [Fact]
    public void 창_끝으로_계속_가까워지는_경사는_꼭대기가_아니라_null()
    {
        // 꼭대기 x=560 은 창(424±60) 밖 — 창 안은 오른쪽으로 갈수록 가까워지기만 한다.
        var d = Depth((x, _) => 300 + Ridge(x, 560) - 0.05 * x);
        Assert.Null(DepthPeakAnalyzer.RefineAlongLine(d, new PixelPoint(0, 240), new PixelPoint(847, 240),
            WeldProgressAxis.Horizontal, 424, 60));
    }

    [Fact]
    public void 비드선_위_깊이가_대부분_비면_null()
    {
        // 비드선 y=240 주변 ±10px 이 결측(금속광택).
        var d = Depth((x, y) => Math.Abs(y - 240) <= 10 ? 0 : 300 + Ridge(x, 440));
        Assert.Null(DepthPeakAnalyzer.RefineAlongLine(d, new PixelPoint(0, 240), new PixelPoint(847, 240),
            WeldProgressAxis.Horizontal, 424, 60));
    }
}
