namespace HD.AMR.App.Service;

/// <summary>평탄면 검출 스냅샷의 진행 단계 — 카메라 페이지 상태 배지 표시용.</summary>
public enum FlatDetectionStage
{
    /// <summary>검출만 수행(코봇 이동 없음) — 카메라 페이지 '평탄 검증'.</summary>
    DetectOnly,
    /// <summary>선택 셀 중심으로 코봇 횡이동 중.</summary>
    Moving,
    /// <summary>선택 셀이 이미 ROI 중심 근방이라 이동 생략.</summary>
    MoveSkipped,
    /// <summary>횡이동 완료.</summary>
    Moved,
    /// <summary>레이저 3점 측정·틸트 보정 중.</summary>
    LaserMeasuring,
    /// <summary>정렬 완료.</summary>
    Done,
    /// <summary>카메라 페이지 '레이저 이동·측정' — 평탄 셀 위로 레이저를 옮겨 측정만 함(틸트 보정 없음).</summary>
    LaserMeasured,
    /// <summary>실패/취소.</summary>
    Failed,
}

/// <summary>
/// 평탄면 검출 1회의 결과 스냅샷. <see cref="Analysis"/> 는 검출 순간의 깊이 프레임과 전체 셀 σ 를 담고 있어,
/// 코봇이 이동해 라이브 영상이 바뀐 뒤에도 "어느 셀을 잡았는지"를 정지 화면으로 다시 그릴 수 있다.
/// </summary>
public sealed record FlatDetectionSnapshot(
    string Source,
    DateTime DetectedAt,
    CameraService.DepthGridAnalysis Analysis,
    double FullRoiX, double FullRoiY, double FullRoiW, double FullRoiH,
    double? DeltaXmm, double? DeltaYmm,
    FlatDetectionStage Stage,
    string? StageMessage = null,
    double? LaserRxDeg = null, double? LaserRyDeg = null,
    double? LaserZmm = null, IReadOnlyList<double>? LaserChannelsMm = null,
    double[]? TcpPoseAtDetect = null, int? Tool = null);

/// <summary>
/// 가장 최근 평탄면 검출 결과를 보관하고 변경을 알리는 싱글톤. 시퀀스(<see cref="Sequence.Steps.FlatSurfaceAlignStep"/>)·
/// 정렬 루틴(<see cref="FlatSurfaceCenteringService"/>, transient)·카메라 페이지가 이 한 곳을 공유한다.
/// <see cref="Changed"/> 는 발행한 스레드에서 호출되므로 UI 쪽에서 디스패처로 넘겨야 한다.
/// </summary>
public sealed class FlatDetectionMonitor
{
    private readonly object _lock = new();
    private FlatDetectionSnapshot? _latest;

    public FlatDetectionSnapshot? Latest
    {
        get { lock (_lock) return _latest; }
    }

    public event Action<FlatDetectionSnapshot?>? Changed;

    public void Publish(FlatDetectionSnapshot snapshot) => Set(_ => snapshot);

    /// <summary>현재 스냅샷의 단계만 갱신(스냅샷이 없으면 무시).</summary>
    public void UpdateStage(FlatDetectionStage stage, string? message = null)
        => Set(s => s is null ? null : s with { Stage = stage, StageMessage = message });

    /// <summary>레이저 3점 측정값 갱신(스냅샷이 없으면 무시). 채널 원시 거리는 있을 때만.</summary>
    public void SetLaser(double rxDeg, double ryDeg, double? zMm = null, IReadOnlyList<double>? channelsMm = null)
        => Set(s => s is null ? null : s with
        {
            LaserRxDeg = rxDeg, LaserRyDeg = ryDeg, LaserZmm = zMm,
            LaserChannelsMm = channelsMm ?? s.LaserChannelsMm,
        });

    public void Clear() => Set(_ => null);

    private void Set(Func<FlatDetectionSnapshot?, FlatDetectionSnapshot?> update)
    {
        FlatDetectionSnapshot? next;
        lock (_lock)
        {
            var prev = _latest;
            next = update(prev);
            if (ReferenceEquals(prev, next)) return;
            _latest = next;
        }
        Changed?.Invoke(next);
    }
}
