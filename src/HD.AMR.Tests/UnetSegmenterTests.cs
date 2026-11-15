using HD.AMR.App.Service.Vision;
using Microsoft.ML.OnnxRuntime;

namespace HD.AMR.Tests;

public class UnetSegmenterTests
{
    // logits = (x − 0.5)·20 — 밝기 > 127.5 인 픽셀만 양성. 패딩·잘라내기·임계를 정확히 검증할 수 있다.
    private static InferenceSession Thr() => new(Path.Combine(AppContext.BaseDirectory, "TestData", "threshold_unet.onnx"));

    [Fact]
    public void 패딩_후_원래_크기로_잘라_임계처리한다()
    {
        using var s = Thr();
        Assert.True(UnetSegmenter.LooksLikeUnet(s));
        const int w = 301, h = 157;                   // 32 배수 아님 → 320×160 으로 패딩
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                g[y * w + x] = (byte)(Math.Abs(y - 80) <= 2 ? 200 : 60);   // 가로 5px 밝은 선
        var prob = new float[w * h];
        var m = UnetSegmenter.Segment(s, g, w, h, 0.5f, prob);
        Assert.Equal(w * h, m.Length);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(Math.Abs(y - 80) <= 2 ? 255 : 0, m[y * w + x]);
        Assert.True(prob[80 * w + 10] > 0.99f);
        Assert.True(prob[10 * w + 10] < 0.01f);
    }

    [Fact]
    public void 임계값을_올리면_약한_픽셀이_빠진다()
    {
        using var s = Thr();
        // 밝기 140 → logit (140/255−0.5)·20 ≈ 0.98 → 확률 ≈ 0.73
        var g = Enumerable.Repeat((byte)140, 64 * 32).ToArray();
        Assert.All(UnetSegmenter.Segment(s, g, 64, 32, 0.5f), v => Assert.Equal(255, v));
        Assert.All(UnetSegmenter.Segment(s, g, 64, 32, 0.9f), v => Assert.Equal(0, v));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 4)]
    [InlineData(5, 5, 3)]   // REFLECT_101: n−2
    [InlineData(6, 5, 2)]
    [InlineData(-1, 5, 1)]
    [InlineData(3, 1, 0)]
    public void 반사_인덱스는_BORDER_REFLECT_101(int i, int n, int expected)
        => Assert.Equal(expected, UnetSegmenter.Reflect(i, n));

    [Theory]
    [InlineData(480, 480)]
    [InlineData(848, 864)]
    [InlineData(1, 32)]
    public void 배수32_올림(int v, int expected) => Assert.Equal(expected, UnetSegmenter.PadTo32(v));

    [Fact]
    public void 내장_학습_스크립트가_있다()
    {
        var py = WeldTrainingService.ReadUnetScript();
        Assert.Contains("def cmd_train", py);
        Assert.Contains("def cmd_export", py);
        Assert.Contains("Epoch {ep}/{a.epochs}", py);   // 앱의 진행률 파싱(k/N) 규약
    }
}
