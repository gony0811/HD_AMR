using System.Text.Json.Serialization;

namespace HD_AMR.Contracts.Lidar;

/// <summary>
/// 3차원 점/벡터. 단위는 <b>mm</b>로, HD_AMR 의 포즈 규약([x,y,z,rx,ry,rz] = mm + 도)과
/// 같은 길이 단위를 쓴다. 회전은 이 타입에 담지 않는다 — 좌표 변환은 수신 측(HD_AMR)의
/// PoseMath 가 담당한다.
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => default;

    /// <summary>벡터 크기(mm).</summary>
    [JsonIgnore]
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>
/// 측정값이 표현된 좌표계.
///
/// 설계상 Jetson 서비스는 <see cref="SensorOptical"/> 만 내보내고, 툴/베이스 좌표계로의
/// 변환은 HD_AMR 이 코봇 자세와 hand-eye 캘리브레이션을 곱해 수행한다. Jetson 은 로봇
/// 기구학을 전혀 알지 못한다. 나머지 값은 향후 확장 여지로만 정의해 둔 것이다.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LidarFrame
{
    /// <summary>
    /// 센서 광학 좌표계. 원점은 센서, 단위 mm. 오른손 좌표계(X × Y = Z).
    ///
    /// <b>배열 기준 (장착 자세와 무관하게 불변):</b>
    /// <list type="bullet">
    ///   <item>열(column) 증가 = <b>+X</b></item>
    ///   <item>행(row) 증가 = <b>+Y</b></item>
    ///   <item>광축 전방 = <b>+Z</b></item>
    /// </list>
    ///
    /// ⚠ <b>통상적인 카메라 규약과 다르다.</b> 보통은 열 증가 = 오른쪽, 행 증가 = 아래쪽이지만
    ///   NSL-1110AV 는 이미저가 본체 대비 90° 회전 장착되어 있어, 표준 거치 자세(장변 수직,
    ///   커넥터 아래) 기준으로 <b>+X = 위쪽, +Y = 오른쪽</b>이 된다. "관례상 이럴 것"이라고
    ///   가정하고 코드를 쓰면 좌우와 상하가 통째로 뒤바뀐다.
    ///
    /// 물리 방향은 장착 자세에 따라 회전하므로 소비 측은 <b>배열 관계만 신뢰</b>하고,
    /// 물리 대응은 hand-eye 캘리브레이션에 맡길 것.
    ///
    /// 실측 근거(2026-08-07): 열 스캔 dX/d(col)=+12.63, 행 스캔 dY/d(row)=+12.15,
    /// 사전 선택 없는 전체 점군 RANSAC 에서 바닥 평면의 법선이 X축과 6.0°·중심 X=−861mm.
    /// </summary>
    SensorOptical = 0,

    /// <summary>코봇 툴 플랜지 좌표계.</summary>
    ToolFlange = 1,

    /// <summary>코봇 베이스 좌표계.</summary>
    RobotBase = 2,
}

/// <summary>
/// 검출된 능선(코러게이션 마루). 측정 대상 최상단 마루를 직선으로 표현한다.
///
/// 단일 픽셀의 최대값이 아니라 다수 픽셀을 직선 피팅(RANSAC + 최소제곱)해 얻는다.
/// 320x240 ToF 의 픽셀당 분해능은 1m 에서 3~6mm 수준이라, 피팅 없이는 요구 정밀도
/// (10mm 내외)를 만족할 수 없다. <see cref="RmsMm"/> 과 <see cref="InlierCount"/> 는
/// 그 피팅이 실제로 신뢰할 만했는지 판단하는 근거이므로 소비 측에서 반드시 확인해야 한다.
/// </summary>
public sealed record RidgeLine
{
    /// <summary>능선 위의 대표점. 유효 구간(<see cref="Start"/>~<see cref="End"/>)의 중점.</summary>
    public required Vec3 Point { get; init; }

    /// <summary>
    /// 단위 방향 벡터.
    ///
    /// 직선의 방향은 본질적으로 ± 부호 모호성이 있다. 이 계약에서는 <b><see cref="Start"/> →
    /// <see cref="End"/> 방향</b>으로 부호를 고정한다. 서비스 구현은 이 규약을 지켜야 하고,
    /// 소비 측은 부호가 프레임마다 뒤집히지 않는다고 가정해도 된다.
    /// </summary>
    public required Vec3 Direction { get; init; }

    /// <summary>유효 구간 시작점.</summary>
    public Vec3? Start { get; init; }

    /// <summary>유효 구간 끝점.</summary>
    public Vec3? End { get; init; }

    /// <summary>유효 구간 길이(mm). <see cref="Start"/>/<see cref="End"/> 사이 거리.</summary>
    public double LengthMm { get; init; }

    /// <summary>직선 피팅에 사용된 인라이어 픽셀 수. 적으면 피팅이 우연일 수 있다.</summary>
    public int InlierCount { get; init; }

    /// <summary>피팅 잔차 RMS(mm). 품질 게이트로 쓴다 — 임계값을 넘으면 서비스가 valid=false 로 내린다.</summary>
    public double RmsMm { get; init; }
}

/// <summary>센서 픽셀 좌표 기준 ROI. nanolib <c>nsl_setRoi</c> 와 같은 의미.</summary>
public sealed record RoiRect
{
    public required int XMin { get; init; }
    public required int YMin { get; init; }
    public required int XMax { get; init; }
    public required int YMax { get; init; }
}
