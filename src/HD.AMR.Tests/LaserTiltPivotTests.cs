using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class LaserTiltPivotTests
{
    // 10-28 현장 헤드 중심(툴 X 15.9, Y −148.7) · 스팟 깊이 343mm.
    private static readonly double[] Spot = { 15.9, -148.7, -343.2 };

    private static double[] Apply(double[] relInTool, double[] toolPose)
        => FrameMath.FromFrame(new[] { relInTool[0], relInTool[1], relInTool[2], 0, 0, 0 }, toolPose);

    // 회전 후에도 스팟(툴 좌표 점)이 베이스에서 같은 자리여야 한다 — 앵커 자세와 무관하게.
    [Theory]
    [InlineData(7.184, -4.203)]
    [InlineData(-9.485, -4.84)]
    [InlineData(10, 10)]
    public void SpotStaysFixedInBase(double rx, double ry)
    {
        var anchor = new[] { -1789.3, 784.0, -61.6, 150.94, 35.74, 133.1 };
        var offset = LaserTiltCorrector.PivotRotationOffset(rx, ry, Spot);
        var target = FrameMath.FromFrame(offset, anchor);

        var before = Apply(Spot, anchor);
        var after = Apply(Spot, target);
        for (var i = 0; i < 3; i++) Assert.Equal(before[i], after[i], 6);

        // 자세는 지령 회전만큼 바뀐다(위치 보상만 추가).
        Assert.Equal(rx, offset[3]);
        Assert.Equal(ry, offset[4]);
    }

    // 종전(TCP 축) 회전이면 7.2° 에 스팟이 수십 mm 움직였다 — 회귀 확인용 기준값.
    [Fact]
    public void TcpRotation_WouldMoveSpotTensOfMm()
    {
        var anchor = new[] { 0.0, 0, 0, 0, 0, 0 };
        var target = FrameMath.FromFrame(new[] { 0.0, 0, 0, 7.184, -4.203, 0 }, anchor);
        var a = Apply(Spot, anchor);
        var b = Apply(Spot, target);
        var moved = Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2) + Math.Pow(b[2] - a[2], 2));
        Assert.InRange(moved, 40, 60);
    }
}
