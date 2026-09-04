using HD_AMR.Communication;
using HD_AMR.Service;

namespace HD_AMR.Tests;

/// <summary>
/// QR 위치 검증 변환 체인의 순수 수학 검증. 합성 ground-truth 로 라운드트립:
/// T_C_Q = inv(T_W_A·T_A_B·T_B_F·T_F_C)·T_W_Q 를 만들어 넣으면
/// SolveAmrPose 가 T_W_A 의 평면 성분을 그대로 복원해야 한다.
/// </summary>
public class QrLocalizationTests
{
    private const double Tol = 1e-9;

    private static double[] ChainCQ(double[] tWA, double[] tAB, double[] tBF, double[] tFC, double[] tWQ)
    {
        var tWC = FrameMath.Multiply(
            FrameMath.Multiply(FrameMath.PoseToMatrix(tWA), FrameMath.PoseToMatrix(tAB)),
            FrameMath.Multiply(FrameMath.PoseToMatrix(tBF), FrameMath.PoseToMatrix(tFC)));
        return FrameMath.MatrixToPose(FrameMath.Multiply(FrameMath.Invert(tWC), FrameMath.PoseToMatrix(tWQ)));
    }

    private static void AssertMatrixEqual(double[] poseA, double[] poseB, double tol)
    {
        var a = FrameMath.PoseToMatrix(poseA);
        var b = FrameMath.PoseToMatrix(poseB);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True(Math.Abs(a[i, j] - b[i, j]) < tol,
                    $"[{i},{j}] {a[i, j]} != {b[i, j]}");
    }

    [Fact]
    public void SolveAmrPose_RoundTrip_RecoversPlanarPose()
    {
        var reg = new MapRegistration(12.5, 3000, -2000, 0, 3);
        var marker = new QrMarkerReg { Text = "QR1", Gx = 1500, Gy = 800, Zmm = 1200, AzimuthDegG = 30, SizeMm = 150 };
        var tWQ = QrLocalization.MarkerWorldPose(reg, marker);

        var tWA = new[] { 3200.0, -1500, 0, 0, 0, 137 };
        var tAB = new[] { 120.0, -80, 250, 0, 0, 15 };
        var tBF = new[] { 400.0, 100, 600, 10, -20, 30 };
        var tFC = new[] { 30.0, -40, 50, 5, -3, 90 };
        var tCQ = ChainCQ(tWA, tAB, tBF, tFC, tWQ);

        var r = QrLocalization.SolveAmrPose(tWQ, tCQ, tFC, tBF, tAB);

        Assert.Equal(tWA[0], r.Xmm, Tol);
        Assert.Equal(tWA[1], r.Ymm, Tol);
        Assert.Equal(tWA[5], r.ThetaDeg, Tol);
        Assert.Equal(0, r.ZResidMm, Tol);
        Assert.Equal(0, r.RollDeg, Tol);
        Assert.Equal(0, r.PitchDeg, Tol);
    }

    [Fact]
    public void SolveAmrPose_RandomPoses_RoundTrip()
    {
        var rnd = new Random(42);
        double R(double lo, double hi) => lo + rnd.NextDouble() * (hi - lo);

        var reg = new MapRegistration(R(-180, 180), R(-5000, 5000), R(-5000, 5000), 0, 3);
        for (int n = 0; n < 20; n++)
        {
            var marker = new QrMarkerReg
            {
                Gx = R(-3000, 3000), Gy = R(-3000, 3000), Zmm = R(500, 2000),
                AzimuthDegG = R(-180, 180), SizeMm = 150,
            };
            var tWQ = QrLocalization.MarkerWorldPose(reg, marker);
            var tWA = new[] { R(-5000, 5000), R(-5000, 5000), 0, 0, 0, R(-180, 180) };
            var tAB = new[] { R(-300, 300), R(-300, 300), R(0, 400), 0, 0, R(-180, 180) };
            var tBF = new[] { R(-800, 800), R(-800, 800), R(200, 900), R(-45, 45), R(-45, 45), R(-180, 180) };
            var tFC = new[] { R(-60, 60), R(-60, 60), R(-60, 60), R(-30, 30), R(-30, 30), R(-180, 180) };
            var tCQ = ChainCQ(tWA, tAB, tBF, tFC, tWQ);

            var r = QrLocalization.SolveAmrPose(tWQ, tCQ, tFC, tBF, tAB);
            Assert.Equal(tWA[0], r.Xmm, 1e-6);
            Assert.Equal(tWA[1], r.Ymm, 1e-6);
            Assert.Equal(0, QrLocalization.AngleDiffDeg(r.ThetaDeg, tWA[5]), 1e-6);
            Assert.Equal(0, r.ZResidMm, 1e-6);
        }
    }

