using HD_AMR.Contracts.Lidar;
using HD_AMR.LidarService.Device;

namespace HD_AMR.LidarService.Detection;

/// <summary>능선(코러게이션 마루) 검출.</summary>
internal interface IRidgeDetector
{
    RidgeDetectionResult Detect(AveragedCapture capture);
}

/// <summary>
/// 검출 결과. 실패 사유를 명시적으로 담아 호출 측이 그대로 응답에 실을 수 있게 한다.
///
/// 진단 필드(<see cref="PlaneAInliers"/> 이하)는 <b>실패했을 때도 가능한 데까지 채운다.</b>
/// "첫 평면은 찾았는데 두 번째에서 실패"와 "아예 후보 점이 부족"은 대응이 전혀 다른데,
/// 실패 사유 문자열만으로는 그 구분이 화면에 남지 않는다.
/// </summary>
internal sealed record RidgeDetectionResult
{
    public required bool Success { get; init; }
    public RidgeLine? Ridge { get; init; }
    public double Confidence { get; init; }
    public MeasureFailure? Failure { get; init; }
    public string? FailureDetail { get; init; }
    public IReadOnlyList<Vec3>? Inliers { get; init; }

    /// <summary>깊이 게이팅을 통과해 평면 피팅 후보가 된 픽셀 수.</summary>
    public int CandidateCount { get; init; }

    /// <summary>첫 번째 평면의 인라이어 픽셀 인덱스. 오버레이 렌더링용.</summary>
    public int[]? PlaneAInliers { get; init; }

    /// <summary>두 번째 평면의 인라이어 픽셀 인덱스.</summary>
    public int[]? PlaneBInliers { get; init; }

    /// <summary>두 평면의 사잇각(도). <see cref="RidgeDetector"/> 전용.</summary>
    public double PlaneAngleDeg { get; init; }

    /// <summary>원호 피팅 진단. <see cref="ArcRidgeDetector"/> 전용.</summary>
    public ArcDiagnostics? Arc { get; init; }

    public static RidgeDetectionResult Fail(MeasureFailure reason, string detail) =>
        new() { Success = false, Failure = reason, FailureDetail = detail };
}

/// <summary>
/// 반원 비드 검출의 중간 결과. 실패했을 때 어디까지 갔는지 보여주기 위해 성공/실패 모두에서 채운다.
///
/// <see cref="RadiusMm"/> 이 가장 중요한 진단값이다. 배경이나 엉뚱한 곡면을 잡으면 반경이
/// 실물과 전혀 다른 값으로 나오는데, 인라이어 수나 잔차 같은 다른 지표는 그때도 멀쩡해 보인다.
/// </summary>
internal sealed record ArcDiagnostics
{
    /// <summary>추정된 비드 반경(mm). 실물과 대조하는 것이 가장 강력한 검증이다.</summary>
    public double RadiusMm { get; init; }

    /// <summary>
    /// 원 중심의 평판 위 높이(mm). 반원이면 0 에 가까워야 한다 — 중심이 판 위에 있기 때문이다.
    /// 크게 벗어나면 형상이 반원이 아니거나 평판 피팅이 어긋난 것이다.
    /// </summary>
    public double CenterHeightMm { get; init; }

    /// <summary>단면 원 피팅 잔차 RMS(mm).</summary>
    public double CircleRmsMm { get; init; }

    /// <summary>평판 평면 피팅 잔차 RMS(mm).</summary>
    public double PlaneRmsMm { get; init; }

    /// <summary>높이 구간을 통과한 비드 후보 점 수(비드 여러 개 포함).</summary>
    public int BeadPointCount { get; init; }

    /// <summary>평판 위 최대 높이(mm). 비드가 안 보일 때 원인을 좁히는 근거다.</summary>
    public double MaxHeightMm { get; init; }
}
