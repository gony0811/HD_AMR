namespace HD_AMR.Enums;

/// <summary>
/// Input Register 주소 10 - 로봇 상태
/// </summary>
public enum RobotState : ushort
{
    Stopped = 1,  // 정지
    Started = 2,  // 시작
    Paused = 3    // 일시정지
}
