using HD.AMR.App.Communication;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 작업물 좌표계(user=N) 기준 경유점 자세 합성 — 시퀀스 ⑫ <c>InspectionRunStep</c> 과 검사 프로파일 페이지
/// (/inspection) 가 공유하는 규칙.
///
/// 3점 교시는 원점 위치와 축 방향만 정하고 "교시 당시 툴 자세"를 자세 0 으로 만들어 주지 않는다.
/// 프레임 X 는 항상 점1→점2 방향이라, 점1 캡처 때 툴 +X 가 반대를 보고 있었으면 툴은 프레임 대비
/// RZ ≈ ±180° 상태다. 이때 rz=0 을 명령하면 같은 위치에서 툴을 Z 둘레로 180° 돌린 자세가 되어
/// 손목 한계/도달 불가(112)가 난다. 그래서 시작 시점의 프레임 기준 RZ(<see cref="Rz0"/>)를 읽어
/// 유지하고, 도면 회전(rz)은 그 위에 더한다.
///
/// 틸트 θ 는 프레임 Y 둘레 회전(ry)인데, ZYX 규약에서 Rz(rz0)·Ry(θ) 는 rz0≈±180 이면 툴 기준으로
/// 부호가 뒤집힌다(Ry_frame(θ)·Rz(rz0) = Rz(rz0)·Ry(±θ)). 그래서 <see cref="TiltSign"/> = sign(cos rz0).
/// </summary>
public readonly record struct WObjAttitude(double Rz0, double TiltSign)
{
    /// <summary>보정 없음 — 베이스(user=0) 실행처럼 pose 를 절대값으로 그대로 명령할 때.</summary>
    public static readonly WObjAttitude Identity = new(0.0, 1.0);

    /// <summary>현재 베이스 pose 와 프레임 T_N(베이스 기준)으로 유지 RZ·틸트 부호를 구한다.</summary>
    public static WObjAttitude FromCurrent(double[] curBasePose, double[] framePose)
    {
        var rel = FrameMath.ToFrame(curBasePose, framePose);
        var rz0 = rel[5];
        var sign = Math.Cos(rz0 * Math.PI / 180.0) >= 0 ? 1.0 : -1.0;
        return new WObjAttitude(rz0, sign);
    }

    /// <summary>컨트롤러에서 현재 TCP(공구 <paramref name="tool"/>)와 작업물 <paramref name="wobjId"/> 를 읽어 구한다.
    /// wobjId ≤ 0(베이스)이면 <see cref="Identity"/>. 프레임 미등록(원점=0)이면 예외.</summary>
    public static async Task<WObjAttitude> ResolveAsync(FairinoRpcClient rpc, int wobjId, int tool, CancellationToken ct = default)
    {
        if (wobjId <= 0) return Identity;
        var frame = await rpc.GetWObjCoordAsync(wobjId, ct);
        if (frame.Length != 6 || frame.All(v => v == 0))
            throw new InvalidOperationException($"작업물 좌표계 #{wobjId} 가 등록되지 않았습니다(원점=0) — 좌표계를 먼저 등록하세요.");
        var cur = await rpc.GetTcpPoseInBaseAsync(tool, ct);
        return FromCurrent(cur, frame);
    }

    /// <summary>경유점 pose 합성: 위치 그대로, rx=0, ry=틸트부호·θ, rz=rz0+도면 회전.</summary>
    public double[] Pose(double x, double y, double z, double thetaDeg, double rzDeg)
        => new[] { x, y, z, 0.0, TiltSign * thetaDeg, Rz0 + rzDeg };

    public override string ToString() => $"RZ 유지={Rz0:0.0}° (틸트부호 {TiltSign:+0;-0})";
}
