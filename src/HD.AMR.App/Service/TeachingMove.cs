using HD.AMR.App.Communication;
using HD.AMR.App.Data.Entities;

namespace HD.AMR.App.Service;

/// <summary>
/// Teaching 페이지 '이동' 버튼 — Web(Teaching.razor)·Desktop(TeachingViewModel) 공용.
/// 베이스 기준 위치는 <b>저장된 관절값으로 MoveJ</b> 한다. 홈 → 홈_1.5 → 홈_2 처럼 J3 부호가 바뀌는
/// (팔꿈치 자세가 뒤집히는) 위치는 MoveL 이 직선 경로 중 J3=0 특이점을 지나야 해 rc=38/112 로 갈 수 없고,
/// 자세(pose)에서 IK 를 다시 풀면 다른 팔꿈치 해로 갈 수 있으므로 캡처한 J1~J6 를 그대로 쓴다.
/// 작업물 좌표계 기준(UserFrame&gt;0) 위치는 프레임 재등록 시 목표가 바뀌어 저장 관절값이 무효라 기존 MoveL 유지.
/// </summary>
public static class TeachingMove
{
    /// <summary>관절 이동(MoveJ) 속도 비율 파라미터 키. 같은 속도 % 라도 MoveJ 는 관절 최대속도 기준이라
    /// MoveL 보다 약 2배 빠르다 — 이 비율을 화면 속도에 곱해 종전 MoveL 체감 속도에 맞춘다.</summary>
    public const string JointVelocityScaleKey = "Teaching.JointMoveVelocityScale";
    public const double DefaultJointVelocityScale = 0.45;

    /// <summary>비율 로드 — 없거나 (0, 1] 밖이면 기본값.</summary>
    public static async Task<double> LoadJointVelocityScaleAsync(ParameterService param)
        => await param.GetDoubleAsync(JointVelocityScaleKey) is > 0 and <= 1 and var s ? s : DefaultJointVelocityScale;

    /// <summary>true = 저장된 관절값으로 MoveJ 할 위치(베이스 기준 + 관절값 캡처됨).</summary>
    public static bool UsesJointMove(TeachingPosition p)
        => p.UserFrame is not > 0 && p.J1.HasValue && p.J2.HasValue && p.J3.HasValue
           && p.J4.HasValue && p.J5.HasValue && p.J6.HasValue;

    /// <summary>티칭 위치로 이동. 반환 rc(0=성공).</summary>
    /// <param name="vel">화면 속도(%). MoveJ 일 때는 <paramref name="jointVelScale"/> 를 곱해 보낸다.</param>
    public static async Task<int> MoveToAsync(CobotService cobot, TeachingPosition p, int tool, double vel,
                                              double jointVelScale, CancellationToken ct)
    {
        var pose = new[] { p.X!.Value, p.Y!.Value, p.Z!.Value, p.Rx!.Value, p.Ry!.Value, p.Rz!.Value };

        if (UsesJointMove(p))
        {
            var joints = new[] { p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value };
            // 판정·저장은 ±180° 범위인데 원값을 지령하면 같은 자세에서도 J6 가 한 바퀴 돈다 — 현재 관절 쪽으로 감기 보정.
            var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
            // 캡처 공구와 이동 공구가 다르면 저장 pose 가 관절값과 맞지 않으므로 0 배열 → 정기구학으로 채운다.
            var desc = p.Tool == tool ? pose : new double[6];
            return await cobot.Rpc.MoveJAsync(FairinoRpcClient.UnwrapToward(joints, cur), desc,
                tool: tool, user: 0, vel: vel * jointVelScale, ct: ct);
        }

        double[] target = pose;
        if (p.UserFrame is int n && n > 0 && p.RelX.HasValue)
        {
            // 작업물 추종: 현재(재등록된) 프레임 T_N 에 상대 pose 적용 → 베이스 목표.
            var tN = await cobot.Rpc.GetWObjCoordAsync(n, ct);
            var rel = new[] { p.RelX!.Value, p.RelY!.Value, p.RelZ!.Value, p.RelRx!.Value, p.RelRy!.Value, p.RelRz!.Value };
            target = FrameMath.FromFrame(rel, tN);
        }
        return await cobot.Rpc.MoveLAsync(target, tool: tool, user: 0, vel: vel, ct: ct);
    }
}
