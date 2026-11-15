using HD.AMR.App.Service.Vision;
using Microsoft.ML.OnnxRuntime;

namespace HD.AMR.Tests;

/// <summary>
/// 중심선 히트맵 모델 추론·디코딩. 시험 모델은 logit = (채널 평균 − 0.5)·20, 메타데이터 train_width=640 —
/// 밝은 선이 곧 히트맵 최댓값이라 배율·전치·띠 마스크를 정확히 검증할 수 있다.
/// </summary>
public class HeatmapSegmenterTests
{
    private static InferenceSession Thr() => new(Path.Combine(AppContext.BaseDirectory, "TestData", "threshold_heatmap.onnx"));

    /// <summary>w×h RGB, 밝은 선(가로면 y=pos, 세로면 x=pos, 폭 5px), 배경 어두움.</summary>
    private static byte[] Line(int w, int h, bool horizontal, int pos)
    {
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int d = horizontal ? Math.Abs(y - pos) : Math.Abs(x - pos);
                byte v = (byte)(d <= 2 ? 230 - d * 20 : 40);
                rgb[(y * w + x) * 3] = rgb[(y * w + x) * 3 + 1] = rgb[(y * w + x) * 3 + 2] = v;
            }
        return rgb;
    }

    /// <summary>각 진행 슬라이스에서 띠 마스크의 중앙 cross 좌표들.</summary>
    private static List<double> Centers(byte[] m, int w, int h, bool horizontal)
    {
        var list = new List<double>();
        int n = horizontal ? w : h, c = horizontal ? h : w;
        for (int s = 0; s < n; s++)
        {
            var on = Enumerable.Range(0, c).Where(k => m[horizontal ? k * w + s : s * w + k] > 0).ToList();
            if (on.Count > 0) list.Add(on.Average());
        }
        return list;
    }

    [Fact]
    public void 형식과_학습_해상도를_메타데이터에서_읽는다()
    {
        using var s = Thr();
        Assert.True(HeatmapSegmenter.LooksLikeHeatmap(s));
        Assert.False(UnetSegmenter.LooksLikeUnet(s));
        Assert.Equal(640, HeatmapSegmenter.TrainWidth(s));
    }

    [Fact]
    public void 가로선은_슬라이스마다_최댓값_자리에_띠를_칠한다()
    {
        using var s = Thr();
        const int w = 300, h = 120;
        var m = HeatmapSegmenter.RidgeMask(s, Line(w, h, true, 61), w, h, fullWidth: 640, horizontal: true, threshold: 0.5f);
        var c = Centers(m, w, h, true);
        Assert.Equal(w, c.Count);
        Assert.All(c, v => Assert.InRange(v, 60, 62));
    }

    [Fact]
    public void 전체_프레임이_크면_학습_배율로_줄여_추론하고_원래_좌표로_되돌린다()
    {
        using var s = Thr();
        const int w = 400, h = 160;
        // fullWidth 1280 → 배율 0.5: 200×80 으로 추론, 결과는 원래 y≈101 근처.
        var m = HeatmapSegmenter.RidgeMask(s, Line(w, h, true, 101), w, h, fullWidth: 1280, horizontal: true, threshold: 0.5f);
        var c = Centers(m, w, h, true);
        Assert.True(c.Count > w * 0.9);
        Assert.All(c, v => Assert.InRange(v, 99.5, 102.5));
    }

    [Fact]
    public void 진행축이_세로면_전치해서_세로선을_찾는다()
    {
        using var s = Thr();
        const int w = 120, h = 260;
        var m = HeatmapSegmenter.RidgeMask(s, Line(w, h, false, 47), w, h, fullWidth: 640, horizontal: false, threshold: 0.5f);
        var c = Centers(m, w, h, false);
        Assert.Equal(h, c.Count);
        Assert.All(c, v => Assert.InRange(v, 46, 48));
    }

    [Fact]
    public void 선이_없으면_임계값_미만이라_비운다()
    {
        using var s = Thr();
        const int w = 64, h = 64;
        var dark = Enumerable.Repeat((byte)40, w * h * 3).ToArray();
        Assert.All(HeatmapSegmenter.RidgeMask(s, dark, w, h, 640, true, 0.5f), v => Assert.Equal(0, v));
    }

    [Fact]
    public void 쌍선형_축소는_평균_밝기를_유지한다()
    {
        var src = Enumerable.Range(0, 8 * 4 * 3).Select(i => (byte)(i % 3 == 0 ? 100 : 200)).ToArray();
        var o = HeatmapSegmenter.ResizeBilinear(src, 8, 4, 4, 2);
        Assert.Equal(4 * 2 * 3, o.Length);
        Assert.Equal(100, o[0]);
        Assert.Equal(200, o[1]);
    }

    [Fact]
    public void 히트맵_모델_파일은_U_Net_칸_모델로_분류된다()
    {
        Assert.True(WeldModelNames.IsUnet("models/weld_heatmap_rgb.onnx"));
        Assert.False(WeldModelNames.IsYolo("models/weld_heatmap_rgb.onnx"));
        Assert.Equal("weld_heatmap_ir.onnx", WeldModelNames.HeatmapExportName("ir"));
        Assert.Equal("rgb", WeldModelNames.ModalityOf("models/weld_heatmap_rgb.onnx"));
    }

    /// <summary>실제 학습 모델로 실사진 디코딩 확인 — 환경변수 HEATMAP_MODEL·HEATMAP_IMAGE 가 있을 때만.</summary>
    [Fact]
    public void 실제_모델_스모크()
    {
        var model = Environment.GetEnvironmentVariable("HEATMAP_MODEL");
        var image = Environment.GetEnvironmentVariable("HEATMAP_RGB_RAW");   // "경로|w|h" — RGB raw 바이트
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(image)) return;
        var parts = image.Split('|');
        int w = int.Parse(parts[1]), h = int.Parse(parts[2]);
        var rgb = File.ReadAllBytes(parts[0]);
        using var s = new InferenceSession(model);
        var m = HeatmapSegmenter.RidgeMask(s, rgb, w, h, w, true, 0.3f);
        var c = Centers(m, w, h, true);
        File.WriteAllText(parts[0] + ".centers.txt", string.Join("\n", c.Select(v => v.ToString("0.0"))));
        Assert.True(c.Count > w / 2);
    }
}
