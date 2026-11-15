using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class CameraAlignRefineTests
{
    private static Func<Task<DepthDistanceMoveResult>> Moves(params DepthDistanceMoveResult[] seq)
    {
        var i = 0;
        return () => Task.FromResult(seq[Math.Min(i++, seq.Length - 1)]);
    }

    private static DepthDistanceMoveResult Moved(double from, double moved) => new(true, from - moved, moved, "moved");
    private static DepthDistanceMoveResult InRange(double d) => new(true, d, 0, "skip");

    // ③이 목표 범위에 이미 보냈으면 이동 없이 통과.
    [Fact]
    public async Task AlreadyInRange_PassesWithoutMove()
    {
        var (ok, msg) = await CameraAlignRefineStep.RefineAsync(Moves(InRange(302)), () => Task.FromResult<double?>(302), 300);
        Assert.True(ok);
        Assert.Contains("이동 없음", msg);
    }

    // 10-27 L7·L8 — ③이 구조물 기준으로 487mm 에 멈춤 → 재측정에서 벽 거리로 한 번 더 접근해 범위 진입.
    [Fact]
    public async Task ShortStop_RefinesIntoRange()
    {
        var (ok, msg) = await CameraAlignRefineStep.RefineAsync(
            Moves(Moved(487, 187), InRange(301)), () => Task.FromResult<double?>(301), 300);
        Assert.True(ok);
        Assert.Contains("추가 접근 1회", msg);
    }

    // 끝내 범위에 못 들면 NG — 먼 거리에서 ④가 실행되지 않게.
    [Fact]
    public async Task NeverConverges_FailsWithFinalDistance()
    {
        var (ok, msg) = await CameraAlignRefineStep.RefineAsync(
            Moves(Moved(500, 200), Moved(480, 180), Moved(470, 170)), () => Task.FromResult<double?>(460), 300);
        Assert.False(ok);
        Assert.Contains("미도달", msg);
        Assert.Contains("460", msg);
    }

    [Fact]
    public async Task MoveFailure_PropagatesAsNg()
    {
        var (ok, msg) = await CameraAlignRefineStep.RefineAsync(
            Moves(new DepthDistanceMoveResult(false, 490, null, "접근 이동 실패 (rc=112)")),
            () => Task.FromResult<double?>(490), 300);
        Assert.False(ok);
        Assert.Contains("rc=112", msg);
    }
}
