using HD.AMR.App.Service.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace HD.AMR.Tests;

/// <summary>학습 화면 ④ 추론 탭의 히트맵 모델 해석(OpenCV 불필요 — macOS 포함).</summary>
public class HeatmapInferTabTests
{
    private static OnnxBeadSegmentationService Svc() => new(null!, NullLogger<OnnxBeadSegmentationService>.Instance);

    private static byte[] Png(int w, int h, Func<int, int, byte> px)
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { var v = px(x, y); img[x, y] = new(v, v, v); }
        using var ms = new MemoryStream();
        img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        return ms.ToArray();
    }

    private static string Model() => Path.Combine(AppContext.BaseDirectory, "TestData", "threshold_heatmap.onnx");

    [Fact]
    public async Task 가로선_영상은_검출_1건과_오버레이를_만든다()
    {
        var svc = Svc();
        var r = await svc.RunOnBytesAsync(Model(), Png(320, 160, (_, y) => (byte)(Math.Abs(y - 70) <= 2 ? 230 : 40)), 0.25f, 0.5f);
        Assert.Equal(1, r.Count);
        Assert.True(r.Coverage > 0.9);
        Assert.True(r.MaxScore > 0.9f);
        Assert.NotNull(svc.LastOverlay);
    }

    [Fact]
    public async Task 세로선_영상도_자동으로_전치해서_찾는다()
    {
        var r = await Svc().RunOnBytesAsync(Model(), Png(320, 160, (x, _) => (byte)(Math.Abs(x - 200) <= 2 ? 230 : 40)), 0.25f, 0.5f);
        Assert.Equal(1, r.Count);
        Assert.True(r.Coverage > 0.9);
    }

    [Fact]
    public async Task 선이_없으면_검출_0건()
    {
        var r = await Svc().RunOnBytesAsync(Model(), Png(160, 96, (_, _) => 40), 0.25f, 0.5f);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void 슬라이스_비율()
    {
        var m = new byte[4 * 3];
        m[1 * 4 + 0] = 255; m[2 * 4 + 1] = 255;   // 열 0·1 에만
        Assert.Equal(0.5, OnnxBeadSegmentationService.SliceFraction(m, 4, 3, true));
        Assert.Equal(2 / 3.0, OnnxBeadSegmentationService.SliceFraction(m, 4, 3, false), 6);
    }

    /// <summary>실제 학습 모델 스모크 — HEATMAP_MODEL·HEATMAP_IMAGE 가 있을 때만(오버레이는 HEATMAP_IMAGE.overlay.jpg).</summary>
    [Fact]
    public async Task 실제_모델_스모크()
    {
        var model = Environment.GetEnvironmentVariable("HEATMAP_MODEL");
        var image = Environment.GetEnvironmentVariable("HEATMAP_IMAGE");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(image)) return;
        var svc = Svc();
        var r = await svc.RunOnBytesAsync(model, File.ReadAllBytes(image), 0.25f, 0.5f);
        File.WriteAllBytes(Environment.GetEnvironmentVariable("HEATMAP_OUT") ?? image + ".overlay.jpg", svc.LastOverlay!);
        Assert.Equal(1, r.Count);
    }
}
