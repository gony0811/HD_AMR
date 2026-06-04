namespace HD_AMR.Models;

/// <summary>
/// 코봇 상태 (Input Register 310~322, 13개). NAMUGA_AMR에서 이식.
/// </summary>
public class CobotStatus
{
    /// <summary>Enable 상태 (0=Not enabled, 1=Enabled)</summary>
    public ushort EnableState { get; set; }

    /// <summary>로봇 모드 (1=Manual, 0=Automatic)</summary>
    public ushort RobotMode { get; set; }

    /// <summary>동작 상태 (1=Stop, 2=Run, 3=Pause, 4=Drag)</summary>
    public ushort OperationStatus { get; set; }

    /// <summary>Tool 번호</summary>
    public ushort ToolNo { get; set; }

    /// <summary>Job 번호</summary>
    public ushort JobNumber { get; set; }

    /// <summary>비상정지 상태 (0=정상, 1=비상정지)</summary>
    public ushort ScrumState { get; set; }

    /// <summary>로봇 상태 — Super soft limit fault</summary>
    public ushort RobotStatusFault { get; set; }

    /// <summary>Master fault code</summary>
    public ushort MasterFaultCode { get; set; }

    /// <summary>Sub fault code</summary>
    public ushort SubFaultCode { get; set; }

    /// <summary>충돌 감지 (1=Collision, 0=No collision)</summary>
    public ushort CollisionDetection { get; set; }

    /// <summary>Motion in place signal</summary>
    public ushort MotionInPlace { get; set; }

    /// <summary>Safety stop signal S0</summary>
    public ushort SafetyStopS0 { get; set; }

    /// <summary>Safety stop signal S1</summary>
    public ushort SafetyStopS1 { get; set; }

    /// <summary>이 스냅샷 갱신 시각(UTC).</summary>
    public DateTime UpdatedAt { get; set; }
}
