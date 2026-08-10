using HD_AMR.Contracts.Lidar;
using HD_AMR.Models;

namespace HD_AMR.Communication;

/// <summary>
/// 젯슨이 돌려준 센서 좌표계 능선을 로봇 좌표계로 옮긴다.
///
/// <b>역할 분담.</b> 젯슨은 로봇 기구학을 전혀 모르고 센서 좌표계 값만 낸다. 코봇 자세와
/// hand-eye 캘리브레이션을 아는 것은 이쪽뿐이므로 변환도 전적으로 이쪽 책임이다. 측정이
/// 정지 상태에서 이뤄지기 때문에 이 분담이 성립한다 — 캡처 시각과 로봇 자세 시각을 정합할
/// 필요가 없다.
///
/// 변환 사슬: <c>T_base←sensor = T_base←tool · T_tool←sensor</c>
///
/// 포즈 규약은 <see cref="PoseMath"/> 와 같은 ZYX RPY(고정축), mm + 도다. 코봇에서 받은
/// 자세를 그대로 넣을 수 있어야 하므로 규약이 어긋나면 안 된다.
///
/// ⚠ <b>센서 광학 좌표계는 통상적인 카메라 규약이 아니다.</b> NSL-1110AV 는 이미저가 본체 대비
///   90° 회전 장착되어 열 증가 = +X, 행 증가 = +Y, 광축 전방 = +Z 다. 이 90°까지 포함해
///   <see cref="LidarVisionSettings"/> 의 장착 회전값이 정해져야 한다. 틀려도 결과는 여전히
///   그럴듯한 mm 값이라 눈으로는 잡히지 않는다.
/// </summary>
public static class LidarRidgeTransform
{
    /// <summary>
    /// 측정 응답을 로봇 <b>베이스</b> 좌표계로 변환한다.
    ///
    /// 검출 실패는 실패로 전달한다 — 응답의 <c>valid=false</c> 를 여기서 흡수하면 호출 측이
    /// 실패를 모른 채 좌표를 쓰게 된다.
    /// </summary>
    /// <param name="response">젯슨 측정 응답.</param>
    /// <param name="settings">hand-eye 장착 변환이 담긴 설정.</param>
    /// <param name="toolPoseInBase">
    /// 측정 시점의 코봇 자세 [x,y,z,rx,ry,rz](mm + 도), 베이스 기준.
    ///
    /// ⚠ 이 포즈의 기준 프레임과 <see cref="LidarVisionSettings"/> 의 장착 오프셋 기준 프레임이
    ///   <b>같아야 한다.</b> 컨트롤러에 TCP 가 설정되어 있으면 FK 출력이 플랜지가 아니라 툴
    ///   기준일 수 있는데, 그때 장착 오프셋을 플랜지 기준으로 재놨다면 TCP 만큼 어긋난다.
    /// </param>
    public static RidgePose ToBaseFrame(
        MeasureResponse response, LidarVisionSettings settings, double[] toolPoseInBase)
    {
        var tool = ToToolFrame(response, settings);
        if (!tool.Valid) return tool with { Frame = LidarFrame.RobotBase };

        if (toolPoseInBase is not { Length: >= 6 })
            return RidgePose.Invalid(LidarFrame.RobotBase, "코봇 자세가 [x,y,z,rx,ry,rz] 6개가 아니다.");

        var baseFromTool = PoseMath.FromPose(toolPoseInBase);

        return tool with
        {
            Frame = LidarFrame.RobotBase,
            Ridge = Apply(baseFromTool, tool.Ridge!),
        };
    }

