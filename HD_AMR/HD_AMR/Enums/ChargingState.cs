namespace HD_AMR.Enums;

/// <summary>
/// Input Register 주소 54 - 배터리 충전 여부
/// </summary>
public enum ChargingState : ushort
{
    Charging = 1,     // 충전중
    Discharging = 2   // 소비중
}
