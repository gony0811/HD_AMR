using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class CameraRoiParametersTests
{
    [Fact]
    public void 정규화_ROI_를_IR_해상도_픽셀로_변환()
    {
        // 원격 현장 DB 의 Peak ROI 값 → 로그에 찍힌 ROI=Peak ROI(27,50,793,339) 와 같아야 한다.
        var roi = new NormalizedRoi(0.03126571013306503, 0.10412144931297082, 0.9352353624121151, 0.7069976285192794);
        Assert.Equal(new RoiRect(27, 50, 793, 339), roi.ToPixel(848, 480));
    }

    [Fact]
    public void 픽셀_정규화_왕복은_1px_이내()
    {
        var px = new RoiRect(81, 9, 713, 422);
        var back = NormalizedRoi.FromPixel(px, 848, 480).ToPixel(848, 480);
        Assert.Equal(px, back);
    }

    [Fact]
    public void 화면_밖으로_나간_픽셀_ROI_는_경계로_자른다()
    {
        var n = NormalizedRoi.FromPixel(new RoiRect(800, 400, 200, 200), 848, 480);
        Assert.True(n.IsValid);
        Assert.True(n.X + n.W <= 1.0 && n.Y + n.H <= 1.0);
    }

    [Theory]
    [InlineData(0, 0, 0, 0.5)]        // 폭 0
    [InlineData(0.6, 0, 0.5, 0.5)]    // 오른쪽 경계 초과
    [InlineData(0, 0.6, 0.5, 0.5)]    // 아래 경계 초과
    public void 무효_ROI(double x, double y, double w, double h) =>
        Assert.False(new NormalizedRoi(x, y, w, h).IsValid);

    [Fact]
    public void 프레임_크기_0_이면_빈_영역() =>
        Assert.False(NormalizedRoi.FromPixel(new RoiRect(1, 1, 10, 10), 0, 0).IsValid);

    [Fact]
    public void 키_세트는_자기_키만_포함()
    {
        Assert.True(CameraRoiKeys.Bead.Contains("Camera.Bead.Roi.W"));
        Assert.False(CameraRoiKeys.Bead.Contains("Camera.Peak.Roi.W"));
        Assert.DoesNotContain("Camera.Bead.Roi.X", ParameterService.ObsoleteNames);
        Assert.Contains("Camera.Weld.Roi.X", ParameterService.ObsoleteNames);
    }
}
