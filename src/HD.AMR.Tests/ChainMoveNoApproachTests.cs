using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>연속 이동은 면 쪽으로 가지 않는다 — 2026-11-14 A 면 L4-R01C09 2→3번 사례.</summary>
public class ChainMoveNoApproachTests
{
    private static readonly double[] WallOut = { 1, 0, 0 };   // 면은 −x 쪽

    [Fact]
    public void FieldCase_TargetBehindRealWall_KeepsHeightAndMovesSideways()
    {
        // 접근점(모델 면 이격 400) [-1496.9, -278.0, -772.5] → 연속 이동 평면(이격 100) x=-1796.9
        var chainBase = CobotInspectionMoveStep.ChainTargetBase(new[] { -1496.9, -278.0, -772.5 }, WallOut, 400, 100);
        Assert.Equal(-1796.9, chainBase[0], 3);
        var cur = new[] { -1557.35, -423.23, -703.24 };   // 직전 검사 종료 — 실제 면 수십 mm 앞

        var kept = CobotInspectionMoveStep.ChainTargetKeepingHeight(cur, chainBase, WallOut);
        Assert.NotNull(kept);
        Assert.Equal(239.55, kept!.Value.AboveMm, 2);
        // 면 법선 성분은 그대로(벽 쪽 이동 0), 옆 성분만 시작점 정면으로
        Assert.Equal(cur[0], kept.Value.Target[0], 6);
        Assert.Equal(-278.0, kept.Value.Target[1], 6);
        Assert.Equal(-772.5, kept.Value.Target[2], 6);
        // 그 높이에서는 들어 올릴 것도 없다
        Assert.Null(CobotInspectionMoveStep.PlanChainLift(cur, kept.Value.Target, WallOut));
    }

    [Fact]
    public void AtOrBelowChainPlane_ReturnsNull_SoLiftPathIsUsed()
    {
        var chainBase = new[] { -1800.0, 0.0, 0.0 };
        Assert.Null(CobotInspectionMoveStep.ChainTargetKeepingHeight(new[] { -1800.0, 50, 0 }, chainBase, WallOut));
        Assert.Null(CobotInspectionMoveStep.ChainTargetKeepingHeight(new[] { -1799.5, 50, 0 }, chainBase, WallOut));
        // 평면보다 면 쪽이면 종전대로 면에서 멀어지는 들어 올림
        var cur = new[] { -1860.0, 50.0, 0.0 };
        Assert.Null(CobotInspectionMoveStep.ChainTargetKeepingHeight(cur, chainBase, WallOut));
        var lift = CobotInspectionMoveStep.PlanChainLift(cur, chainBase, WallOut);
        Assert.NotNull(lift);
        Assert.Equal(60, lift![0], 6);
    }

    [Fact]
    public void TiltedNormal_KeepsNormalComponentOnly()
    {
        var n = new[] { Math.Cos(0.1), Math.Sin(0.1), 0.0 };
        var chainBase = new[] { -1800.0, 0.0, -700.0 };
        var cur = new[] { -1600.0, 300.0, -650.0 };
        var kept = CobotInspectionMoveStep.ChainTargetKeepingHeight(cur, chainBase, n)!.Value;
        double H(double[] p) => (p[0] - chainBase[0]) * n[0] + (p[1] - chainBase[1]) * n[1] + (p[2] - chainBase[2]) * n[2];
        Assert.Equal(H(cur), H(kept.Target), 6);          // 면까지 높이 유지
        Assert.Equal(H(cur), kept.AboveMm, 6);
        Assert.Equal(-700.0, kept.Target[2], 6);           // 옆 성분은 목표 것
    }

    [Fact]
    public void MeasuredFrame_FieldCase_StaysWhereItIs()
    {
        // 3번 시작점으로 옮긴 실측 좌표계 원점 [-1591.1, -421.9, -704.1], 직전 종료 TCP 는 그 33.8mm 위
        var cur = new[] { -1557.35, -423.23, -703.24 };
        var (target, height, kept) = CobotInspectionMoveStep.ChainTargetFromMeasuredFrame(
            cur, new[] { -1591.1, -421.9, -704.1, 88.5, 0.5, 86.0 }, WallOut, 100);
        Assert.True(kept);
        Assert.Equal(33.75, height, 2);
        Assert.Equal(cur[0], target[0], 6);                                   // 면 쪽 이동 없음
        var move = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (target[i] - cur[i]) * (target[i] - cur[i])));
        Assert.True(move < 2.0, $"이동 {move:0.0}mm");                         // 모델 기준이면 289mm 였다
    }

    [Fact]
    public void MeasuredFrame_TooCloseToSurface_LiftsAwayToChainStandoff()
    {
        var (target, height, kept) = CobotInspectionMoveStep.ChainTargetFromMeasuredFrame(
            new[] { -1586.0, -400.0, -700.0 }, new[] { -1591.1, -421.9, -704.1, 0, 0, 0 }, WallOut, 100);
        Assert.False(kept);
        Assert.Equal(100, height, 6);
        Assert.Equal(-1491.1, target[0], 6);                                   // 면에서 멀어지는 쪽
        Assert.Equal(-421.9, target[1], 6);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(60.0)]
    [InlineData(250.0)]
    public void MeasuredFrame_NeverMovesTowardWall(double heightAbove)
    {
        var origin = new[] { -1600.0, 0.0, -700.0, 0, 0, 0 };
        var cur = new[] { -1600.0 + heightAbove, 120.0, -650.0 };
        var (target, _, _) = CobotInspectionMoveStep.ChainTargetFromMeasuredFrame(cur, origin, WallOut, 100);
        Assert.True(target[0] >= cur[0] - 1e-9);
    }
}
