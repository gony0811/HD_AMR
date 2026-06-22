namespace HD_AMR.Enums;

/// <summary>
/// Input Register 주소 64 - 작업 상태
/// </summary>
public enum WorkStatus : ushort
{
    None = 0,      // 미정의
    Idle = 1,      // 대기중
    Moving = 2,    // 이동중
    Docking = 3,   // 도킹중
    Jog = 4        // 조그 이동중
}
