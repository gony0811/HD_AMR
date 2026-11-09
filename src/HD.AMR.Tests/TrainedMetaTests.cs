using HD.AMR.App.Service.Vision;

namespace HD.AMR.Tests;

public class TrainedMetaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdamr_meta_" + Guid.NewGuid().ToString("N"));

    public TrainedMetaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("001_20261105_095725_ir.png", "ir")]
    [InlineData("001_20261105_095725_cw90_rgb.png", "rgb")]
    public void 데이터셋_모달리티는_이미지_파일명으로(string file, string expected)
    {
        var imgs = Path.Combine(_dir, "images", "train");
        Directory.CreateDirectory(imgs);
        File.WriteAllBytes(Path.Combine(imgs, file), Array.Empty<byte>());
        Assert.Equal(expected, WeldTrainingService.DatasetModality(_dir));
    }

    [Fact]
    public void 이미지가_없으면_모달리티_null()
        => Assert.Null(WeldTrainingService.DatasetModality(_dir));

    [Fact]
    public void 학습_기록_읽기()
    {
        File.WriteAllLines(Path.Combine(_dir, WeldTrainingService.TrainedMetaMarker), new[] { "modality=rgb", "size=1280x720" });
        var (mod, size) = WeldTrainingService.ReadTrainedMeta(_dir);
        Assert.Equal("rgb", mod);
        Assert.Equal((1280, 720), size);
    }

    [Fact]
    public void 기록이_없거나_값이_이상하면_null()
    {
        Assert.Equal((null, null), WeldTrainingService.ReadTrainedMeta(_dir));
        File.WriteAllLines(Path.Combine(_dir, WeldTrainingService.TrainedMetaMarker), new[] { "modality=xyz", "size=abc" });
        Assert.Equal((null, null), WeldTrainingService.ReadTrainedMeta(_dir));
    }
}
