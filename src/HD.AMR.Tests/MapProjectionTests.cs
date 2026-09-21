using HD.AMR.App.Models;
using Xunit;

namespace HD.AMR.Tests;

public class MapProjectionTests
{
    [Fact]
    public void ConvertsMetresToPixelsAndFlipsImageY()
    {
        Assert.True(MapProjection.TryProject(new RobotPose(3, 5, MathF.PI / 2), .05, 1, 2,
            307, 427, out var x, out var y, out var heading));
        Assert.Equal(40, x, 5);
        Assert.Equal(367, y, 5);
        Assert.Equal(-90, heading, 4);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-.05, 1, 1)]
    [InlineData(.05, -1, 1)]
    [InlineData(.05, 100, 1)]
    [InlineData(.05, 1, 100)]
    [InlineData(.05, float.NaN, 1)]
    public void RejectsInvalidScaleOrPosition(double scale, float px, float py)
    {
        Assert.False(MapProjection.TryProject(new RobotPose(px, py, 0), scale, 0, 0,
            307, 427, out _, out _, out _));
    }

    [Fact]
    public void LowerLeftOriginLandsOnBottomOfImage()
    {
        Assert.True(MapProjection.TryProject(new RobotPose(0, 0, 0), .05, 0, 0,
            307, 427, out var x, out var y, out _));
        Assert.Equal(0, x);
        Assert.Equal(427, y);
    }

    [Fact]
    public void ProjectsLidarWorldPointUsingSameMapFrame()
    {
        Assert.True(MapProjection.TryProjectPoint(4.3896208, 12.2516994, .05, 0, 0,
            307, 427, out var x, out var y));
        Assert.Equal(87.792416, x, 5);
        Assert.Equal(181.966012, y, 5);
    }
}
