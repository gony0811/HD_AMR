using System.Text.Json.Serialization;

namespace HD_AMR.Contracts.Lidar;

/// <summary>
/// 1회 측정 요청.
///
/// 측정은 <b>코봇이 정지한 상태</b>에서 수행하는 것을 전제로 한다. 정지 측정이기 때문에
/// 캡처 시각과 로봇 자세 시각을 정합할 필요가 없고, 다중 프레임 평균으로 거리 노이즈를
/// 1/√N 로 줄일 수 있다. 이 두 가지가 요구 정밀도(10mm 내외)를 맞추는 핵심이다.
/// </summary>
public sealed record MeasureRequest
{
    /// <summary>
    /// 평균에 사용할 프레임 수.
    ///
    /// 실측(거리 2.3m, 노출 400µs): 단일 프레임 σ 8~12mm 가 10프레임 평균에서 <b>σ 4~7mm</b>
    /// 로 떨어진다. 다만 1/√N 을 따르지 않고 <b>N≈10 에서 바닥을 친다</b> — 시간 상관 노이즈가
    /// 남기 때문이라 20프레임 이상은 이득이 거의 없다. 15fps 기준 10프레임은 약 0.67초.
    /// </summary>
    public int Frames { get; init; } = 10;

    /// <summary>전체 측정 타임아웃(ms). 프레임 수집 + 분석을 모두 포함한다.</summary>
    public int TimeoutMs { get; init; } = 3000;

    /// <summary>
    /// 사용할 센서 파라미터 프로파일 이름(ROI/노출/필터 묶음). null 이면 서비스의 현재 설정을 쓴다.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    /// true 면 응답에 피팅 인라이어 점군(<see cref="MeasureResponse.Samples"/>)을 포함한다.
    /// 진단용이며 응답 크기가 커지므로 평상시에는 끈다.
    /// </summary>
    public bool IncludeSamples { get; init; }
}

/// <summary>
/// 측정 결과.
///
/// ⚠ 소비 측은 <see cref="Valid"/> 를 반드시 확인해야 한다. 검출 실패를 "직전 값 유지"로
///   숨기면 잘못된 위치로 제어가 나간다. 실패는 실패로 전달하는 것이 이 계약의 전제다.
/// </summary>
public sealed record MeasureResponse
{
    /// <summary>서비스가 부여하는 단조 증가 시퀀스. 응답 누락/중복 감지용.</summary>
    public required long Seq { get; init; }

    /// <summary>
    /// <b>캡처</b> 시각(발행 시각이 아님). 정지 측정이라 제어에 직접 쓰이지는 않지만,
    /// 로그 대조와 지연 진단에 필요하다. 다중 프레임 평균인 경우 첫 프레임 시각.
    /// </summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>검출 성공 여부. false 면 <see cref="Ridge"/> 는 null 이고 <see cref="Failure"/> 에 사유가 담긴다.</summary>
    public required bool Valid { get; init; }

    /// <summary>
    /// <see cref="Ridge"/> 가 표현된 좌표계. 현재 서비스는 항상 <see cref="LidarFrame.SensorOptical"/> 을 반환한다.
    /// 필드로 명시해 두는 이유는, 값만 보고 "센서 기준인가 로봇 기준인가"를 헷갈리는 사고를 막기 위해서다.
    /// </summary>
    public required LidarFrame Frame { get; init; }

    /// <summary>검출된 능선. <see cref="Valid"/> 가 true 일 때만 채워진다.</summary>
    public RidgeLine? Ridge { get; init; }

    /// <summary>검출 신뢰도 0.0~1.0. 인라이어 비율과 피팅 잔차로부터 서비스가 산출한다.</summary>
    public double Confidence { get; init; }

    /// <summary><see cref="Valid"/> 가 false 일 때의 사유.</summary>
    public MeasureFailure? Failure { get; init; }

    /// <summary>사람이 읽을 수 있는 실패 상세. 로그/화면 표시용이며 분기 조건으로 쓰지 말 것.</summary>
    public string? FailureDetail { get; init; }

