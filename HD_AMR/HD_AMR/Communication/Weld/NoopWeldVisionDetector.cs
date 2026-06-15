using HD_AMR.Models;

namespace HD_AMR.Communication.Weld;

/// <summary>
/// OpenCV 네이티브가 없는 플랫폼용 폴백. 항상 "검출 비활성" 결과를 돌려준다.
/// </summary>
public sealed class NoopWeldVisionDetector : IWeldVisionDetector
{
    public bool IsAvailable => false;

    public WeldDetectionResult DetectWeld(
        CameraFrame frame, RoiRect weldRoi, WeldDetectionParams p,
        WeldReferenceMode referenceMode = WeldReferenceMode.FovCenter, double? peakReferencePos = null)
        => WeldDetectionResult.Fail("OpenCV 네이티브가 없어 용접라인 검출이 비활성 상태입니다(Windows에서만 지원).");
}
