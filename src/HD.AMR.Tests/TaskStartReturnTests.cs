using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>점2(끝쪽) 교시 뒤 검사 시작점으로 돌아가는 목표 선택. 점2 포즈는 쓰지 않는다.</summary>
public class TaskStartReturnTests
{
    private static double[] Pose(double x) => new[] { x, 0, 0, 0, 0, 0 };

    [Fact]
    public void 비드1_센터링_자세를_시작점으로_쓰고_점2는_버린다()
    {
        var bag = new Dictionary<string, object>
        {
            [WeldSequenceSupport.Bead1CenteredPoseBagKey] = Pose(10),
            [WObjPointStep.PointPoseBagKey(1)] = Pose(20),
            [WObjPointStep.PointPoseBagKey(2)] = Pose(999),
        };

        var pose = TaskStartReturnStep.SelectStartPose(bag, out var source, out var ignored);
        Assert.NotNull(pose);
        Assert.Equal(10, pose![0]);
        Assert.NotSame(bag[WeldSequenceSupport.Bead1CenteredPoseBagKey], pose);
        Assert.Contains("비드1", source);
        Assert.Contains("점2", ignored);
    }

    [Fact]
    public void 비드1이_없으면_작업물_점1로_돌아간다()
    {
        var bag = new Dictionary<string, object>
        {
            [WObjPointStep.PointPoseBagKey(1)] = Pose(20),
            [WObjPointStep.PointPoseBagKey(2)] = Pose(999),
        };

        var pose = TaskStartReturnStep.SelectStartPose(bag, out var source, out _);
        Assert.Equal(20, pose![0]);
        Assert.Contains("점1", source);
    }

    [Fact]
    public void 끝점만_있으면_시작점으로_삼지_않는다()
    {
        var bag = new Dictionary<string, object>
        {
            [WObjPointStep.PointPoseBagKey(2)] = Pose(999),
        };

        Assert.Null(TaskStartReturnStep.SelectStartPose(bag, out _, out var ignored));
        Assert.Contains("점2", ignored);
    }

    [Fact]
    public void 검사_시작_재탐색은_수평_얼라인_단계와_키가_다르다()
    {
        Assert.Equal("flatSurfaceSearchTaskStart", FlatSurfaceAlignStep.TaskStartKey);
        Assert.Equal("laserWorkingDistanceTaskStart", LaserWorkingDistanceStep.TaskStartKey);
        Assert.Equal(1175, new TaskStartReturnStep(null!, null!).DefaultOrder);
    }
}
