namespace HD_AMR.Enums;

/// <summary>
/// Holding Register 주소 30 - 상태 제어
/// </summary>
public enum ExecutionControl : ushort
{
    Stop = 1,    // 정지
    Start = 2,   // 시작
    Pause = 3    // 일시정지
}
