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

/// <summary>파일명 → 모델 종류·모달리티 해석(용접 추적·시퀀스가 방식·입력 모드를 맞추는 근거).</summary>
public class WeldModelNameParsingTests
{
    [Theory]
    [InlineData("[4점제외]weld_seg_ir_261106_밝기증강off.onnx", "ir")]
    [InlineData("weld_seg_rgb_261106_밝기증강on.onnx", "rgb")]
    [InlineData("weld_yolo_ir.onnx", "ir")]
    [InlineData("weld_unet_RGB_1106.onnx", "rgb")]
    [InlineData("weld_seg_ir_1012.onnx", "ir")]
    [InlineData("weld_seg.onnx", null)]          // 모달리티 표식 없음 → 모드 유지
    [InlineData("weld_yolo_mirror.onnx", null)]  // 단어 속 'ir' 는 무시
    public void ModalityOf_ReadsIrRgbToken(string name, string? expected)
        => Assert.Equal(expected, WeldModelNames.ModalityOf(name));

    [Theory]
    [InlineData("weld_unet_ir_261106.onnx", WeldDetectionMethod.Dl, WeldDetectionMethod.Unet)]   // DL 칸에 넣어도 U-Net
    [InlineData("weld_seg_rgb_1106.onnx", WeldDetectionMethod.Unet, WeldDetectionMethod.Dl)]     // U-Net 칸에 넣어도 DL
    [InlineData("custom_model.onnx", WeldDetectionMethod.Unet, WeldDetectionMethod.Unet)]        // 표식 없으면 넣은 칸 기준
    public void KindOf_UsesNameTag_ElseImplied(string name, WeldDetectionMethod implied, WeldDetectionMethod expected)
        => Assert.Equal(expected, WeldModelNames.KindOf(name, implied));

    [Theory]
    [InlineData("ir", WeldImageMode.RgbHsv, WeldImageMode.Ir)]
    [InlineData("rgb", WeldImageMode.Ir, WeldImageMode.RgbGrayscale)]
    [InlineData("rgb", WeldImageMode.RgbHsv, WeldImageMode.RgbHsv)]   // RGB 계열 세부 선택은 유지
    public void ModeFor_MapsModality(string modality, WeldImageMode current, WeldImageMode expected)
        => Assert.Equal(expected, WeldModelNames.ModeFor(modality, current));

    // 현장 보고: 용접 추적 화면에서 히트맵 모델을 고를 수 없음 — 히트맵은 U-Net 칸 목록에 나와야 하고 YOLO 목록엔 없어야 한다.
    [Theory]
    [InlineData("weld_heatmap_ir.onnx")]
    [InlineData(@"D:\data\dl\models\[대각선]weld_heatmap_rgb_261108.onnx")]
    [InlineData("WELD_HEATMAP_ir_밝기증강.onnx")]
    public void HeatmapNames_AreListedInUnetSlot_NotYolo(string name)
    {
        Assert.True(WeldModelNames.IsHeatmap(name));
        Assert.True(WeldModelNames.IsUnet(name));
        Assert.False(WeldModelNames.IsYolo(name));
        Assert.Equal(WeldDetectionMethod.Unet, WeldModelNames.KindOf(name, WeldDetectionMethod.Dl));
        Assert.Contains("히트맵", WeldModelNames.DisplayLabel(name));
        Assert.Contains(Path.GetFileName(name), WeldModelNames.DisplayLabel(name));
    }

    [Fact]
    public void DisplayLabel_NonHeatmap_IsFileName()
    {
        Assert.Equal("weld_unet_rgb_261106.onnx", WeldModelNames.DisplayLabel(@"C:\m\weld_unet_rgb_261106.onnx"));
        Assert.Equal("weld_yolo_ir.onnx", WeldModelNames.DisplayLabel("weld_yolo_ir.onnx"));
        Assert.False(WeldModelNames.IsHeatmap("weld_unet_ir.onnx"));
    }

    [Fact]
    public void EmptyListHint_NamesFolderAndTags()
    {
        var unet = WeldModelNames.EmptyListHint(WeldDetectionMethod.Unet, @"C:\source\dl\models");
        Assert.Contains(@"C:\source\dl\models", unet);
        Assert.Contains(WeldModelNames.HeatmapTag, unet);
        Assert.Contains(WeldModelNames.UnetTag, unet);

        var yolo = WeldModelNames.EmptyListHint(WeldDetectionMethod.Dl, null);
        Assert.Contains(WeldModelNames.YoloTag, yolo);
        Assert.Contains("미설정", yolo);
        Assert.Contains("히트맵", yolo);   // 히트맵은 U-Net·히트맵 방식에서 고른다는 안내
    }
}