    /// <summary>
    /// 측정 응답을 코봇 <b>툴</b> 좌표계로 변환한다. 로봇 자세가 필요 없으므로, 아직 코봇에
    /// 연결되지 않은 상태에서도 장착 오프셋이 맞는지 확인할 수 있다.
    /// </summary>
    public static RidgePose ToToolFrame(MeasureResponse response, LidarVisionSettings settings)
    {
        if (!response.Valid || response.Ridge is null)
        {
            return RidgePose.Invalid(LidarFrame.ToolFlange,
                response.FailureDetail ?? $"측정 실패({response.Failure}).");
        }

        // 젯슨은 항상 센서 좌표계로 낸다. 다른 값이 왔다면 서비스 쪽이 계약을 깬 것이고,
        // 그대로 변환하면 이미 변환된 좌표에 변환을 한 번 더 거는 셈이 된다.
        if (response.Frame != LidarFrame.SensorOptical)
        {
            return RidgePose.Invalid(LidarFrame.ToolFlange,
                $"응답 좌표계가 {response.Frame} 다. SensorOptical 을 기대했다.");
        }

        if (!settings.HandEyeCalibrated)
        {
            return RidgePose.Invalid(LidarFrame.ToolFlange,
                "hand-eye 캘리브레이션이 완료되지 않았다(HandEyeCalibrated=false). " +
                "장착 오프셋 없이 변환하면 센서 좌표를 툴 좌표로 착각한 값이 나온다.");
        }

        var toolFromSensor = PoseMath.FromPose(settings.ToMountPose());

        return new RidgePose(
            LidarFrame.ToolFlange,
            Apply(toolFromSensor, response.Ridge),
            response.Confidence,
            Valid: true,
            Note: null);
    }

    /// <summary>
    /// 능선에 강체 변환을 적용한다.
    ///
    /// <b>점과 방향의 처리가 다르다.</b> 점은 회전 후 평행이동까지 받지만 방향 벡터는 회전만
    /// 받는다. 방향에 평행이동을 함께 걸면 원점에서 멀어질수록 방향이 크게 틀어지는데,
    /// 가까운 거리에서는 그럴듯한 값이 나와서 실장비에서야 드러난다.
    ///
    /// <see cref="RidgeLine.LengthMm"/> 은 강체 변환에서 불변이므로 그대로 옮긴다. 변환된
    /// Start/End 로 다시 계산하지 않는 이유는 두 가지다 — 부동소수 오차로 원래 값과 미세하게
    /// 어긋나고, Start/End 가 null 인 응답에서는 계산할 근거 자체가 없다.
    ///
    /// 부호 규약(Start → End)도 자동으로 유지된다. Start, End, Direction 이 모두 같은 회전을
    /// 받으므로 셋의 관계가 보존되기 때문이다.
    /// </summary>
    private static RidgeLine Apply(double[,] t, RidgeLine ridge) => new()
    {
        Point = TransformPoint(t, ridge.Point),
        Direction = TransformDirection(t, ridge.Direction),
        Start = ridge.Start is { } s ? TransformPoint(t, s) : null,
        End = ridge.End is { } e ? TransformPoint(t, e) : null,
        LengthMm = ridge.LengthMm,
        InlierCount = ridge.InlierCount,
        RmsMm = ridge.RmsMm,
    };

    /// <summary>점 변환. 회전 + 평행이동.</summary>
    private static Vec3 TransformPoint(double[,] t, Vec3 p) => new(
        t[0, 0] * p.X + t[0, 1] * p.Y + t[0, 2] * p.Z + t[0, 3],
        t[1, 0] * p.X + t[1, 1] * p.Y + t[1, 2] * p.Z + t[1, 3],
        t[2, 0] * p.X + t[2, 1] * p.Y + t[2, 2] * p.Z + t[2, 3]);

    /// <summary>방향 벡터 변환. 회전만 적용하고 평행이동은 뺀다.</summary>
    private static Vec3 TransformDirection(double[,] t, Vec3 d)
    {
        var x = t[0, 0] * d.X + t[0, 1] * d.Y + t[0, 2] * d.Z;
        var y = t[1, 0] * d.X + t[1, 1] * d.Y + t[1, 2] * d.Z;
        var z = t[2, 0] * d.X + t[2, 1] * d.Y + t[2, 2] * d.Z;

        // 회전은 크기를 보존하므로 이론상 정규화가 필요 없다. 그래도 다시 맞추는 것은
        // 변환을 여러 번 거치며 쌓인 부동소수 오차가 소비 측의 단위벡터 가정을 깨지 않게
        // 하기 위해서다.
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length < 1e-12) return new Vec3(x, y, z);

        return new Vec3(x / length, y / length, z / length);
    }
}
