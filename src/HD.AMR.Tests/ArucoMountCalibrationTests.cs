using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class ArucoMountCalibrationTests
{
    [Fact]
    public void Solve_RecoversMountAndUnknownMarker()
    {
        double[] ab = [410, -85, 620, 1.2, -0.8, 4.5];
        double[] tc = [65, 4, 110, 0.3, -0.4, 1.0];
        double[] wq = [2350, -640, 2, 0, 0, 28];
        var samples = new List<ArucoMountSample>();
        for (int i = 0; i < 20; i++)
        {
            double[] wa = [300 * Math.Cos(i * .7), 260 * Math.Sin(i * .7), 0, 0, 0, -80 + i * 9];
            double[] bt = [520 + (i % 4) * 35, -140 + (i % 5) * 45, 480 + (i % 3) * 25, 178, (i % 4 - 2) * 4, 15 + i * 3];
            var wc = FrameMath.Multiply(FrameMath.Multiply(FrameMath.Multiply(FrameMath.PoseToMatrix(wa), FrameMath.PoseToMatrix(ab)), FrameMath.PoseToMatrix(bt)), FrameMath.PoseToMatrix(tc));
            var cq = FrameMath.MatrixToPose(FrameMath.Multiply(FrameMath.Invert(wc), FrameMath.PoseToMatrix(wq)));
            samples.Add(new(i, DateTime.UtcNow, wa, bt, cq, .2));
        }
        double[] initial = [390, -70, 600, 0, 0, 2];
        var r = ArucoMountCalibration.Solve(samples, tc, wq[2], initial);
        Assert.True(r.Success, r.Error);
        var delta = FrameMath.MatrixToPose(FrameMath.Multiply(FrameMath.Invert(FrameMath.PoseToMatrix(ab)), FrameMath.PoseToMatrix(r.MountPoseAB)));
        Assert.True(Math.Sqrt(delta[0] * delta[0] + delta[1] * delta[1] + delta[2] * delta[2]) < .05);
        Assert.True(Math.Abs(delta[3]) + Math.Abs(delta[4]) + Math.Abs(delta[5]) < .02);
        Assert.InRange(r.TranslationRmsMm, 0, .01);
    }

    [Fact]
    public void Solve_RejectsInsufficientPoseDiversity()
    {
        var s = Enumerable.Range(0, 8).Select(i => new ArucoMountSample(i, DateTime.UtcNow,
            [i, 0, 0, 0, 0, i], new double[6], [0, 0, 500, 180, 0, 0], .2)).ToList();
        var r = ArucoMountCalibration.Solve(s, new double[6], 0, new double[6]);
        Assert.False(r.Success);
    }
}
