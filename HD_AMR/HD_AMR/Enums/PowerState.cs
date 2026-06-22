namespace HD_AMR.Enums;

/// <summary>
/// Input Register 주소 1 - 전원 상태
/// </summary>
public enum PowerState : ushort
{
    Normal = 1,          // 정상
    ShutDown = 2,        // 종료
    Restarting = 3,      // 재시작
    QuickRestarting = 4  // 빠른 재시작
}
