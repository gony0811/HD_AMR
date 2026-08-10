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

    /// <summary>두 평면의 사잇각(도). 두 평면을 모두 찾았을 때만 의미가 있다.</summary>
    public double PlaneAngleDeg { get; init; }

    public static RidgeDetectionResult Fail(MeasureFailure reason, string detail) =>
        new() { Success = false, Failure = reason, FailureDetail = detail };
}
