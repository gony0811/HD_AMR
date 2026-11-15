using HD.AMR.App.Service.Vision;

namespace HD.AMR.Tests;

public class RectExportSizeTests
{
    [Theory]
    [InlineData(864, 848, 480, 512, 864)]   // IR 848×480 → 864×489 레터박스 → 32 배수 512
    [InlineData(640, 848, 480, 384, 640)]
    [InlineData(864, 480, 848, 864, 512)]   // 세로 영상(90° 회전 증강)
    [InlineData(640, 640, 640, 640, 640)]   // 정사각은 그대로
    public void 데이터_비율로_32배수_직사각(int size, int w, int h, int eh, int ew)
    {
        var (H, W) = WeldTrainingService.RectExportSize(size, w, h);
        Assert.Equal(eh, H);
        Assert.Equal(ew, W);
        Assert.Equal(0, H % 32);
        Assert.Equal(0, W % 32);
    }
}
