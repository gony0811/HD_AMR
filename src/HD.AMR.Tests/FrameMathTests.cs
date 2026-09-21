using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary><see cref="FrameMath"/> 의 회전각·거리 보조 함수 — 도달 판정·캡처 중 회전 검사·표본 자세 비교가 의존한다.</summary>
public class FrameMathTests
{
    [Fact]
    public void RelativeRotationDeg_IsZeroForSamePose_AndIgnoresTranslation()
    {
        double[] a = [100, 200, 300, 10, -20, 30];
        double[] b = [500, -200, 900, 10, -20, 30];
        Assert.True(FrameMath.RelativeRotationDeg(a, a) < 1e-9);
        Assert.True(FrameMath.RelativeRotationDeg(a, b) < 1e-9);
        Assert.True(Math.Abs(FrameMath.DistanceMm(a, b) - Math.Sqrt(400.0 * 400 + 400 * 400 + 600 * 600)) < 1e-9);
    }

    [Theory]
    [InlineData(0, 0, 25)]
    [InlineData(15, 0, 0)]
    [InlineData(0, -12.5, 0)]
    public void RelativeRotationDeg_MatchesSingleAxisRotation(double rx, double ry, double rz)
    {
        double[] a = [0, 0, 0, 178, 3, -40];
        var b = FrameMath.MatrixToPose(FrameMath.Multiply(FrameMath.PoseToMatrix(a), FrameMath.PoseToMatrix([0, 0, 0, rx, ry, rz])));
        double expected = Math.Abs(rx) + Math.Abs(ry) + Math.Abs(rz);
        Assert.True(Math.Abs(FrameMath.RelativeRotationDeg(a, b) - expected) < 1e-6);
        Assert.True(Math.Abs(FrameMath.RelativeRotationDeg(b, a) - expected) < 1e-6);
    }

    [Fact]
    public void RotationAngleDeg_HandlesHalfTurn()
    {
        var m = FrameMath.PoseToMatrix([0, 0, 0, 0, 0, 180]);
        Assert.True(Math.Abs(FrameMath.RotationAngleDeg(m) - 180) < 1e-6);
    }
}
