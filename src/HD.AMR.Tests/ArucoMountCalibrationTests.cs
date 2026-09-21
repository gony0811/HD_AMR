using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class ArucoMountCalibrationTests
{
    [Fact]
    public void AutoTargets_AreExpressedInInitialAmrFrame()
    {
        var start = new RobotPose(10, 20, (float)(Math.PI / 2));

        var targets = ArucoMountAutoRoutine.BuildTargets(start, .25, .30, 45);

        Assert.Equal(4, targets.Count);
        // 시작 yaw=90°이므로 로컬 +X 250mm는 월드 +Y가 된다.
        Assert.InRange(targets[1].X, 9.9999f, 10.0001f);
        Assert.InRange(targets[1].Y, 20.2499f, 20.2501f);
        Assert.Equal(135, targets[1].Angle * 180 / Math.PI, 3);
        // 로컬 +Y 300mm는 월드 -X가 된다.
        Assert.InRange(targets[2].X, 9.6999f, 9.7001f);
        Assert.InRange(targets[2].Y, 19.9999f, 20.0001f);
    }

    [Fact]
    public void CalculateViewToolPose_KeepsCameraFixedInWorld()
    {
        var mount = new[] { 400.0, -80, 620, 1, -1, 3 };
        var tc = new[] { 60.0, 5, 100, .2, -.3, 1 };
        var firstAmr = new RobotPose(1, 2, .2f);
        var firstTool = new[] { 500.0, -120, 450, 178, 2, 15 };
        var firstWa = new[] { 1000.0, 2000, 0, 0, 0, firstAmr.Angle * 180 / Math.PI };
        var cameraW = FrameMath.Multiply(FrameMath.Multiply(FrameMath.Multiply(
            FrameMath.PoseToMatrix(firstWa), FrameMath.PoseToMatrix(mount)),
            FrameMath.PoseToMatrix(firstTool)), FrameMath.PoseToMatrix(tc));
        var nextAmr = new RobotPose(1.25f, 1.8f, -.3f);

        var nextTool = ArucoMountAutoRoutine.CalculateViewToolPose(nextAmr, mount, tc, cameraW);
        var nextWa = new[] { 1250.0, 1800, 0, 0, 0, nextAmr.Angle * 180 / Math.PI };
        var reconstructed = FrameMath.Multiply(FrameMath.Multiply(FrameMath.Multiply(
            FrameMath.PoseToMatrix(nextWa), FrameMath.PoseToMatrix(mount)),
            FrameMath.PoseToMatrix(nextTool)), FrameMath.PoseToMatrix(tc));
        var error = FrameMath.MatrixToPose(FrameMath.Multiply(FrameMath.Invert(cameraW), reconstructed));

        Assert.All(error, value => Assert.InRange(Math.Abs(value), 0, 1e-6));
    }

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
