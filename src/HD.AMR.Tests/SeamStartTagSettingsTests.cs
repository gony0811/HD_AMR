using HD.AMR.App.Service;
using OpenCvSharp.Aruco;

namespace HD.AMR.Tests;

public class SeamStartTagSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("없는사전")]
    public void 비었거나_모르는_값이면_AprilTag_36h11(string? raw) =>
        Assert.Equal(PredefinedDictionaryName.DictAprilTag_36h11, SeamStartTagSettings.ParseDictionary(raw));

    [Theory]
    [InlineData("DictAprilTag_36h11", PredefinedDictionaryName.DictAprilTag_36h11)]
    [InlineData("36h11", PredefinedDictionaryName.DictAprilTag_36h11)]
    [InlineData("AprilTag_25h9", PredefinedDictionaryName.DictAprilTag_25h9)]
    [InlineData("Dict4X4_50", PredefinedDictionaryName.Dict4X4_50)]
    public void 사전_이름_해석(string raw, PredefinedDictionaryName expected) =>
        Assert.Equal(expected, SeamStartTagSettings.ParseDictionary(raw));

    [Fact]
    public void 표기는_사전에_따라_AprilTag_또는_ArUco()
    {
        Assert.Equal("AprilTag", new SeamStartTagSettings(PredefinedDictionaryName.DictAprilTag_36h11, null, 100).Label);
        Assert.Equal("ArUco", new SeamStartTagSettings(PredefinedDictionaryName.Dict4X4_50, null, 100).Label);
    }

    [Fact]
    public void 결과_요약에_태그_종류가_붙는다()
    {
        var ok = new ArucoCenterOffset(true, "AprilTag", 3, 12.3, -4.5, 401, 10, -3, 0.4, 5, 5, "3", null);
        Assert.StartsWith("AprilTag#3 중심편차", ok.Summary());
        Assert.StartsWith("AprilTag 미검출", ArucoCenterOffset.Fail("태그 미검출").Summary());
    }
}
