using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

public class EmitterFrameClassifierTests
{
    private const int W = 848, H = 480;

    // 완만한 그라데이션 배경(이미터 OFF 유사) + 선택적 도트 패턴(이미터 ON 유사).
    private static byte[] Frame(bool dots, int baseLevel = 60)
    {
        var g = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                g[y * W + x] = (byte)(baseLevel + x * 40 / W);
        if (dots)
        {
            var rnd = new Random(1);
            for (int i = 0; i < 6000; i++)
            {
                int cx = rnd.Next(2, W - 2), cy = rnd.Next(2, H - 2);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        g[(cy + dy) * W + cx + dx] = 230;
            }
        }
        return g;
    }

    [Fact]
    public void 도트_있는_프레임의_에너지가_훨씬_크다()
    {
        var on = EmitterFrameClassifier.DotEnergy(Frame(true), W, H);
        var off = EmitterFrameClassifier.DotEnergy(Frame(false), W, H);
        Assert.True(on > off * 5, $"on={on}, off={off}");
    }

    [Fact]
    public void 첫_프레임은_판정_보류_이후_교번을_판정()
    {
        var c = new EmitterFrameClassifier();
        Assert.Null(c.ClassifyByEnergy(Frame(true), W, H));
        Assert.False(c.ClassifyByEnergy(Frame(false), W, H));
        Assert.True(c.ClassifyByEnergy(Frame(true), W, H));
        Assert.False(c.ClassifyByEnergy(Frame(false), W, H));
    }

    [Fact]
    public void 같은_상태가_연속되면_추측하지_않고_보류()
    {
        var c = new EmitterFrameClassifier();
        c.ClassifyByEnergy(Frame(false), W, H);
        Assert.Null(c.ClassifyByEnergy(Frame(false, baseLevel: 70), W, H));
    }

    [Fact]
    public void 전체_밝기_변화에는_둔감()
    {
        // 자동노출로 OFF 프레임이 밝아져도 도트 없는 한 ON 으로 오판하지 않는다.
        var c = new EmitterFrameClassifier();
        c.ClassifyByEnergy(Frame(false, baseLevel: 40), W, H);
        Assert.NotEqual(true, c.ClassifyByEnergy(Frame(false, baseLevel: 150), W, H));
    }
}
