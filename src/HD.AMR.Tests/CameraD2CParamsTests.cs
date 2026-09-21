using HD.AMR.App.Models;

namespace HD.AMR.Tests;

/// <summary>
/// 색상 렌즈 왜곡 규약 처리 — RealSense 는 색상 센서 왜곡을 정방향(Brown-Conrady) 또는
/// 역방향(Inverse Brown-Conrady)으로 보고한다. ArUco PnP 는 정방향이면 계수를 solvePnP 에 넘기고,
/// 역방향이면 코너를 먼저 편 뒤 무왜곡으로 푼다. 두 경로가 같은 물리 왜곡을 상쇄해야 한다.
/// </summary>
public class CameraD2CParamsTests
{
    private static CameraD2CParams Make(string model, double[]? coeffs) => new(
        DepthFx: 640, DepthFy: 640, DepthCx: 640, DepthCy: 360, DepthW: 1280, DepthH: 720,
        ColorFx: 910, ColorFy: 910, ColorCx: 640, ColorCy: 360, ColorW: 1280, ColorH: 720,
        Rot: new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, Trans: new double[] { 0, 0, 0 },
        ColorDistortion: model, ColorCoeffs: coeffs);

    [Fact]
    public void NoCoefficients_IsTreatedAsUndistorted()
    {
        var p = Make("inverse_brown_conrady", new double[5]);
        Assert.False(p.HasColorDistortion);
        Assert.False(p.ColorDistortionIsForward);
        Assert.False(p.ColorDistortionIsInverse);
        Assert.All(p.OpenCvColorDistCoeffs(), v => Assert.Equal(0, v));
        Assert.Equal((100.0, 200.0), p.ColorUndistortPixel(100, 200, 910, 910, 640, 360));
    }

    [Fact]
    public void ForwardModel_PassesCoefficientsThrough_AndDoesNotUndistortPixels()
    {
        double[] c = [-0.05, 0.06, 0.001, -0.002, 0.0];
        var p = Make("brown_conrady", c);
        Assert.True(p.ColorDistortionIsForward);
        Assert.False(p.ColorDistortionIsInverse);
        Assert.Equal(c, p.OpenCvColorDistCoeffs());
        Assert.Equal((100.0, 200.0), p.ColorUndistortPixel(100, 200, 910, 910, 640, 360));
    }

    [Fact]
    public void ModifiedBrownConrady_CountsAsForward()
    {
        var p = Make("modifiedbrownconrady", [0.1, 0, 0, 0, 0]);
        Assert.True(p.ColorDistortionIsForward);
    }

    /// <summary>
    /// 역방향 모델은 "왜곡 픽셀 → 무왜곡 픽셀" 매핑이다. 정방향 Brown-Conrady 로 왜곡시킨 픽셀에
    /// 역방향 계수(같은 계수의 1차 근사)를 적용하면 원래 픽셀 근처로 돌아와야 한다 — 부호 규약 검증.
    /// </summary>
    [Fact]
    public void InverseModel_UndistortsPixelsBackTowardIdeal()
    {
        const double fx = 910, fy = 910, cx = 640, cy = 360;
        double k1 = -0.04, k2 = 0.01;
        // 정방향 왜곡: x_d = x(1 + k1 r² + k2 r⁴)
        double xi = 0.55, yi = -0.3;                       // 이상 정규화 좌표(화면 가장자리 근처)
        double r2 = xi * xi + yi * yi;
        double f = 1 + k1 * r2 + k2 * r2 * r2;
        double ud = xi * f * fx + cx, vd = yi * f * fy + cy;   // 왜곡 픽셀

        // 역방향 계수는 정방향의 부호 반전에 가깝다(1차 근사). 라운드트립이 원래보다 훨씬 가까워져야 한다.
        var p = Make("inverse_brown_conrady", [-k1, -k2, 0, 0, 0]);
        Assert.True(p.ColorDistortionIsInverse);
        Assert.All(p.OpenCvColorDistCoeffs(), v => Assert.Equal(0, v));   // 역방향은 PnP 에 계수를 넘기지 않는다
        var (uu, vv) = p.ColorUndistortPixel(ud, vd, fx, fy, cx, cy);

        double before = Math.Sqrt(Math.Pow(ud - (xi * fx + cx), 2) + Math.Pow(vd - (yi * fy + cy), 2));
        double after = Math.Sqrt(Math.Pow(uu - (xi * fx + cx), 2) + Math.Pow(vv - (yi * fy + cy), 2));
        Assert.True(before > 5, $"왜곡이 의미 있어야 한다: {before}px");
        Assert.True(after < before * 0.15, $"펴기 전 {before:0.00}px → 후 {after:0.00}px");
    }
}
