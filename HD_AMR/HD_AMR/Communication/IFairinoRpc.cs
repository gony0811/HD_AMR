using Horizon.XmlRpc.Client;
using Horizon.XmlRpc.Core;

namespace HD_AMR.Communication;

/// <summary>
/// 페어리노 컨트롤러의 XML-RPC 인터페이스. 파이썬/C++ SDK가 와이어에서 호출하는 것과 동일한 메서드를
/// C#에서 직접 호출한다. <see cref="Horizon.XmlRpc.Client.XmlRpcProxyGen"/>이 런타임에 프록시를 생성한다.
///
/// 반환형은 모두 <c>object</c>로 둔다. FAIRINO는 메서드에 따라 errcode를 bare <c>&lt;int&gt;</c>로 주거나
/// <c>[errcode, ...data]</c> 배열로 감싸 주기 때문(예: GetSDKVersion = [err, [..]]). 고정 타입으로 받으면
/// 형식이 어긋나는 순간 역직렬화가 실패하므로, object로 받아 <see cref="FairinoRpcClient"/>에서 방어적으로 해석한다.
///
/// ⚠ 메서드명·파라미터 순서는 FAIRINO 파이썬 SDK 시그니처 기준 추정이다. 어긋나면 실물에서 Wireshark로
///   20003 포트를 캡처해 보정할 것.
/// </summary>
public interface IFairinoRpc : IXmlRpcProxy
{
    // ── 좌표계 설정 ────────────────────────────────────────────────
    /// <summary>공구(Tool) 좌표계 설정. 파이썬 SDK 와이어: SetToolCoord(id, coord[6], type, install, toolID, loadNum).</summary>
    [XmlRpcMethod("SetToolCoord")]
    object SetToolCoord(int id, double[] coord, int type, int install, int toolID, int loadNum);

    /// <summary>사용자(작업물/User) 좌표계 설정. 와이어: SetWObjCoord(id, coord[6], refFrame).</summary>
    [XmlRpcMethod("SetWObjCoord")]
    object SetWObjCoord(int id, double[] coord, int refFrame);

    /// <summary>id 공구 좌표계 현재 값 읽기. 반환 [err, x,y,z,rx,ry,rz].</summary>
    [XmlRpcMethod("GetToolCoordWithID")]
    object GetToolCoordWithID(int id);

    /// <summary>id 작업물 좌표계 현재 값 읽기. 반환 [err, x,y,z,rx,ry,rz].</summary>
    [XmlRpcMethod("GetWObjCoordWithID")]
    object GetWObjCoordWithID(int id);

    /// <summary>작업물 좌표계 3점법 참조점 기록. pointNum 1~3 (로봇이 현재 TCP를 해당 점으로 기록).</summary>
    [XmlRpcMethod("SetWObjCoordPoint")]
    object SetWObjCoordPoint(int pointNum);

    /// <summary>기록된 3점으로 작업물 좌표계 계산. method 0=원점-X축-Z축, 1=원점-X축-XY평면. 반환 [err, x,y,z,rx,ry,rz].</summary>
    [XmlRpcMethod("ComputeWObjCoord")]
    object ComputeWObjCoord(int method, int refFrame);

    // ── 이동 명령 ──────────────────────────────────────────────────
    /// <summary>
    /// 직교 공간 직선 이동(MoveL). ⚠ FAIRINO는 모든 인자를 33요소 단일 배열 1개로 받는다(개별 파라미터 아님):
    /// [j0..j5, x,y,z,rx,ry,rz, tool, user, vel, acc, ovl, blendR, blendMode,
    ///  ex0..ex3, search, offset_flag, off0..off5, oacc, velAccParamMode]. int/double 타입을 위치별로 정확히.
    /// </summary>
    [XmlRpcMethod("MoveL")]
    object MoveL(object[] args);

    /// <summary>관절 공간 이동(MoveJ). 와이어: 11개 개별 파라미터.</summary>
    [XmlRpcMethod("MoveJ")]
    object MoveJ(double[] joint_pos, double[] desc_pos, int tool, int user,
                 double vel, double acc, double ovl, double[] exaxis_pos,
                 double blendT, int offset_flag, double[] offset_pos);

    /// <summary>역기구학: 직교 자세 → 관절각. 반환 [errcode, j0..j5]. type=0, config=-1 기본.</summary>
    [XmlRpcMethod("GetInverseKin")]
    object GetInverseKin(int type, double[] desc_pos, int config);

    /// <summary>정기구학: 관절각[6](도) → 베이스 기준 공구 포즈. 반환 [errcode, x,y,z,rx,ry,rz].</summary>
    [XmlRpcMethod("GetForwardKin")]
    object GetForwardKin(double[] joint_pos);

    // ── 상태/제어 ──────────────────────────────────────────────────
    /// <summary>로봇 사용 활성화/비활성화. state: 1=Enable, 0=Disable.</summary>
    [XmlRpcMethod("RobotEnable")]
    object RobotEnable(int state);

    /// <summary>모드 전환. state: 0=자동, 1=수동.</summary>
    [XmlRpcMethod("Mode")]
    object Mode(int state);

    /// <summary>전역 속도 비율(%) 설정.</summary>
    [XmlRpcMethod("SetSpeed")]
    object SetSpeed(int vel);

    /// <summary>현재 관절각(도) 조회. flag: 0=블로킹,1=논블로킹. 반환 [errcode, j0..j5].</summary>
    [XmlRpcMethod("GetActualJointPosDegree")]
    object GetActualJointPosDegree(int flag);

    /// <summary>
    /// 현재 TCP 직교 포즈 조회. flag: 0=블로킹,1=논블로킹(좌표계 선택 아님).
    /// ⚠ 반환 포즈는 현재 활성 작업물(user) 좌표계 기준이다. BASE 기준 포즈는 GetForwardKin으로 구할 것.
    /// 반환 = [errcode, [x,y,z,rx,ry,rz]] 또는 평탄 배열.
    /// </summary>
    [XmlRpcMethod("GetActualTCPPose")]
    object GetActualTCPPose(int flag);

    /// <summary>모션 정지.</summary>
    [XmlRpcMethod("StopMotion")]
    object StopMotion();
}
