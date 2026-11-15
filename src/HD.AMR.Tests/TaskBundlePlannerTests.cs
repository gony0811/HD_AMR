using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class TaskBundlePlannerTests
{
    private static BundleTaskInput Task(string id, string wall, double? lift, InspectionMoveDirection? dir,
        double startX, double endX = 1)
        => new(id, id, wall, lift, dir, new[] { startX, 0, 1.0 }, new[] { startX + endX, 0, 1.0 });

    [Fact]
    public void 리프트_면_방향이_같으면_첫_시작과_마지막_시작으로_묶는다()
    {
        var tasks = new[]
        {
            Task("a", "SM", 400, InspectionMoveDirection.Horizontal, 0),
            Task("b", "SM", 400, InspectionMoveDirection.Horizontal, 0.36),
            Task("c", "sm", 400.4, InspectionMoveDirection.Horizontal, 0.72),
        };
        var plan = TaskBundlePlanner.Assign(tasks);
        Assert.Equal(BundleRole.Leader, plan[0].Role);
        Assert.Equal(BundleRole.Follower, plan[1].Role);
        Assert.Equal(BundleRole.Follower, plan[2].Role);
        Assert.Equal(plan[0].BundleId, plan[2].BundleId);
        Assert.Equal(0, plan[0].FirstStartW![0]);
        Assert.Equal(0.72, plan[0].LastStartW![0]);
    }

    // 실기 2026-11-12 03:56 천장 L4-R03C17: AMR 이 용접선에 45° 로 서서 가로·세로 선이 모두 Horizontal 로 분류됐고,
    // CROSS4 뒤 90° 꺾인 V04 가 앞 가로선 좌표계를 물려받아 순회했다(비상정지).
    [Fact]
    public void 분류가_같아도_90도_꺾인_용접선은_묶지_않는다()
    {
        var h = InspectionMoveDirection.Horizontal;
        var plan = TaskBundlePlanner.Assign(new[]
        {
            Task("h1", "T", 0, h, 0, 0.36),
            Task("h2", "T", 0, h, 0.36, 0.36),
            new BundleTaskInput("v4", "v4", "T", 0, h, new[] { 0.36, 0.0, 1.0 }, new[] { 0.36, -0.36, 1.0 }),
        });
        Assert.Equal(BundleRole.Leader, plan[0].Role);
        Assert.Equal(BundleRole.Follower, plan[1].Role);
        Assert.Equal(BundleRole.None, plan[2].Role);
        Assert.Equal(0.36, plan[0].LastStartW![0]);   // 점2 는 꺾인 선이 아니라 h2 시작점
    }

    [Theory]
    [InlineData(0.36, 0.0, true)]       // 같은 방향
    [InlineData(0.36, 0.10, true)]      // 15.5° — 허용
    [InlineData(0.36, 0.16, false)]     // 24° — 꺾임
    [InlineData(0.0, 0.36, false)]      // 90°
    [InlineData(-0.36, 0.0, false)]     // 반대 방향
    [InlineData(0.0, 0.0, false)]       // 길이 0
    public void 같은_줄_판정(double dx, double dy, bool expected)
    {
        var a0 = new[] { 1.0, 2.0, 3.0 };
        var a1 = new[] { 1.36, 2.0, 3.0 };
        var b0 = new[] { 5.0, 5.0, 3.0 };
        Assert.Equal(expected, TaskBundlePlanner.SameSeamLine(a0, a1, b0, new[] { 5.0 + dx, 5.0 + dy, 3.0 }));
        Assert.False(TaskBundlePlanner.SameSeamLine(a0, a1, b0, null));
    }

    [Fact]
    public void 리프트가_다르면_묶지_않는다()
    {
        var plan = TaskBundlePlanner.Assign(new[]
        {
            Task("a", "SM", 0, InspectionMoveDirection.Horizontal, 0),
            Task("b", "SM", 500, InspectionMoveDirection.Horizontal, 1),
        });
        Assert.All(plan, p => Assert.Equal(BundleRole.None, p.Role));
    }

    [Fact]
    public void 면이_다르면_묶지_않는다()
    {
        var plan = TaskBundlePlanner.Assign(new[]
        {
            Task("a", "SM", 400, InspectionMoveDirection.Horizontal, 0),
            Task("b", "PM", 400, InspectionMoveDirection.Horizontal, 1),
        });
        Assert.All(plan, p => Assert.Equal(BundleRole.None, p.Role));
    }

    [Fact]
    public void 수직과_수평은_묶지_않는다()
    {
        var plan = TaskBundlePlanner.Assign(new[]
        {
            Task("a", "SM", 400, InspectionMoveDirection.Horizontal, 0),
            Task("b", "SM", 400, InspectionMoveDirection.Vertical, 0, 0),
        });
        Assert.All(plan, p => Assert.Equal(BundleRole.None, p.Role));
    }

    [Fact]
    public void 방향_판별이_안_되면_묶지_않는다()
    {
        var plan = TaskBundlePlanner.Assign(new[]
        {
            Task("a", "SM", 400, null, 0),
            Task("b", "SM", 400, InspectionMoveDirection.Horizontal, 1),
        });
        Assert.All(plan, p => Assert.Equal(BundleRole.None, p.Role));
    }

    [Fact]
    public void 가운데가_다르면_양옆은_한_묶음이_아니다()
    {
        var plan = TaskBundlePlanner.Assign(new[]
        {
            Task("a", "SM", 400, InspectionMoveDirection.Horizontal, 0),
            Task("b", "SM", 800, InspectionMoveDirection.Horizontal, 1),
            Task("c", "SM", 400, InspectionMoveDirection.Horizontal, 2),
        });
        Assert.Equal(BundleRole.None, plan[0].Role);
        Assert.Equal(BundleRole.None, plan[1].Role);
        Assert.Equal(BundleRole.None, plan[2].Role);
    }

    [Fact]
    public void 사선_경계와_짧은_선은_방향을_확정하지_않는다()
    {
        // theta 0(정면 +X)에서 벽면 수평 탄젠트는 +Y.
        Assert.True(SeamDirectionResolver.TryClassify(
            new[] { 0.0, 0, 0 }, new[] { 0.0, 1.0, 0 }, 0, out var horizontal, out _));
        Assert.Equal(InspectionMoveDirection.Horizontal, horizontal);

        Assert.True(SeamDirectionResolver.TryClassify(
            new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1.0 }, 0, out var vertical, out _));
        Assert.Equal(InspectionMoveDirection.Vertical, vertical);

        // 수평·수직이 같으면 45° — 모호해서 묶지 않는다.
        Assert.False(SeamDirectionResolver.TryClassify(
            new[] { 0.0, 0, 0 }, new[] { 0.0, 1.0, 1.0 }, 0, out _, out var ambiguous));
        Assert.Contains("모호", ambiguous);

        Assert.False(SeamDirectionResolver.TryClassify(
            new[] { 0.0, 0, 0 }, new[] { 0.001, 0, 0 }, 0, out _, out var shortSeam));
        Assert.Contains("폴백", shortSeam);

        Assert.False(SeamDirectionResolver.TryClassify(
            new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, null, out _, out var noTheta));
        Assert.Contains("theta", noTheta);
    }

    [Fact]
    public void 피치_횟수는_시작점_간격이고_반대_방향은_음수_부호()
    {
        var (n, sign) = TaskBundlePlanner.PitchPlan(
            new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.72, 0, 0 }, 360);
        Assert.Equal(2, n);
        Assert.Equal(1, sign);

        var back = TaskBundlePlanner.PitchPlan(
            new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { -0.36, 0, 0 }, 360);
        Assert.Equal(-1, back.GeomSign);
    }

    [Fact]
    public void 묶음_점2는_도달_점검_점에_들어간다()
    {
        var plan = TaskBundlePlanner.Assign(new[]
        {
            Task("a", "SM", 400, InspectionMoveDirection.Horizontal, 0),
            Task("b", "SM", 400, InspectionMoveDirection.Horizontal, 0.72),
        });
        var extra = TaskBundlePlanner.Point2ReachPoints(plan, allowBackup: false);
        var point = Assert.Single(extra);
        Assert.Equal("a 묶음 점2", point.Label);
        Assert.Equal(0.72, point.SeamW[0]);
        Assert.Equal(400, point.StrokeMm);
    }

    [Fact]
    public void 묶음이_풀리면_다시_풀_수_없다()
    {
        var context = new SequenceContext();
        Assert.False(BundleTeach.TryMarkDissolved(context));
        context.Bag[BundleTeach.ActiveKey] = true;
        Assert.True(BundleTeach.IsActive(context));
        Assert.True(BundleTeach.TryMarkDissolved(context));
        Assert.False(BundleTeach.IsActive(context));
        Assert.False(BundleTeach.TryMarkDissolved(context));
    }

    [Fact]
    public void 두_점_WD_차이는_좌표계_기울기로_읽힌다()
    {
        // 점1 툴 +Z = 베이스 +Z (자세 0). 점2 가 Z 로 100mm 높고 X 로 360mm.
        var p1 = new[] { 0.0, 0, 0, 0, 0, 0 };
        var p2 = new[] { 360.0, 0, 100, 0, 0, 0 };
        var slope = InternalWorkpieceFrame.SlopeAlongSeamDeg(p1, p2);
        Assert.InRange(slope, 10, 20);
        var frame = InternalWorkpieceFrame.Compute(p1, p2);
        Assert.Equal(6, frame.Length);
    }
}
