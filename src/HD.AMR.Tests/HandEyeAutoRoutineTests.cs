using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class HandEyeAutoRoutineTests
{
    /// <summary>피벗 회전 오프셋: 오프셋 변환을 피벗점에 적용하면 제자리여야 한다(클램프 미발동 조건).</summary>
    [Theory]
    [InlineData(1, 0, 0, 20)]     // Rx
    [InlineData(0, 1, 0, -20)]    // Ry
    [InlineData(0, 0, 1, 25)]     // Rz
    [InlineData(0.70710678, 0.70710678, 0, 15)]   // 대각
    public void BuildPivotOffsetPose_KeepsPivotFixed(double ax, double ay, double az, double thetaDeg)
    {
        double[] pivot = [12, -8, 350];
        var pose = HandEyeAutoRoutine.BuildPivotOffsetPose([ax, ay, az], thetaDeg, pivot, maxTransMm: 500);

        var m = FrameMath.PoseToMatrix(pose);
        for (int i = 0; i < 3; i++)
        {
            double v = m[i, 0] * pivot[0] + m[i, 1] * pivot[1] + m[i, 2] * pivot[2] + m[i, 3];
            Assert.True(Math.Abs(v - pivot[i]) < 1e-6, $"성분 {i}: {v} != {pivot[i]}");
        }
    }

    [Fact]
    public void BuildPivotOffsetPose_RotationAboutAxisThroughPivot_IsIdentityAtZero()
    {
        var pose = HandEyeAutoRoutine.BuildPivotOffsetPose([0, 0, 1], 0, [0, 0, 400], 150);
        Assert.All(pose, v => Assert.True(Math.Abs(v) < 1e-9));
    }

    [Fact]
    public void BuildPivotOffsetPose_ClampsTranslation()
    {
        // 먼 피벗 + 큰 각도 → 보상 병진이 커진다. 클램프 한도를 넘지 않아야 한다.
        var pose = HandEyeAutoRoutine.BuildPivotOffsetPose([1, 0, 0], 30, [0, 0, 2000], maxTransMm: 150);
        double norm = Math.Sqrt(pose[0] * pose[0] + pose[1] * pose[1] + pose[2] * pose[2]);
        Assert.True(norm <= 150 + 1e-6, $"|t|={norm}");
        // 회전 성분은 클램프와 무관하게 유지된다.
        Assert.True(Math.Abs(pose[3] - 30) < 1e-6);
    }

    [Fact]
    public void EstimatePivotInTool_FallsBackToOpticalAxisWhenNoTtc()
    {
        double[] markerCQ = [15, -20, 480, 0, 0, 0];
        Assert.Equal([0, 0, 480], HandEyeAutoRoutine.EstimatePivotInTool(null, markerCQ));
        Assert.Equal([0, 0, 480], HandEyeAutoRoutine.EstimatePivotInTool(new double[6], markerCQ));
    }

    [Fact]
    public void EstimatePivotInTool_UsesTtcWhenAvailable()
    {
        // T_T_C = 순수 병진 [50, 0, 100] → p_T = p_C + [50, 0, 100].
        double[] ttc = [50, 0, 100, 0, 0, 0];
        double[] markerCQ = [10, 20, 400, 0, 0, 0];
        var p = HandEyeAutoRoutine.EstimatePivotInTool(ttc, markerCQ);
        Assert.True(Math.Abs(p[0] - 60) < 1e-9);
        Assert.True(Math.Abs(p[1] - 20) < 1e-9);
        Assert.True(Math.Abs(p[2] - 500) < 1e-9);
    }
}