    [Fact]
    public void SolveAmrPose_ThetaWraparound()
    {
        var reg = new MapRegistration(0, 0, 0, 0, 2);
        var marker = new QrMarkerReg { Gx = 1000, Gy = 0, Zmm = 1000, AzimuthDegG = 180, SizeMm = 150 };
        var tWQ = QrLocalization.MarkerWorldPose(reg, marker);

        var tWA = new[] { 0.0, 0, 0, 0, 0, 179.7 };
        var tAB = new double[] { 0, 0, 200, 0, 0, 0 };
        var tBF = new double[] { 300, 0, 500, 0, 0, 0 };
        var tFC = new double[] { 0, 0, 0, 0, 0, 0 };
        var tCQ = ChainCQ(tWA, tAB, tBF, tFC, tWQ);

        var r = QrLocalization.SolveAmrPose(tWQ, tCQ, tFC, tBF, tAB);
        Assert.Equal(0, QrLocalization.AngleDiffDeg(r.ThetaDeg, 179.7), 1e-9);
    }

    [Fact]
    public void MarkerWorldPose_AxisConvention()
    {
        // 정합 항등(θ=0,t=0), 방위각 ψ=0: Z축=맵 +X(법선), Y축=맵 +Z(코드 위쪽), X축=맵 +Y.
        var reg = new MapRegistration(0, 0, 0, 0, 2);
        var m0 = FrameMath.PoseToMatrix(QrLocalization.MarkerWorldPose(
            reg, new QrMarkerReg { Gx = 100, Gy = 200, Zmm = 1500, AzimuthDegG = 0 }));

        Assert.Equal(1, m0[0, 2], Tol);  // Z축 = (1,0,0)
        Assert.Equal(0, m0[1, 2], Tol);
        Assert.Equal(0, m0[2, 2], Tol);
        Assert.Equal(1, m0[2, 1], Tol);  // Y축 = (0,0,1)
        Assert.Equal(0, m0[0, 0], Tol);  // X축 = (0,1,0)
        Assert.Equal(1, m0[1, 0], Tol);
        Assert.Equal(100, m0[0, 3], Tol);
        Assert.Equal(200, m0[1, 3], Tol);
        Assert.Equal(1500, m0[2, 3], Tol);

        // ψ=90°: Z축 = (0,1,0)
        var m90 = FrameMath.PoseToMatrix(QrLocalization.MarkerWorldPose(
            reg, new QrMarkerReg { AzimuthDegG = 90 }));
        Assert.Equal(0, m90[0, 2], Tol);
        Assert.Equal(1, m90[1, 2], Tol);

        // ψ=180°: Z축 = (−1,0,0)
        var m180 = FrameMath.PoseToMatrix(QrLocalization.MarkerWorldPose(
            reg, new QrMarkerReg { AzimuthDegG = 180 }));
        Assert.Equal(-1, m180[0, 2], Tol);

        // 정합 회전 θ 가 방위각에 더해진다: az=10 + θ=20 → ψ=30 → Z=(cos30,sin30,0)
        var reg2 = new MapRegistration(20, 0, 0, 0, 2);
        var m30 = FrameMath.PoseToMatrix(QrLocalization.MarkerWorldPose(
            reg2, new QrMarkerReg { AzimuthDegG = 10 }));
        Assert.Equal(Math.Cos(30 * Math.PI / 180), m30[0, 2], Tol);
        Assert.Equal(Math.Sin(30 * Math.PI / 180), m30[1, 2], Tol);
    }

    [Fact]
    public void SolveHandEye_RecoversGroundTruth()
    {
        var reg = new MapRegistration(-8, 1000, 500, 0, 3);
        var marker = new QrMarkerReg { Gx = -500, Gy = 1200, Zmm = 900, AzimuthDegG = -45, SizeMm = 200 };
        var tWQ = QrLocalization.MarkerWorldPose(reg, marker);

        var tWA = new[] { 700.0, -300, 0, 0, 0, -60 };
        var tAB = new[] { 100.0, 50, 300, 0, 0, 90 };
        var tBF = new[] { 250.0, -150, 700, 15, 25, -40 };
        var tFC = new[] { 20.0, -35, 45, 2, -1, 88 };
        var tCQ = ChainCQ(tWA, tAB, tBF, tFC, tWQ);

        var solved = QrLocalization.SolveHandEye(tWQ, tCQ, tBF, tAB, tWA);
        AssertMatrixEqual(tFC, solved, 1e-9);
    }

    [Theory]
    [InlineData(-170, 170, 20)]
    [InlineData(170, -170, -20)]
    [InlineData(10, 350, 20)]
    [InlineData(90, 90, 0)]
    public void AngleDiffDeg_Wraps(double a, double b, double expected)
        => Assert.Equal(expected, QrLocalization.AngleDiffDeg(a, b), 1e-12);

    [Fact]
    public void CircularMeanDeg_Wraparound()
    {
        var mean = QrLocalization.CircularMeanDeg(new[] { 179.0, -179.0 });
        Assert.Equal(0, Math.Abs(QrLocalization.AngleDiffDeg(mean, 180)), 1e-9);
    }
}
