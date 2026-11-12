using HD.AMR.App.Communication.Vision;

namespace HD.AMR.Tests;

public class CaptureHeightTests
{
    [Theory]
    [InlineData(-12, 0)]    // CROSS-4-V1 교차 중심 4점
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(34, 34)]
    public void 촬영_요청의_음수_높이는_0으로_올려_보낸다(int raw, int expected)
        => Assert.Equal(expected, FaceLocalMapper.CaptureHeight(raw));
}
