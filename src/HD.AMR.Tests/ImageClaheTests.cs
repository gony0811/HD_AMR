using HD.AMR.App.Service.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.Tests;

/// <summary>CLAHE 전처리(학습·추론 공용 구현). OpenCV 결과와의 비교(±1)는 실제 IR 영상으로 별도 확인함.</summary>
public class ImageClaheTests
{
    private static byte[] Ramp(int w, int h)
    {
        var g = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) g[y * w + x] = (byte)(60 + (x + y) % 40);   // 좁은 밝기 범위(저대비)
        return g;
    }

    [Fact]
    public void 저대비_영상의_대비를_넓힌다()
    {
        var g = Ramp(160, 96);
        var o = ImageClahe.ApplyGray(g, 160, 96);
        Assert.True(o.Max() - o.Min() > g.Max() - g.Min());
    }

    [Fact]
    public void 균일_영상은_균일하게_유지()
    {
        var g = Enumerable.Repeat((byte)120, 64 * 48).ToArray();
        var o = ImageClahe.ApplyGray(g, 64, 48);
        Assert.Single(o.Distinct());
    }

    [Theory]
    [InlineData(160, 96)]   // 타일로 나누어떨어짐
    [InlineData(157, 93)]   // 나누어떨어지지 않음 → 반사 패딩
    public void 크기와_결정성(int w, int h)
    {
        var g = Ramp(w, h);
        var a = ImageClahe.ApplyGray(g, w, h);
        Assert.Equal(w * h, a.Length);
        Assert.Equal(a, ImageClahe.ApplyGray(g, w, h));
    }

    [Fact]
    public void 흑백을_컬러로_넣어도_흑백_결과와_같다()
    {
        const int w = 96, h = 64;
        var g = Ramp(w, h);
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < g.Length; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = g[i];
        ImageClahe.ApplyColorInPlace(rgb, w, h, 3, bgr: false);
        var gray = ImageClahe.ApplyGray(g, w, h);
        for (int i = 0; i < g.Length; i++)
        {
            Assert.Equal(gray[i], rgb[i * 3]);
            Assert.Equal(rgb[i * 3], rgb[i * 3 + 2]);   // 색이 생기지 않음
        }
    }

    [Fact]
    public void 흑백_PNG는_흑백_PNG로_돌려준다()
    {
        const int w = 64, h = 48;
        using var img = Image.LoadPixelData<L8>(Ramp(w, h), w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        var outPng = ImageClahe.ApplyEncoded(ms.ToArray());
        using var back = Image.Load<L8>(outPng);
        var px = new byte[w * h]; back.CopyPixelDataTo(px);
        Assert.Equal(ImageClahe.ApplyGray(Ramp(w, h), w, h), px);
        Assert.Equal(SixLabors.ImageSharp.Formats.Png.PngColorType.Grayscale,
            Image.Identify(outPng).Metadata.GetPngMetadata().ColorType);
    }

    [Theory]
    [InlineData("weld_yolo_ir_line_clahe_261108_1430.onnx", true)]
    [InlineData("[메모]weld_unet_rgb_area_clahe_261108_1430.onnx", true)]
    [InlineData("weld_yolo_ir_line_261108_1430.onnx", false)]
    [InlineData("weld_yolo_ir_noclahex.onnx", false)]
    public void 모델_이름의_clahe_표식(string name, bool expected)
        => Assert.Equal(expected, WeldModelNames.HasClahe(name));

    [Fact]
    public void 내보내기_이름에_clahe()
    {
        var t = new DateTime(2026, 11, 8, 14, 30, 0);
        Assert.Equal("weld_yolo_ir_line_clahe_261108_1430.onnx", WeldModelNames.YoloExportName("ir", LabelKind.Line, t, clahe: true));
        Assert.Equal("weld_unet_rgb_area_261108_1430.onnx", WeldModelNames.UnetExportName("rgb", LabelKind.Area, t));
    }
}
