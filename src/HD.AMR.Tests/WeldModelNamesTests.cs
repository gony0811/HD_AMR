using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Vision;

namespace HD.AMR.Tests;

/// <summary>모델 파일명 규칙 — 날짜·특징을 덧붙인 이름도 종류(YOLO/U-Net)가 맞게 분류되어야 한다.</summary>
public class WeldModelNamesTests
{
    [Theory]
    // 현장에서 실제로 쓰는 기존 YOLO 이름(weld_seg) — 계속 YOLO 로 인식
    [InlineData("[4점제외]weld_seg_ir_261106_밝기증강off.onnx")]
    [InlineData("weld_seg_ir_1012.onnx")]
    [InlineData("weld_seg_rgb_261106_밝기증강on.onnx")]
    [InlineData("weld_seg.onnx")]
    // 새 이름(weld_yolo)
    [InlineData("weld_yolo_ir.onnx")]
    [InlineData("[테스트]weld_yolo_rgb_261107.onnx")]
    public void YoloNames_AreYolo_NotUnet(string name)
    {
        Assert.True(WeldModelNames.IsYolo(name));
        Assert.False(WeldModelNames.IsUnet(name));
    }

    [Theory]
    [InlineData("weld_unet_ir.onnx")]
    [InlineData("weld_unet_ir_261106.onnx")]
    [InlineData("[4점제외]WELD_UNET_rgb_밝기증강on.onnx")]
    public void UnetNames_AreUnet_NotYolo(string name)
    {
        Assert.True(WeldModelNames.IsUnet(name));
        Assert.False(WeldModelNames.IsYolo(name));
    }

    [Fact]
    public void YoloRunOutput_IsYolo_UnrelatedFile_IsNeither()
    {
        Assert.True(WeldModelNames.IsYolo("/data/dl/runs/hd/weights/best.onnx"));
        Assert.True(WeldModelNames.IsYolo(@"C:\data\dl\runs\hd\weights\best.onnx"));
        Assert.False(WeldModelNames.IsYolo("/data/dl/models/other.onnx"));
        Assert.False(WeldModelNames.IsUnet("/data/dl/models/other.onnx"));
    }

    [Fact]
    public void ExportNames_And_AutoFallbackOrder()
    {
        Assert.Equal("weld_yolo_ir.onnx", WeldModelNames.YoloExportName("ir"));
        Assert.Equal("weld_unet_rgb.onnx", WeldModelNames.UnetExportName("rgb"));
        // 자동: 새 이름 → 기존 이름 → 레거시
        Assert.Equal(["weld_yolo_rgb.onnx", "weld_seg_rgb.onnx", "weld_seg.onnx"], WeldModelNames.YoloAutoCandidates("rgb"));
    }

    [Fact]
    public void UnetModelPath_IsRestoredFromParameter_Trimmed()
    {
        var p = new WeldDetectionParams();
        WeldDetectionSettingsStore.Apply(
            k => k == WeldDetectionSettingsStore.UnetModelPathKey ? "  \"weld_unet_ir_261106.onnx\" " : null, p);
        Assert.Equal("weld_unet_ir_261106.onnx", p.UnetModelPath);
        Assert.True(WeldDetectionSettingsStore.IsKey(WeldDetectionSettingsStore.UnetModelPathKey));
    }
}
