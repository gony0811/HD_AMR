using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

public class InternalWorkpieceFrameTests
{
    [Fact]
    public void Compute_TopAndXDirection_SetsFloorOriginBelowTop()
    {
        var top = new[] { 100.0, 200.0, 300.0, 0.0, 0.0, 0.0 };
        var xPoint = new[] { 460.0, 200.0, 300.0, 0.0, 0.0, 0.0 };

        var frame = InternalWorkpieceFrame.Compute(top, xPoint);

        Assert.Equal(100.0, frame[0], 6);
        Assert.Equal(200.0, frame[1], 6);
        Assert.Equal(266.2, frame[2], 6);
        Assert.Equal(0.0, frame[3], 6);
        Assert.Equal(0.0, frame[4], 6);
        Assert.Equal(0.0, frame[5], 6);
    }

    [Fact]
    public void Compute_UsesFirstPoseToolZ_NotSecondPoseAttitude()
    {
        var top = new[] { 10.0, 20.0, 30.0, 0.0, 90.0, 0.0 };
        var tool = FrameMath.PoseToMatrix(top);
        var x = new[] { tool[0, 0], tool[1, 0], tool[2, 0] };
        var xPoint = new[]
        {
            top[0] + 360 * x[0], top[1] + 360 * x[1], top[2] + 360 * x[2],
            45.0, -20.0, 120.0,
        };

        var frame = InternalWorkpieceFrame.Compute(top, xPoint);
        var expectedOrigin = FrameMath.FromFrame(
            new[] { 0.0, 0.0, -33.8, 0.0, 0.0, 0.0 }, top);

        Assert.Equal(expectedOrigin[0], frame[0], 6);
        Assert.Equal(expectedOrigin[1], frame[1], 6);
        Assert.Equal(expectedOrigin[2], frame[2], 6);
    }

    [Fact]
    public void ToBasePose_AppliesInternalFrameWithoutControllerUserFrame()
    {
        var frame = new[] { 100.0, 200.0, 300.0, 0.0, 0.0, 90.0 };
        var target = InternalWorkpieceFrame.ToBasePose(
            new[] { 360.0, 10.0, 33.8, 0.0, 0.0, 0.0 }, frame);

        Assert.Equal(90.0, target[0], 6);
        Assert.Equal(560.0, target[1], 6);
        Assert.Equal(333.8, target[2], 6);
        Assert.Equal(90.0, target[5], 6);
    }

    [Fact]
    public void Compute_RejectsCoincidentTeachingPoints()
    {
        var top = new[] { 100.0, 200.0, 300.0, 0.0, 0.0, 0.0 };
        var ex = Assert.Throws<InvalidOperationException>(() => InternalWorkpieceFrame.Compute(top, top));
        Assert.Contains("가깝", ex.Message);
    }
}
