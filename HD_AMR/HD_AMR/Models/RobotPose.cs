namespace HD_AMR.Models;

/// <summary>
/// 로봇 위치 및 각도 정보
/// </summary>
public record RobotPose(float X, float Y, float Angle)
{
    /// <summary>X 좌표 (meters)</summary>
    public float X { get; init; } = X;

    /// <summary>Y 좌표 (meters)</summary>
    public float Y { get; init; } = Y;

    /// <summary>각도 (radian)</summary>
    public float Angle { get; init; } = Angle;
}
