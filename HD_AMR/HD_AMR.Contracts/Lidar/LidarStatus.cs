using System.Text.Json.Serialization;

namespace HD_AMR.Contracts.Lidar;

/// <summary>
/// LiDAR 서비스 및 센서 상태.
///
/// 모니터링 UI 는 Jetson 자체 프론트엔드가 담당하지만, HD_AMR 에도 최소한의 연결 상태
/// 표시는 필요하다. 운영자가 "왜 측정이 안 되지"를 판단할 수 있어야 하기 때문이다.
/// </summary>
public sealed record LidarStatus
{
    /// <summary>센서 핸들이 열려 있고 통신이 되는 상태인지.</summary>
    public required bool Connected { get; init; }

    /// <summary>
    /// 이더넷 링크(캐리어) 상태. 판단할 수 없으면 null.
    ///
    /// <see cref="Connected"/> 와 함께 보면 원인을 좁힐 수 있다 — 센서 연결 실패는
    /// 반환 코드만으로 "케이블 단선"과 "잘못된 IP/센서 미응답"을 구분할 수 없기 때문이다
    /// (둘 다 동일한 오류 코드). <c>LinkUp=false</c> 면 케이블, <c>LinkUp=true</c> 인데
    /// <c>Connected=false</c> 면 센서나 주소 문제다.
    /// </summary>
    public bool? LinkUp { get; init; }

    /// <summary>모델명(예: "NSL-1110AV").</summary>
    public string? Model { get; init; }

    /// <summary>펌웨어 릴리스 번호(nanolib <c>NslConfig.firmware_release</c>).</summary>
    public int FirmwareRelease { get; init; }

    /// <summary>nanolib SDK 버전 문자열(예: "1.13").</summary>
    public string? SdkVersion { get; init; }

    /// <summary>센서 칩 ID. 개체 식별/이력 추적용.</summary>
    public int ChipId { get; init; }

    /// <summary>
    /// 장착 렌즈. 3D 재투영 계산에 직접 영향을 주므로 실제 장착품과 반드시 일치해야 한다.
    ///
    /// ⚠ nano-roboscan 공식 샘플은 이 값을 SF 로 하드코딩한다. 실물이 다르면 distance3D
    ///   좌표가 통째로 틀어지므로, 서비스는 반드시 실제 값을 읽어 보고해야 한다.
    /// </summary>
    public LidarLensType LensType { get; init; }

    /// <summary>센서 온도(℃).</summary>
    public double SensorTemperatureC { get; init; }

    /// <summary>센서 공급 전압(V). nanolib <c>nsl_getBitInfo</c>.</summary>
    public double VoltageV { get; init; }

    /// <summary>센서 소비 전류(mA). nanolib <c>nsl_getBitInfo</c>.</summary>
    public int CurrentMa { get; init; }

    /// <summary>최근 측정된 실제 프레임 레이트.</summary>
    public double MeasuredFps { get; init; }

    /// <summary>마지막으로 프레임을 수신한 시각. null 이면 한 번도 받지 못했다.</summary>
    public DateTimeOffset? LastFrameAt { get; init; }

    /// <summary>마지막 오류 메시지. 표시/로그용.</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// 렌즈 화각. 값은 nanolib <c>NslOption::LENS_TYPE</c> 과 일치시킨다.
/// 화각은 횡방향 분해능을 결정한다 — 1m 거리에서 NF 는 약 2.9mm/px, SF 는 약 6.3mm/px.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LidarLensType
{
    /// <summary>Narrow Field, 약 50°.</summary>
    NarrowField = 0,

    /// <summary>Standard Field, 약 90°.</summary>
    StandardField = 1,

    /// <summary>Wide Field, 약 110°.</summary>
    WideField = 2,
}
