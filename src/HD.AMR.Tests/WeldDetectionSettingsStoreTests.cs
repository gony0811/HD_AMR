using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class WeldDetectionSettingsStoreTests
{
    [Fact]
    public void RoundTrip_RestoresMethodAndRidgeParams()
    {
        var src = new WeldDetectionParams
        {
            RidgeSigmaMin = 1.0, RidgeSigmaMax = 2.5, RidgeScales = 2, RidgePolarity = WeldRidgePolarity.Dark,
            RidgeMaxAngleDeg = 15, RidgeThresholdK = 4.5, RidgeRelative = 0.35,
        };
        var stored = WeldDetectionSettingsStore.ToValues(WeldDetectionMethod.Ridge, src).ToDictionary(kv => kv.Key, kv => kv.Value);

        var dst = new WeldDetectionParams();
        var method = WeldDetectionSettingsStore.Apply(k => stored.GetValueOrDefault(k), dst);

        Assert.Equal(WeldDetectionMethod.Ridge, method);
        Assert.Equal(1.0, dst.RidgeSigmaMin);
        Assert.Equal(2.5, dst.RidgeSigmaMax);
        Assert.Equal(2, dst.RidgeScales);
        Assert.Equal(WeldRidgePolarity.Dark, dst.RidgePolarity);
        Assert.Equal(15, dst.RidgeMaxAngleDeg);
        Assert.Equal(4.5, dst.RidgeThresholdK);
        Assert.Equal(0.35, dst.RidgeRelative);
    }

    [Fact]
    public void Apply_MissingOrInvalidValues_KeepDefaults()
    {
        var stored = new Dictionary<string, string?>
        {
            [WeldDetectionSettingsStore.MethodKey] = "999",
            [WeldDetectionSettingsStore.RidgeSigmaMinKey] = "abc",
            [WeldDetectionSettingsStore.RidgePolarityKey] = "Sideways",
        };
        var p = new WeldDetectionParams();
        var method = WeldDetectionSettingsStore.Apply(k => stored.GetValueOrDefault(k), p);

        var d = new WeldDetectionParams();
        Assert.Null(method);
        Assert.Equal(d.RidgeSigmaMin, p.RidgeSigmaMin);
        Assert.Equal(d.RidgeSigmaMax, p.RidgeSigmaMax);
        Assert.Equal(d.RidgePolarity, p.RidgePolarity);
    }

    [Theory]
    [InlineData(@"C:\models\ir_261106_1.onnx", @"C:\models\ir_261106_1.onnx")]
    [InlineData("  \"ir_261106_1.onnx\" ", "ir_261106_1.onnx")]
    [InlineData("", "")]
    public void Apply_RestoresDlModelPath_Trimmed(string stored, string expected)
    {
        var p = new WeldDetectionParams { DlModelPath = "previous.onnx" };
        WeldDetectionSettingsStore.Apply(
            k => k == WeldDetectionSettingsStore.DlModelPathKey ? stored : null, p);
        Assert.Equal(expected, p.DlModelPath);
    }

    [Fact]
    public void Apply_MissingDlModelPathKey_KeepsCurrentPath()
    {
        var p = new WeldDetectionParams { DlModelPath = "keep.onnx" };
        WeldDetectionSettingsStore.Apply(_ => null, p);
        Assert.Equal("keep.onnx", p.DlModelPath);
    }

    [Fact]
    public void DlModelPathKey_IsWatched_AndSharedWithSequence()
    {
        Assert.True(WeldDetectionSettingsStore.IsKey("Weld.Dl.ModelPath"));
        Assert.Equal(WeldDetectionSettingsStore.DlModelPathKey,
            HD.AMR.App.Service.Sequence.Steps.WeldSequenceSupport.DlModelPathKey);
    }

    [Fact]
    public void Apply_ClampsOutOfRange_AndKeepsSigmaOrder()
    {
        var stored = new Dictionary<string, string?>
        {
            [WeldDetectionSettingsStore.RidgeSigmaMinKey] = "3",
            [WeldDetectionSettingsStore.RidgeSigmaMaxKey] = "1",
            [WeldDetectionSettingsStore.RidgeScalesKey] = "40",
            [WeldDetectionSettingsStore.RidgeRelativeKey] = "1.7",
            [WeldDetectionSettingsStore.RidgeMaxAngleDegKey] = "-10",
        };
        var p = new WeldDetectionParams();
        WeldDetectionSettingsStore.Apply(k => stored.GetValueOrDefault(k), p);

        Assert.Equal(3, p.RidgeSigmaMin);
        Assert.Equal(3, p.RidgeSigmaMax);
        Assert.Equal(6, p.RidgeScales);
        Assert.Equal(1, p.RidgeRelative);
        Assert.Equal(0, p.RidgeMaxAngleDeg);
    }
}
