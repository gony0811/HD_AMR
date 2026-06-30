using HD_AMR.Models;

namespace HD_AMR.Communication.Weld;

/// <summary>
/// 용접라인 검출기 추상화. 실제 구현은 OpenCvSharp(네이티브 OpenCV) 기반이며, 네이티브 런타임이
/// 없는 플랫폼(예: Jetson linux-arm64, 미설치 환경)에서는 <see cref="NoopWeldVisionDetector"/> 가
/// 주입되어 앱이 죽지 않고 "검출 비활성" 으로 동작한다.
/// </summary>
public interface IWeldVisionDetector
{
    /// <summary>OpenCV 네이티브가 사용 가능한지(=실제 검출 가능 여부).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 컬러/IR 프레임 한 장에서 ROI 내부의 용접비드 중심선과 위치 오차 d(픽셀)를 검출한다.
    /// </summary>
    /// <param name="frame">컬러(mjpg/rgb24) 또는 IR(ir8/ir16) 프레임.</param>
    /// <param name="weldRoi">Weld 검출 ROI(이미지 픽셀 좌표).</param>
    /// <param name="p">검출 파라미터.</param>
    /// <param name="referenceMode">d 기준선.</param>
    /// <param name="peakReferencePos">PeakLine 모드일 때 기준선 cross-axis 위치(픽셀). 없으면 FOV 중심 사용.</param>
    /// <param name="peakProgressPos">Peak 의 진행축 위치(픽셀). 주면 오버레이에 자홍색 Peak 선을 그린다.</param>
    /// <param name="peakLabel">Peak 선 라벨(예: P1/P2). 없으면 라벨 생략.</param>
    /// <param name="peakRoi">Peak ROI(이미지 픽셀 좌표). 자홍 Peak 선 폴백(cross 구간 없을 때)의 범위.</param>
    /// <param name="peakCrossStart">Peak 슬라이스 비드 cross 구간 시작(픽셀, 검출 프레임). 주면 자홍선을
    /// 이 구간만큼만 그린다(depth 급변점 기반). 없으면 중심선 위 고정 틱으로 폴백.</param>
    /// <param name="peakCrossEnd">위 구간의 끝(픽셀, 검출 프레임).</param>
    WeldDetectionResult DetectWeld(
        CameraFrame frame,
        RoiRect weldRoi,
        WeldDetectionParams p,
        WeldReferenceMode referenceMode = WeldReferenceMode.FovCenter,
        double? peakReferencePos = null,
        double? peakProgressPos = null,
        string? peakLabel = null,
        RoiRect? peakRoi = null,
        double? peakCrossStart = null,
        double? peakCrossEnd = null);
}
