using HD_AMR.Enums;

namespace HD_AMR.Models;

/// <summary>
/// 배터리 상태 정보 (Input Register 50~54)
/// </summary>
public record BatteryStatus
{
    /// <summary>배터리 전량 (0~100%)</summary>
    public float LevelPercent { get; init; }

    /// <summary>배터리 전압 (V, raw / 100)</summary>
    public float Voltage { get; init; }

    /// <summary>배터리 전류 (A, raw / 100)</summary>
    public float Current { get; init; }

    /// <summary>배터리 온도 (°C)</summary>
    public float TemperatureCelsius { get; init; }

    /// <summary>충전 상태</summary>
    public ChargingState ChargingState { get; init; }
}
