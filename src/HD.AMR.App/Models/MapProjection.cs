namespace HD.AMR.App.Models;

/// <summary>Lower-left world origin (metres), image Y pointing down, CCW heading in radians.</summary>
public static class MapProjection
{
    public static bool TryProjectPoint(double worldX, double worldY, double resolution,
        double originX, double originY, int width, int height, out double x, out double y)
    {
        x = y = 0;
        if (!double.IsFinite(resolution) || resolution <= 0 || width <= 0 || height <= 0 ||
            !double.IsFinite(worldX) || !double.IsFinite(worldY) ||
            !double.IsFinite(originX) || !double.IsFinite(originY)) return false;
        x = (worldX - originX) / resolution;
        y = height - (worldY - originY) / resolution;
        return x >= 0 && x <= width && y >= 0 && y <= height;
    }

    public static bool TryProject(RobotPose pose, double resolution, double originX, double originY,
        int width, int height, out double x, out double y, out double headingDegrees)
    {
        x = y = headingDegrees = 0;
        if (!float.IsFinite(pose.Angle) ||
            !TryProjectPoint(pose.X, pose.Y, resolution, originX, originY, width, height, out x, out y)) return false;
        headingDegrees = -pose.Angle * 180 / Math.PI;
        return true;
    }
}
