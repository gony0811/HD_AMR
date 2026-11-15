using HD.AMR.App.Communication;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 연속 이동(같은 anchor 그룹 다음 task) MoveL 경로 샘플링 — 위치는 선형, 자세는 축-각 보간.
/// 경로 위 각 점을 IK 로 이어 풀어 손목 반전·J5 여유·J6 범위를 보기 위한 점 목록이다.
/// </summary>
public class ChainPathSamplingTests
{
    private static double RelAngleDeg(double[] a, double[] b)
    {
        var ma = FrameMath.PoseToMatrix(a);
        var mb = FrameMath.PoseToMatrix(b);
        var tr = 0.0;
        for (var i = 0; i < 3; i++)
            for (var k = 0; k < 3; k++)
                tr += ma[k, i] * mb[k, i];
        return Math.Acos(Math.Clamp((tr - 1) / 2, -1, 1)) * 180 / Math.PI;
    }

    [Fact]
    public void 순수_평행이동은_위치_간격으로_나누고_자세는_그대로()
    {
        double[] from = { 0, 0, 500, 180, 0, 90 };
        double[] to = { 450, 0, 500, 180, 0, 90 };

        var pts = CobotInspectionMoveStep.SamplePoseSegment(from, to, 100, 10);

        Assert.Equal(5, pts.Count);                    // ceil(450/100)
        Assert.Equal(90, pts[0][0], 6);
        Assert.All(pts, p => Assert.True(RelAngleDeg(p, from) < 1e-6));
        Assert.Equal(to, pts[^1]);
    }

    [Fact]
    public void 자세만_돌면_회전_간격으로_나눈다()
    {
        double[] from = { 300, 0, 500, 0, 0, 0 };
        double[] to = { 300, 0, 500, 0, 0, 90 };

        var pts = CobotInspectionMoveStep.SamplePoseSegment(from, to, 100, 10);

        Assert.Equal(9, pts.Count);                    // ceil(90°/10°), 위치 이동 0
        Assert.Equal(50, pts[4][5], 6);                // k=5/9 → 50°
    }

    [Fact]
    public void 중간_자세는_시작과_끝_사이_상대회전을_비율대로_나눈다()
    {
        double[] from = { -800, 300, 700, 170, -20, -95 };
        double[] to = { -650, 420, 760, 160, -35, -60 };
        var total = RelAngleDeg(from, to);

        var pts = CobotInspectionMoveStep.SamplePoseSegment(from, to, 100, 10);

        for (var k = 0; k < pts.Count; k++)
        {
            var t = (double)(k + 1) / pts.Count;
            Assert.Equal(total * t, RelAngleDeg(from, pts[k]), 4);
            Assert.Equal(total * (1 - t), RelAngleDeg(pts[k], to), 4);
            Assert.Equal(from[0] + (to[0] - from[0]) * t, pts[k][0], 6);
        }
    }
}