    /// <summary>측정 품질 지표. 성공/실패와 무관하게 채워진다 — 실패 원인 분석에 필요하다.</summary>
    public MeasurementQuality? Quality { get; init; }

    /// <summary>
    /// 피팅 인라이어 점군(센서 좌표계, mm). <see cref="MeasureRequest.IncludeSamples"/> 가
    /// true 일 때만 채워진다. 진단 전용.
    /// </summary>
    public IReadOnlyList<Vec3>? Samples { get; init; }
}

/// <summary>측정 실패 사유. 분기 조건으로 쓸 수 있도록 안정적으로 유지한다.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeasureFailure
{
    /// <summary>센서가 연결되어 있지 않거나 핸들이 유효하지 않다.</summary>
    NotConnected = 0,

    /// <summary>제한 시간 안에 필요한 프레임을 받지 못했다.</summary>
    Timeout = 1,

    /// <summary>유효 픽셀이 너무 적다. 대상이 시야 밖이거나 거리/반사율 문제.</summary>
    InsufficientValidPixels = 2,

    /// <summary>능선 직선 피팅이 수렴하지 못했다.</summary>
    RidgeFitFailed = 3,

    /// <summary>피팅은 됐으나 잔차(RMS)가 허용치를 넘었다.</summary>
    RmsExceeded = 4,

    /// <summary>포화/ADC 오버플로 픽셀이 과다하다. 노출 시간이나 조명 조건을 조정해야 한다.</summary>
    Saturated = 5,

    /// <summary>그 외 서비스 내부 오류. <see cref="MeasureResponse.FailureDetail"/> 참조.</summary>
    Internal = 99,
}

/// <summary>
/// 측정 품질 지표. 실패했을 때 "왜"를 좁히는 근거이므로 성공/실패 모두에서 채운다.
/// 픽셀 분류는 nanolib 의 무효 픽셀 코드(NSL_LOW_AMPLITUDE 64001, NSL_ADC_OVERFLOW 64002,
/// NSL_SATURATION 64003 등)를 집계한 값이다.
/// </summary>
public sealed record MeasurementQuality
{
    /// <summary>요청된 프레임 수.</summary>
    public int FramesRequested { get; init; }

    /// <summary>실제 평균에 사용된 프레임 수. 요청보다 적으면 수신 중 손실이 있었다는 뜻.</summary>
    public int FramesUsed { get; init; }

    /// <summary>
    /// 불완전하다고 판정되어 버린 프레임 수.
    ///
    /// nanolib 은 손상 프레임을 알려주는 수단이 전혀 없다 — 반환값도, 카운터도, 체크섬도,
    /// 프레임 번호도 없다. 실측에서 원인 미상으로 <c>distance2D == 0</c>인 픽셀을 가진
    /// 프레임이 산발적으로 관측됐고(4600프레임 중 22개), 재현 조건이 특정되지 않았다.
    /// 정상 프레임에서 0은 768만 픽셀 표본에서 한 번도 나오지 않으므로, 0은 곧
    /// "채워지지 않은 픽셀" = 부분 프레임의 증거다.
    ///
    /// 이 값이 0이 아니면 링크나 센서에 이상이 있다는 신호이므로 운영 중 관찰할 것.
    /// </summary>
    public int IncompleteFramesDropped { get; init; }

    /// <summary>ROI 내 유효 거리값을 가진 픽셀 수(값 &lt; 64000).</summary>
    public int ValidPixels { get; init; }

    /// <summary>진폭 부족 픽셀 수(NSL_LOW_AMPLITUDE).</summary>
    public int LowAmplitudePixels { get; init; }

    /// <summary>포화 픽셀 수(NSL_SATURATION).</summary>
    public int SaturatedPixels { get; init; }

    /// <summary>ADC 오버플로 픽셀 수(NSL_ADC_OVERFLOW).</summary>
    public int AdcOverflowPixels { get; init; }

    /// <summary>측정 시점 센서 온도(℃). 드리프트 추적용.</summary>
    public double SensorTemperatureC { get; init; }
}
