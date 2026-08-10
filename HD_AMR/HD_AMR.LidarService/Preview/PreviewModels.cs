using HD_AMR.Contracts.Lidar;

namespace HD_AMR.LidarService.Preview;

/// <summary>
/// 한 시점의 미리보기 결과. 이미지 세 장과 메타데이터를 <b>한 덩어리로 묶는다.</b>
///
/// 이미지와 숫자를 각각 따로 만들면 브라우저가 서로 다른 프레임의 그림과 통계를 섞어
/// 보게 된다. 노출을 바꾸는 순간처럼 화면이 확 변하는 시점에 그 불일치가 그대로 오판으로
/// 이어지므로(방금 바꾼 값이 반영된 건지 아닌지 알 수 없다), 한 프레임에서 만든 것을
/// 하나의 불변 객체로 게시하고 <see cref="Seq"/> 로 묶어 준다.
/// </summary>
internal sealed record PreviewFrame
{
    public required long Seq { get; init; }
    public required byte[] DistancePng { get; init; }
    public required byte[] AmplitudePng { get; init; }

    /// <summary>검출이 꺼져 있으면 null.</summary>
    public required byte[]? OverlayPng { get; init; }

    public required PreviewInfo Info { get; init; }
}

/// <summary>미리보기 메타데이터(JSON 응답 본문).</summary>
internal sealed record PreviewInfo
{
    public required long Seq { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double TemperatureC { get; init; }

    /// <summary>미리보기 루프가 실제로 돌고 있는 속도. 설정한 주기가 아니라 실측이다.</summary>
    public required double LoopFps { get; init; }

    /// <summary>이 프레임의 모든 픽셀이 채워졌는지. false 면 부분 프레임이다.</summary>
    public required bool Complete { get; init; }

    public required PreviewPixelCounts Pixels { get; init; }

    /// <summary>유효 픽셀 거리의 최소/중앙/최대(mm). 깊이 구간을 어디에 둘지 정하는 근거다.</summary>
    public double? DistanceMinMm { get; init; }
    public double? DistanceMedianMm { get; init; }
    public double? DistanceMaxMm { get; init; }

    /// <summary>유효 픽셀 진폭의 99 백분위. 진폭 스케일을 얼마로 둘지 정하는 근거다.</summary>
    public int? AmplitudeP99 { get; init; }

    /// <summary>이 이미지들을 렌더링할 때 쓴 스케일. 화면의 범례가 이 값을 따라간다.</summary>
    public required Imaging.RenderScale Scale { get; init; }

    public required bool DetectionEnabled { get; init; }

    /// <summary>검출 소요 시간(ms). 미리보기 주기를 정하는 근거다.</summary>
    public double DetectMs { get; init; }

    public bool DetectionSucceeded { get; init; }
    public RidgeLine? Ridge { get; init; }
    public double Confidence { get; init; }
    public MeasureFailure? Failure { get; init; }
    public string? FailureDetail { get; init; }

    /// <summary>깊이 게이팅을 통과한 후보 점 수.</summary>
    public int CandidateCount { get; init; }

    public int PlaneAInlierCount { get; init; }
    public int PlaneBInlierCount { get; init; }
    public double PlaneAngleDeg { get; init; }

    /// <summary>반원 코러게이션 검출의 중간값. 다른 검출 방식에서는 null.</summary>
    public Detection.ArcDiagnostics? Arc { get; init; }

    /// <summary>
    /// 검출된 코러게이션 후보 수. 2 이상이면 목표 위치를 주지 않는 한 프레임마다 다른 것이
    /// 선택될 수 있다는 뜻이라, 화면에서 바로 보여야 한다.
    /// </summary>
    public int CandidateRidgeCount { get; init; }
}

internal sealed record PreviewPixelCounts
{
    public required int Valid { get; init; }
    public required int LowAmplitude { get; init; }
    public required int AdcOverflow { get; init; }
    public required int Saturation { get; init; }
    public required int Unfilled { get; init; }
    public required int Other { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// 미리보기 동작 설정. 화면에서 바꿀 수 있어야 하므로 가변 싱글턴으로 둔다.
///
/// 이 값들은 <b>휘발성이다.</b> 서비스를 재시작하면 appsettings.json 값으로 돌아간다.
/// 튜닝 중 실수로 이상한 값을 넣어도 재시작 한 번으로 복구되는 편이 안전하다.
/// </summary>
internal sealed class PreviewOptions
{
    /// <summary>
    /// 프레임 갱신 목표 주기(ms). 검출이 이보다 오래 걸리면 루프가 자연히 느려진다 —
    /// 밀린 만큼 따라잡으려 하지 않는다. 실측 주기는 <see cref="PreviewInfo.LoopFps"/> 로 보고한다.
    /// </summary>
    public int IntervalMs { get; set; } = 500;

    /// <summary>
    /// 미리보기 요청이 이 시간 동안 없으면 루프를 재운다.
    ///
    /// 24시간 떠 있는 서비스에서 아무도 보지 않는 화면을 위해 코어를 계속 태울 이유가 없다.
    /// 특히 검출을 켜 두면 RANSAC 이 CPU 를 물고 있어서, 실제 측정 요청이 왔을 때 응답이
    /// 느려진다.
    /// </summary>
    public int IdleTimeoutMs { get; set; } = 10000;

    /// <summary>능선 검출을 미리보기에서도 돌릴지. 조준만 할 때는 꺼서 CPU 를 아낀다.</summary>
    public bool DetectionEnabled { get; set; } = true;

    /// <summary>거리 컬러맵 하한(mm).</summary>
    public double MinRangeMm { get; set; } = 200;

    /// <summary>거리 컬러맵 상한(mm).</summary>
    public double MaxRangeMm { get; set; } = 3000;

    /// <summary>진폭 회색조의 최대값. 이 값 이상은 전부 흰색이 된다.</summary>
    public int AmplitudeMax { get; set; } = 1500;

    /// <summary>오버레이에서 능선으로 칠할 수직거리 임계(mm).</summary>
    public double RidgeBandMm { get; set; } = 12;

    /// <summary>
    /// 컬러맵 구간을 검출기 깊이 게이트와 자동으로 일치시킬지.
    ///
    /// 켜 두면 슬라이더 하나로 "검출기가 보는 영역"을 조정하면서 그 결과를 그대로 볼 수 있다.
    /// 반대로 전체 장면을 확인하고 싶을 때는 꺼서 컬러맵만 넓힌다.
    /// </summary>
    public bool FollowDetectorBand { get; set; } = true;
}

/// <summary>부분 변경용. null 인 항목은 그대로 둔다.</summary>
internal sealed record PreviewOptionsPatch
{
    public int? IntervalMs { get; init; }
    public bool? DetectionEnabled { get; init; }
    public double? MinRangeMm { get; init; }
    public double? MaxRangeMm { get; init; }
    public int? AmplitudeMax { get; init; }
    public double? RidgeBandMm { get; init; }
    public bool? FollowDetectorBand { get; init; }
}

/// <summary>검출기 파라미터 부분 변경용. 현장 튜닝 전용이며 휘발성이다.</summary>
internal sealed record DetectorOptionsPatch
{
    public double? InlierThresholdMm { get; init; }
    public double? MinDistanceMm { get; init; }
    public double? MaxDistanceMm { get; init; }
    public int? RansacIterations { get; init; }
    public int? MinPlaneInliers { get; init; }
    public double? MinPlaneAngleDeg { get; init; }
    public double? MaxRmsMm { get; init; }
    public double? MinRidgeLengthMm { get; init; }
    public double? MaxRidgeLengthMm { get; init; }
    public double? MinSampleFraction { get; init; }

    // 반원 코러게이션 검출 전용
    public double? PlaneInlierThresholdMm { get; init; }
    public double? CorrugationMinHeightMm { get; init; }
    public double? CorrugationMaxHeightMm { get; init; }
    public double? CorrugationWidthMm { get; init; }
    public double? CorrugationBandMarginMm { get; init; }
    public int? MinCorrugationPoints { get; init; }
    public int? MinArcPoints { get; init; }
    public double? PeakRelativeThreshold { get; init; }
    public int? MaxCandidates { get; init; }
    public double? ArcInlierThresholdMm { get; init; }
    public double? ExpectedRadiusMm { get; init; }
    public double? RadiusTolerance { get; init; }
}
