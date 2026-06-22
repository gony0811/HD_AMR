using HD_AMR.Enums;

namespace HD_AMR.Models;

/// <summary>
/// 로봇 전체 상태 (Input Register 전체 읽기 결과)
/// </summary>
public record RobotStatus
{
    /// <summary>전원 상태</summary>
    public PowerState PowerState { get; init; }

    /// <summary>로봇 상태</summary>
    public RobotState RobotState { get; init; }

    /// <summary>에러 코드</summary>
    public ushort ErrorCode { get; init; }

    /// <summary>로봇 주행 정지 활성화 여부</summary>
    public ushort RobotStopActive { get; init; }

    /// <summary>WiFi 상태</summary>
    public WiFiState WiFi { get; init; }

    /// <summary>작업 상태</summary>
    public WorkStatus WorkStatus { get; init; }

    /// <summary>로봇 현재 위치</summary>
    public RobotPose Pose { get; init; } = new(0, 0, 0);

    /// <summary>맵 일치율 (%)</summary>
    public float MapStatusPercent { get; init; }

    /// <summary>주행 모드</summary>
    public DrivingMode DrivingMode { get; init; }

    /// <summary>배터리 상태</summary>
    public BatteryStatus Battery { get; init; } = new();

    /// <summary>Task/Job 진행 상태</summary>
    public TaskProgress TaskProgress { get; init; } = new();
}
