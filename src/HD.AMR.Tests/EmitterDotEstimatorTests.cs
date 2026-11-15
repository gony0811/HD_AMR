using HD.AMR.App.Service.Vision;

namespace HD.AMR.Tests;

/// <summary>IR 이미지만으로 이미터 ON/OFF 추정(도트 개수).</summary>
public class EmitterDotEstimatorTests
{
    private const int W = 200, H = 120;

    // 완만한 밝기 + (선택) 격자 도트(3×3, 배경보다 +80).
    private static byte[] Image(int dotStep)
    {
        var g = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                g[y * W + x] = (byte)(40 + x / 4);
        if (dotStep > 0)
            for (int y = 4; y < H - 4; y += dotStep)
                for (int x = 4; x < W - 4; x += dotStep)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            g[(y + dy) * W + x + dx] = (byte)Math.Min(255, g[(y + dy) * W + x + dx] + 80);
        return g;
    }

    [Fact]
    public void 매끈한_영상은_도트_0개()
        => Assert.Equal(0, EmitterDotEstimator.CountDots(Image(0), W, H));

    [Fact]
    public void 격자_도트를_하나씩_센다()
    {
        // 4..115 를 8 간격 → 세로 14개, 4..195 를 8 간격 → 가로 24개.
        Assert.Equal(14 * 24, EmitterDotEstimator.CountDots(Image(8), W, H));
    }

    [Fact]
    public void 넓은_밝은_영역은_도트가_아니다()
    {
        var g = Image(0);
        for (int y = 30; y < 90; y++) for (int x = 60; x < 140; x++) g[y * W + x] = 255;   // LED 반사 덩어리
        Assert.Equal(0, EmitterDotEstimator.CountDots(g, W, H));
    }

    [Theory]
    [InlineData(150, EmitterState.Off)]
    [InlineData(EmitterDotEstimator.OffMax, EmitterState.Off)]
    [InlineData(380, EmitterState.Unknown)]
    [InlineData(EmitterDotEstimator.OnMin, EmitterState.On)]
    [InlineData(2500, EmitterState.On)]
    public void 개수로_상태_판정(int dots, EmitterState expected)
        => Assert.Equal(expected, EmitterDotEstimator.Classify(dots));
}
