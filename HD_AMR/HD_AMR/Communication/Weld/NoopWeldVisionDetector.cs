using HD_AMR.Models;

namespace HD_AMR.Communication.Weld;

/// <summary>
/// OpenCV 네이티브가 없는 플랫폼용 폴백. 항상 "검출 비활성" 결과를 돌려준다.
/// 고전/DL 두 인터페이스를 모두 만족해 비-Windows 에서 양쪽 등록에 재사용된다.
/// </summary>
public sealed class NoopWeldVisionDetector : IDlWeldVisionDetector
{
    public bool IsAvailable => false;

    public WeldDetectionResult DetectWeld(
        CameraFrame frame, RoiRect weldRoi, WeldDetectionParams p,
        WeldReferenceMode referenceMode = WeldReferenceMode.FovCenter, double? peakReferencePos = null,
        double? peakProgressPos = null, string? peakLabel = null, RoiRect? peakRoi = null,
        double? peakCrossStart = null, double? peakCrossEnd = null)
        => WeldDetectionResult.Fail("OpenCV 네이티브가 없어 용접라인 검출이 비활성 상태입니다(Windows에서만 지원).");
}
