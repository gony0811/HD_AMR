namespace HD_AMR.Communication;

/// <summary>
/// 상태 피드백 포트(20004)에서 파싱한 로봇 실시간 상태 스냅샷.
/// ⚠ 필드 구성/오프셋은 C++ SDK 헤더의 ROBOT_STATE_PKG 및 캡처로 확정 후 보정할 것.
/// </summary>
public record FairinoState
{
    /// <summary>관절 각도(도) — 6축.</summary>
    public double[] JointPos { get; init; } = new double[6];

    /// <summary>TCP 직교 포즈 [x,y,z,rx,ry,rz] (mm, 도).</summary>
    public double[] TcpPose { get; init; } = new double[6];

    /// <summary>로봇 모드(0=자동,1=수동 등).</summary>
    public int RobotMode { get; init; }

    /// <summary>프로그램/모션 상태(0=정지,1=운행,2=일시정지 등).</summary>
    public int ProgramState { get; init; }

    /// <summary>디지털 입력 상태.</summary>
    public bool[] DigitalInputs { get; init; } = Array.Empty<bool>();

    /// <summary>디지털 출력 상태.</summary>
    public bool[] DigitalOutputs { get; init; } = Array.Empty<bool>();

    /// <summary>에러 코드(0=정상).</summary>
    public int ErrorCode { get; init; }

    /// <summary>이 스냅샷 갱신 시각(UTC).</summary>
    public DateTime UpdatedAt { get; init; }
}
