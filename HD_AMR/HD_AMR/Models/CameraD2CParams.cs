namespace HD_AMR.Models;

/// <summary>
/// 카메라의 Depth↔Color 정합용 공장 캘리브레이션(내부 + 외부) 파라미터의 관리형 스냅샷.
/// Orbbec SDK <c>ob_pipeline_get_camera_param</c> 결과를 네이티브 구조체 노출 없이 담는다.
/// 회전 <see cref="Rot"/>(3×3, row-major 9개) · 평행이동 <see cref="Trans"/>(3, mm)은
/// <b>Depth → Color</b> 변환이다.
/// </summary>
public sealed record CameraD2CParams(
    double DepthFx, double DepthFy, double DepthCx, double DepthCy, int DepthW, int DepthH,
    double ColorFx, double ColorFy, double ColorCx, double ColorCy, int ColorW, int ColorH,
    double[] Rot,
    double[] Trans)
{
    /// <summary>내부 파라미터가 유효한지(초점거리 양수).</summary>
    public bool IsValid => DepthFx > 0 && ColorFx > 0 && Rot.Length == 9 && Trans.Length == 3;
}
