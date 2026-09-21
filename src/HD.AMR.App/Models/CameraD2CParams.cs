namespace HD.AMR.App.Models;

/// <summary>
/// 카메라의 Depth↔Color 정합용 공장 캘리브레이션(내부 + 외부) 파라미터의 관리형 스냅샷.
/// librealsense 의 intrinsics/extrinsics 조회 결과를 SDK 타입 노출 없이 담는다.
/// 회전 <see cref="Rot"/>(3×3, row-major 9개) · 평행이동 <see cref="Trans"/>(3, mm)은
/// <b>Depth → Color</b> 변환이다.
///
/// 색상 렌즈 왜곡은 <see cref="ColorDistortion"/>(librealsense 모델명 소문자, 예:
/// <c>"brown_conrady"</c>, <c>"inverse_brown_conrady"</c>, <c>"none"</c>)과 계수 5개
/// <see cref="ColorCoeffs"/>([k1,k2,p1,p2,k3])로 담는다. 모델에 따라 계수의 의미가 다르다:
/// Brown-Conrady 계열은 "정규화 좌표 → 왜곡 좌표"(OpenCV 와 동일 규약)이고,
/// Inverse Brown-Conrady 는 "왜곡 좌표 → 정규화 좌표"(역방향)라 OpenCV 에 그대로 넘기면 안 된다.
/// 소비자는 <see cref="ColorUndistortPixel"/>·<see cref="OpenCvColorDistCoeffs"/> 로 규약 차이를 흡수한다.
/// </summary>
public sealed record CameraD2CParams(
    double DepthFx, double DepthFy, double DepthCx, double DepthCy, int DepthW, int DepthH,
    double ColorFx, double ColorFy, double ColorCx, double ColorCy, int ColorW, int ColorH,
    double[] Rot,
    double[] Trans,
    string ColorDistortion = "none",
    double[]? ColorCoeffs = null)
{
    /// <summary>내부 파라미터가 유효한지(초점거리 양수).</summary>
    public bool IsValid => DepthFx > 0 && ColorFx > 0 && Rot.Length == 9 && Trans.Length == 3;

    /// <summary>색상 왜곡 계수가 하나라도 0 이 아닌지.</summary>
    public bool HasColorDistortion => ColorCoeffs is { Length: >= 5 } c && c.Any(v => Math.Abs(v) > 1e-12);

    /// <summary>색상 왜곡 모델이 OpenCV 규약(정방향 Brown-Conrady)과 같은지 — 그러면 계수를 PnP 에 그대로 넘긴다.</summary>
    public bool ColorDistortionIsForward =>
        HasColorDistortion && ColorDistortion.Contains("brown", StringComparison.OrdinalIgnoreCase)
                           && !ColorDistortion.Contains("inverse", StringComparison.OrdinalIgnoreCase);

    /// <summary>색상 왜곡 모델이 역방향(Inverse Brown-Conrady)인지 — 그러면 픽셀을 먼저 펴고 무왜곡 PnP 를 한다.</summary>
    public bool ColorDistortionIsInverse =>
        HasColorDistortion && ColorDistortion.Contains("inverse", StringComparison.OrdinalIgnoreCase);

    /// <summary>OpenCV solvePnP 에 넘길 색상 왜곡 계수 [k1,k2,p1,p2,k3]. 정방향 모델일 때만 실제 값, 그 외 0.</summary>
    public double[] OpenCvColorDistCoeffs()
        => ColorDistortionIsForward ? (double[])ColorCoeffs!.Clone() : new double[5];

    /// <summary>
    /// Inverse Brown-Conrady 계수로 색상 픽셀 좌표를 무왜곡 픽셀 좌표로 변환한다
    /// (librealsense <c>rs2_deproject_pixel_to_point</c> 의 INVERSE_BROWN_CONRADY 분기와 같은 식).
    /// 역방향 모델이 아니면 입력을 그대로 돌려준다. <paramref name="fx"/>~<paramref name="cy"/> 는
    /// 해당 프레임 해상도에 맞게 스케일된 내부 파라미터여야 한다.
    /// </summary>
    public (double U, double V) ColorUndistortPixel(double u, double v, double fx, double fy, double cx, double cy)
    {
        if (!ColorDistortionIsInverse) return (u, v);
        var c = ColorCoeffs!;
        double x = (u - cx) / fx, y = (v - cy) / fy;
        double r2 = x * x + y * y;
        double f = 1 + c[0] * r2 + c[1] * r2 * r2 + c[4] * r2 * r2 * r2;
        double ux = x * f + 2 * c[2] * x * y + c[3] * (r2 + 2 * x * x);
        double uy = y * f + 2 * c[3] * x * y + c[2] * (r2 + 2 * y * y);
        return (ux * fx + cx, uy * fy + cy);
    }
}
