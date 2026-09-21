namespace HD.AMR.App.Models;

/// <summary>한 번의 ArUco 장착 보정 관측. 모든 길이는 mm, 각도는 deg이다.</summary>
public sealed record ArucoMountSample(
    int Index,
    DateTime CapturedAtUtc,
    double[] AmrPoseWA,
    double[] ToolPoseBT,
    double[] CameraPoseCQ,
    double ReprojectionErrorPx);

/// <summary>미지의 T_A_B와 바닥 마커 T_W_Q(x,y,yaw)를 동시에 추정한 결과.</summary>
public sealed record ArucoMountCalibrationResult(
    bool Success,
    string? Error,
    double[] MountPoseAB,
    double[] MarkerPoseWQ,
    double TranslationRmsMm,
    double RotationRmsDeg,
    double MaxTranslationMm,
    double YawSpanDeg,
    double PositionSpanMm,
    int SampleCount,
    IReadOnlyList<string> Warnings)
{
    public static ArucoMountCalibrationResult Fail(string error) =>
        new(false, error, new double[6], new double[6], 0, 0, 0, 0, 0, 0, Array.Empty<string>());
}
