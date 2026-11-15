using HD.AMR.App.Models;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 정차 한 점의 도달 판정을 시퀀스 진행 여부로 바꾼다.
/// 정차점 셀이 쓸 수 있으면 진행. 쓸 수 없어도 사유가 <see cref="ReachStatus.FlangeShort"/> 이고
/// 허락된 최대 후진 자리의 셀은 쓸 수 있으면 진행 — 그 경우만 기존 정차 보정(후진)이 고친다.
/// IK 실패·자세 한계·간섭·가지 전환은 후진 보정이 다루지 않으므로 후진 자리가 닿아도 막는다.
/// </summary>
public static class ReachPrecheckPolicy
{
    public static bool Allows(bool nodeUsable, ReachStatus nodeStatus, bool? backupUsable)
    {
        if (nodeUsable) return true;
        return nodeStatus == ReachStatus.FlangeShort && backupUsable == true;
    }
}

/// <summary>맵 좌표 용접선 점을, 그 정차에 선 AMR 의 차체 좌표(mm)로 옮긴다. 리치 맵 격자와 같은 축이다.</summary>
public static class ReachStationFrame
{
    /// <summary>
    /// seam(m, 맵) → 차체 mm. yaw 0·원점이면 맵 xy 가 그대로 전방·좌측이다.
    /// z 는 층 바닥 기준에서 <paramref name="zDatumOffsetMm"/> 를 뺀 값.
    /// </summary>
    public static double[] WorldToBodyMm(double[] seamWMeters, double amrXm, double amrYm, double amrYawRad, double zDatumOffsetMm)
    {
        var seamMap = new[]
        {
            seamWMeters[0] * 1000.0,
            seamWMeters[1] * 1000.0,
            seamWMeters[2] * 1000.0 - zDatumOffsetMm,
        };
        var amrPose = MapCalibration.AmrPoseToMmDeg(amrXm, amrYm, amrYawRad);
        return MapCalibration.MapPointToBase(amrPose, new double[6], seamMap);
    }

    /// <summary>리치 맵이 실제로 평가하는 높이. 바닥은 z 격자를 쓰지 않고 0 이다.</summary>
    public static double CellZMm(string? wallCode, double bodyZmm)
        => WallCodes.Find(wallCode)?.Orientation == SurfaceOrientation.Floor ? 0 : bodyZmm;
}
