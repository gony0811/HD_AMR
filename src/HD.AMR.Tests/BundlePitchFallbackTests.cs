using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 묶음 점2 피치 추정이 도중에 실패하면 단독 교시(피치 1회)까지 내려가지 않고 가장 먼 성공 자리를 점2 로 쓴다
/// (실기 2026-11-24 M2: 피치 3회째 자리만 레이저가 34.7mm 가깝게 읽혀 묶음이 풀리고 줄마다 다시 교시).
/// </summary>
public class BundlePitchFallbackTests
{
    [Theory]
    [InlineData(0, 0)]   // 첫 피치 실패 — 묶음 해제
    [InlineData(1, 0)]   // 1회만 성공 — 단독 교시와 같은 간격이라 묶음 해제
    [InlineData(2, 2)]   // 3회째 실패 — 2회째 자리(720mm)를 점2 로
    [InlineData(3, 3)]
    public void 두_번_이상_성공했으면_가장_먼_성공_자리를_점2로_쓴다(int completed, int expected)
        => Assert.Equal(expected, BundleTeach.FallbackPitchCount(completed));

    private static BundleTaskInput Line(int n) => new(
        $"A{n}", $"JOB-{n}", "SM", 0, HD.AMR.App.Service.Sequence.InspectionMoveDirection.Horizontal,
        new[] { 8.072 + 0.36 * (n - 1), 0.877, 2.243 }, new[] { 8.072 + 0.36 * n, 0.877, 2.243 });

    [Fact]
    public void 점2를_가까운_자리에_잡으면_그_구간_안의_줄만_묶음에_남는다()
    {
        // 4줄 묶음(피치 3회 계획)에서 3회째가 실패해 2회째 자리(720mm)를 점2 로 — 앞 2줄(0~720mm)만 남는다.
        var ordered = TaskBundlePlanner.Assign(new[] { Line(1), Line(2), Line(3), Line(4) }).ToList();
        Assert.All(ordered, a => Assert.Equal("A1", a.BundleId));

        var detached = TaskBundlePlanner.Truncate(ordered, "A1", keepCount: 2);

        Assert.NotNull(detached);
        Assert.Equal(new[] { "A3", "A4" }, detached!.Select(a => a.ActionId));
        Assert.All(detached, a => Assert.Equal("A3", a.BundleId));              // 좌표계 없는 새 묶음 ID
        Assert.All(detached, a => Assert.Equal(BundleRole.Follower, a.Role));
        foreach (var a in detached) ordered[ordered.FindIndex(o => o.ActionId == a.ActionId)] = a;
        Assert.Equal(new[] { "A1", "A2" }, ordered.Where(a => a.BundleId == "A1").Select(a => a.ActionId));

        // 떼어 낸 줄은 차례가 오면 자기들끼리 다시 묶여 새로 교시한다(A3 리더, 점2 = A4 시작).
        var re = TaskBundlePlanner.Rebundle(ordered, "A3", "A3", Line(3).SeamStartW, Line(3).SeamEndW);
        Assert.NotNull(re);
        Assert.Equal(BundleRole.Leader, re![0].Role);
        Assert.Equal(BundleRole.Follower, re[1].Role);
        Assert.Equal(Line(4).SeamStartW![0], re[0].LastStartW![0], 9);
    }

    [Fact]
    public void 마지막_한_줄만_떼어_내면_그_줄은_단독_교시()
    {
        var ordered = TaskBundlePlanner.Assign(new[] { Line(1), Line(2), Line(3), Line(4) }).ToList();
        var detached = TaskBundlePlanner.Truncate(ordered, "A1", keepCount: 3);
        Assert.Single(detached!);
        foreach (var a in detached!) ordered[ordered.FindIndex(o => o.ActionId == a.ActionId)] = a;
        Assert.Null(TaskBundlePlanner.Rebundle(ordered, "A4", "A4", Line(4).SeamStartW, Line(4).SeamEndW));
    }

    [Theory]
    [InlineData(4)]   // 전부 남김 — 뗄 것 없음
    [InlineData(9)]
    [InlineData(0)]
    public void 뗄_것이_없으면_그대로(int keep)
    {
        var ordered = TaskBundlePlanner.Assign(new[] { Line(1), Line(2), Line(3), Line(4) });
        Assert.Null(TaskBundlePlanner.Truncate(ordered, "A1", keep));
    }

    [Fact]
    public void 지금_높이가_들어올린_높이보다_낮은_만큼을_잰다()
    {
        var above = new[] { -1505.3, 70.2, 728.5, 90.0, 0.4, 89.4 };
        double[] wallOut = { 1.0, 0.0, 0.0 };
        // 내리다 12mm 내려와 멈춘 자리(옆으로 360mm 떨어짐) — 12mm 모자란다.
        Assert.Equal(12.0, BundleTeach.HeightShortfallMm(new[] { -1517.3, -289.8, 728.5, 90.0, 0.4, 89.4 }, above, wallOut), 6);
        // 들어올린 채 멈춘 자리 — 모자라지 않는다.
        Assert.Equal(0.0, BundleTeach.HeightShortfallMm(new[] { -1505.3, -289.8, 728.5, 90.0, 0.4, 89.4 }, above, wallOut), 6);
    }

    [Fact]
    public void 복귀는_면_바깥으로_띄운_자리를_거친다()
    {
        // 벽 바깥 법선 = BASE +X(길이가 1 이 아니어도 된다).
        var target = new[] { -1535.3, 70.2, 728.5, 90.0, 0.4, 89.4 };
        var above = BundleTeach.LiftedAlongNormal(target, new[] { 2.0, 0.0, 0.0 }, 30.0);
        Assert.Equal(-1505.3, above[0], 6);
        Assert.Equal(target[1], above[1], 6);
        Assert.Equal(target[2], above[2], 6);
        for (var i = 3; i < 6; i++) Assert.Equal(target[i], above[i], 9);

        // 법선이 없으면 그대로.
        var same = BundleTeach.LiftedAlongNormal(target, new[] { 0.0, 0.0, 0.0 }, 30.0);
        for (var i = 0; i < 6; i++) Assert.Equal(target[i], same[i], 9);
    }
}
