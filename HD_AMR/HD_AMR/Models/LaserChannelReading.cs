namespace HD_AMR.Models;

/// <summary>
/// 레이저 변위 센서 한 채널의 측정 스냅샷. OMRON ZP-EIP Input Assembly(인스턴스 110, 276B)에서
/// 파싱한다. <see cref="Raw"/> 는 32bit signed 원시값, <see cref="Value"/> 는 스케일 적용 물리값(단위는
/// 설정의 MeasurementUnit).
/// </summary>
/// <param name="Channel">채널 번호(1-based).</param>
/// <param name="Raw">측정 원시값(32bit signed, little-endian).</param>
/// <param name="Value">물리값 = Raw × MeasurementScale.</param>
/// <param name="Enabled">Sensor Enable — 측정 범위 내(유효)면 true.</param>
/// <param name="Error">Sensor Error Status.</param>
/// <param name="Warning">Sensor Warning Status.</param>
/// <param name="High">판정 출력 HIGH.</param>
/// <param name="Low">판정 출력 LOW.</param>
/// <param name="Pass">판정 출력 PASS.</param>
/// <param name="Zeroed">이 채널에 영점(Zero) 요청 비트가 설정돼 있는지(Output Assembly).</param>
public record LaserChannelReading(
    int Channel,
    int Raw,
    double Value,
    bool Enabled,
    bool Error,
    bool Warning,
    bool High,
    bool Low,
    bool Pass,
    bool Zeroed);
