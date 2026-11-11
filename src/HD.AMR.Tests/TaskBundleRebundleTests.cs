using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Tests;

/// <summary>
/// 묶음 리더가 실패했을 때 실패한 TASK 만 빼고 남은 TASK 를 다시 묶는다(<see cref="TaskBundlePlanner.Rebundle"/>).
/// 실기 2026-11-11 T L4-R08C16: 1번이 순회 도달 불가로 실패하자 2~4번이 각각 단독 교시로 돌았다.
/// </summary>
public class TaskBundleRebundleTests
{
    private static BundleTaskInput Task(string id, double startX)
        => new(id, "JOB-" + id, "T", 0, InspectionMoveDirection.Horizontal,
            new[] { startX, 0, 1.0 }, new[] { startX + 0.36, 0, 1.0 });

    private static IReadOnlyList<BundleAssignment> FourTasks()
        => TaskBundlePlanner.Assign(new[] { Task("a", 0), Task("b", 0.36), Task("c", 0.72), Task("d", 1.08) });

    [Fact]
    public void 리더가_실패하면_다음_TASK_가_새_리더가_되고_나머지는_그_묶음의_팔로워가_된다()
    {
        var plan = FourTasks();

        var re = TaskBundlePlanner.Rebundle(plan, plan[0].BundleId!, "b",
            new[] { 0.36, 0, 1.0 }, new[] { 0.72, 0, 1.0 });

        Assert.NotNull(re);
        Assert.Equal(new[] { "b", "c", "d" }, re!.Select(a => a.ActionId));
        Assert.Equal(new[] { BundleRole.Leader, BundleRole.Follower, BundleRole.Follower }, re.Select(a => a.Role));
        Assert.All(re, a => Assert.Equal("b", a.BundleId));              // 새 묶음 id = 새 리더
        Assert.All(re, a => Assert.Equal(0.36, a.FirstStartW![0]));      // 점1 = 새 리더 시작점
        Assert.All(re, a => Assert.Equal(0.72, a.FirstEndW![0]));
        Assert.All(re, a => Assert.Equal(1.08, a.LastStartW![0]));       // 점2 = 묶음의 마지막 TASK 시작점 그대로

        // 새 리더 시작 → 마지막 시작 = 2피치.
        var (count, sign) = TaskBundlePlanner.PitchPlan(re[0].FirstStartW!, re[0].FirstEndW!, re[0].LastStartW!, 360);
        Assert.Equal((2, 1), (count, sign));
    }

    [Fact]
    public void 새_리더도_실패하면_그_다음_TASK_로_다시_묶는다()
    {
        var plan = FourTasks().ToList();
        var first = TaskBundlePlanner.Rebundle(plan, plan[0].BundleId!, "b", new[] { 0.36, 0, 1.0 }, new[] { 0.72, 0, 1.0 })!;
        foreach (var a in first) plan[plan.FindIndex(p => p.ActionId == a.ActionId)] = a;

        var second = TaskBundlePlanner.Rebundle(plan, "b", "c", new[] { 0.72, 0, 1.0 }, new[] { 1.08, 0, 1.0 });

        Assert.NotNull(second);
        Assert.Equal(new[] { "c", "d" }, second!.Select(a => a.ActionId));
        Assert.Equal(BundleRole.Leader, second[0].Role);
        Assert.Equal("c", second[1].BundleId);
        Assert.Equal(BundleRole.Leader, plan[0].Role);   // 실패한 원래 리더 배정은 건드리지 않는다
        Assert.Equal("a", plan[0].BundleId);
    }

    [Fact]
    public void 마지막_하나만_남으면_묶지_않는다_단독_교시()
    {
        var plan = FourTasks();

        Assert.Null(TaskBundlePlanner.Rebundle(plan, plan[0].BundleId!, "d", new[] { 1.08, 0, 1.0 }, new[] { 1.44, 0, 1.0 }));
    }

    [Fact]
    public void 묶음에_없는_TASK_나_좌표가_없으면_묶지_않는다()
    {
        var plan = FourTasks();
        var id = plan[0].BundleId!;

        Assert.Null(TaskBundlePlanner.Rebundle(plan, id, "x", new[] { 0.0, 0, 1 }, new[] { 0.36, 0, 1 }));
        Assert.Null(TaskBundlePlanner.Rebundle(plan, "다른 묶음", "b", new[] { 0.36, 0, 1 }, new[] { 0.72, 0, 1 }));
        Assert.Null(TaskBundlePlanner.Rebundle(plan, id, "b", null, new[] { 0.72, 0, 1 }));
        Assert.Null(TaskBundlePlanner.Rebundle(plan, id, "b", new[] { 0.36, 0, 1 }, new[] { 0.72, 0 }));
    }

    [Fact]
    public void 다른_묶음과_단독_TASK_는_건드리지_않는다()
    {
        // a·b 묶음 / c 는 리프트가 달라 단독 / d·e·f 묶음
        var tasks = new[]
        {
            Task("a", 0), Task("b", 0.36),
            Task("c", 0.72) with { LiftStrokeMm = 300 },
            Task("d", 1.08), Task("e", 1.44), Task("f", 1.80),
        };
        var plan = TaskBundlePlanner.Assign(tasks);
        Assert.Equal(BundleRole.None, plan[2].Role);

        var re = TaskBundlePlanner.Rebundle(plan, plan[3].BundleId!, "e", new[] { 1.44, 0, 1.0 }, new[] { 1.80, 0, 1.0 });

        Assert.Equal(new[] { "e", "f" }, re!.Select(a => a.ActionId));
        Assert.Equal(1.80, re[0].LastStartW![0]);
    }
}
