using System.Text.Json.Serialization;

namespace HD_AMR.Contracts.Lidar;

/// <summary>
/// 센서 파라미터 전체 상태(조회용). 값의 의미와 허용 범위는 nanolib 헤더 주석을 따른다.
///
/// 이 값들의 튜닝은 Jetson 자체 프론트엔드에서 하는 것을 전제로 한다. HD_AMR 이 이 계약을
/// 참조하는 것은 상태 확인과, 시나리오상 필요한 경우의 제한적 변경을 위해서다.
/// </summary>
public sealed record LidarConfig
{
    /// <summary>3D 거리 측정 노출 시간(µs). 범위 0~2000.</summary>
    public int IntegrationTime3D { get; init; }

    /// <summary>Grayscale 노출 시간(µs). 범위 0~40000.</summary>
    public int IntegrationTimeGrayscale { get; init; }

    /// <summary>최소 진폭. 이보다 약한 신호는 NSL_LOW_AMPLITUDE 로 버려진다. 범위 0~500.</summary>
    public int MinAmplitude { get; init; }

    /// <summary>HDR 모드.</summary>
    public LidarHdrMode Hdr { get; init; }

    /// <summary>
    /// 변조 주파수. 최대 측정 거리를 결정한다(24MHz=6.25m, 12MHz=12.5m, 6MHz=25m, 3MHz=50m).
    /// 근거리 측정에서는 높은 주파수가 정밀도에 유리하다.
    /// </summary>
    public LidarModulation Modulation { get; init; }

    /// <summary>관심 영역. null 이면 전체 화면.</summary>
    public RoiRect? Roi { get; init; }

    /// <summary>메디안 필터 사용 여부.</summary>
    public bool MedianFilter { get; init; }

    /// <summary>평균(가우시안) 필터 사용 여부.</summary>
    public bool AverageFilter { get; init; }

    /// <summary>시간축 필터 계수. 0 또는 1000 이면 off, 1~999 가 유효값.</summary>
    public int TemporalFactor { get; init; }

    /// <summary>시간축 필터 임계값. 0 이면 off, 1~1000 이 유효값.</summary>
    public int TemporalThreshold { get; init; }

    /// <summary>에지 임계값. 0 이면 off, 1~5000 이 유효값.</summary>
    public int EdgeThreshold { get; init; }

    /// <summary>간섭 검출 한계. 0 이면 off, 1~10000 이 유효값.</summary>
    public int InterferenceLimit { get; init; }

    /// <summary>간섭 검출 시 직전 값을 사용할지 여부.</summary>
    public bool InterferenceUseLastValue { get; init; }

    /// <summary>Grayscale 모드에서 적외선 조명(LED) 사용 여부.</summary>
    public bool GrayscaleIllumination { get; init; }
}

/// <summary>
/// 센서 파라미터 부분 변경(PATCH)용. null 인 항목은 변경하지 않는다.
///
/// 전체 설정을 PUT 으로 덮어쓰지 않는 이유: 튜닝 중에는 슬라이더 하나만 움직이는 경우가
/// 대부분인데, 매번 전체를 보내면 다른 클라이언트의 동시 변경을 덮어써 버린다.
///
/// 이 변경은 <b>휘발성</b>이다. 장비 EEPROM 에 영구 저장하려면 별도의 persist 호출이
/// 필요하다(<see cref="LidarApiRoutes.ConfigPersist"/>). 튜닝할 때마다 플래시에 쓰면
/// 쓰기 수명을 깎기 때문에 의도적으로 분리했다.
/// </summary>
public sealed record LidarConfigPatch
{
    public int? IntegrationTime3D { get; init; }
    public int? IntegrationTimeGrayscale { get; init; }
    public int? MinAmplitude { get; init; }
    public LidarHdrMode? Hdr { get; init; }
    public LidarModulation? Modulation { get; init; }
    public RoiRect? Roi { get; init; }
    public bool? MedianFilter { get; init; }
    public bool? AverageFilter { get; init; }
    public int? TemporalFactor { get; init; }
    public int? TemporalThreshold { get; init; }
    public int? EdgeThreshold { get; init; }
    public int? InterferenceLimit { get; init; }
    public bool? InterferenceUseLastValue { get; init; }
    public bool? GrayscaleIllumination { get; init; }
}

/// <summary>HDR 모드. 값은 nanolib <c>NslOption::HDR_OPTIONS</c> 과 일치.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LidarHdrMode
{
    None = 0,
    Spatial = 1,
    Temporal = 2,
}

/// <summary>변조 주파수. 값은 nanolib <c>NslOption::MODULATION_OPTIONS</c> 과 일치.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LidarModulation
{
    /// <summary>12MHz — 최대 12.5m.</summary>
    Mhz12 = 0,

    /// <summary>24MHz — 최대 6.25m. 근거리 정밀도에 유리.</summary>
    Mhz24 = 1,

    /// <summary>6MHz — 최대 25m.</summary>
    Mhz6 = 2,

    /// <summary>3MHz — 최대 50m.</summary>
    Mhz3 = 3,

    /// <summary>1.5MHz.</summary>
    Mhz1p5 = 4,
}
