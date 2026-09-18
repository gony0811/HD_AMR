namespace HD.AMR.App.Models;

/// <summary>Lower-left world origin (metres), image Y pointing down, CCW heading in radians.</summary>
public static class MapProjection
{
    public static bool TryProject(RobotPose pose, double resolution, double originX, double originY,
        int width, int height, out double x, out double y, out double headingDegrees)
    {
        x = y = headingDegrees = 0;
        if (!double.IsFinite(resolution) || resolution <= 0 || width <= 0 || height <= 0 ||
            !double.IsFinite(originX) || !double.IsFinite(originY) ||
            !float.IsFinite(pose.X) || !float.IsFinite(pose.Y) || !float.IsFinite(pose.Angle)) return false;
        x = (pose.X - originX) / resolution;
        y = height - (pose.Y - originY) / resolution;
        headingDegrees = -pose.Angle * 180 / Math.PI;
        return x >= 0 && x <= width && y >= 0 && y <= height;
    }
}
