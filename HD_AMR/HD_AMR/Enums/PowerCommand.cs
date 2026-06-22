namespace HD_AMR.Enums;

/// <summary>
/// Holding Register 주소 1 - 전원 제어 명령
/// </summary>
public enum PowerCommand : ushort
{
    None = 0,
    PowerOff = 1,
    Restart = 2,
    QuickRestart = 3
}
