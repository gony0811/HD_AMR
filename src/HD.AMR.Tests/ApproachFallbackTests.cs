using HD.AMR.App.Communication;
using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ② 검사위치 이동의 대체 경로 — IK 해 재시도(⑧), 틸트 접근 자세 탐색(⑦), 손목 여유 단일 기준(⑨).
/// </summary>
public class ApproachFallbackTests
{
    // 현재(기준) 관절: J5 +40°, J6 −180°(대기 와인딩).
    private static readonly double[] Reference = { 0, -60, 100, -130, 40, -180 };

    [Fact]
    public void 해_순위는_J6_와인딩_유지가_먼저이고_중복은_하나로()
    {
        var keepJ6 = new[] { 5.0, -60, 100, 50, -40, -178 };     // 손목 반전이지만 J6 거의 그대로
        var bigJ6 = new[] { 2.0, -60, 100, -130, 40, -20 };      // 손목 유지, J6 160° 회전
        var dup = new[] { 5.2, -60, 100, 50, -40, -178 };        // keepJ6 와 0.5° 미만 차이

        var ranked = FairinoRpcClient.RankKeepWrist(new[] { bigJ6, keepJ6, dup }, Reference);

        Assert.Equal(2, ranked.Count);
        Assert.Same(keepJ6, ranked[0]);
        Assert.Same(bigJ6, ranked[1]);
    }

    [Fact]
    public void 순위_1등이_손목_반전이면_진입_검사에서_거부되고_다음_해는_통과()
    {
        // 관절 이동량(140° 이하)은 통과하고 J5 부호만 바뀐 해.
        var flipped = new[] { 5.0, -60, 100, -20, -40, -178 };
        var keep = new[] { 2.0, -60, 100, -130, 40, -240 };

        Assert.Contains("손목 가지 전환", CobotInspectionMoveStep.EntryJointBlocker(Reference, flipped));
        Assert.Null(CobotInspectionMoveStep.EntryJointBlocker(Reference, keep));
    }

    [Fact]
    public void 진입_해의_J5가_여유보다_작으면_거부()
    {
        var nearSingular = new[] { 2.0, -60, 100, -130, PostureLimits.DefaultWristMarginDeg - 1, -180 };

        Assert.Contains("손목 특이점", CobotInspectionMoveStep.EntryJointBlocker(Reference, nearSingular));
    }

    [Fact]
    public void 진입_해가_J6_안전_범위_밖이면_거부()
    {
        var outOfRange = new[] { 2.0, -60, 100, -130, 40, -320 };

        Assert.Contains("J6 안전 범위", CobotInspectionMoveStep.EntryJointBlocker(Reference, outOfRange));
    }

    [Fact]
    public void 자기_간섭_판정을_주면_반영한다()
    {
        var ok = new[] { 2.0, -60, 100, -130, 40, -200 };

        Assert.Equal("자기 간섭", CobotInspectionMoveStep.EntryJointBlocker(Reference, ok, _ => "hit"));
        Assert.Null(CobotInspectionMoveStep.EntryJointBlocker(Reference, ok, _ => null));
    }

    [Fact]
    public void 틸트_후보는_정면을_빼고_작은_기울기부터_최대각까지()
    {
        var c = CobotInspectionMoveStep.TiltCandidates(10);

        Assert.DoesNotContain((0.0, 0.0), c);
        Assert.Equal(5.0, Math.Abs(c[0].Up) + Math.Abs(c[0].Side));
        Assert.All(c, t => Assert.True(Math.Abs(t.Up) <= 10 && Math.Abs(t.Side) <= 10));
        Assert.Equal(5 * 5 - 1, c.Count);   // {0,±5,±10}² − 정면
        for (var i = 1; i < c.Count; i++)
            Assert.True(Math.Abs(c[i - 1].Up) + Math.Abs(c[i - 1].Side) <= Math.Abs(c[i].Up) + Math.Abs(c[i].Side));
    }

    [Fact]
    public void 틸트_최대각이_5도_미만이면_후보_없음()
    {
        Assert.Empty(CobotInspectionMoveStep.TiltCandidates(4));
    }

    [Fact]
    public void 틸트_목표는_손목_여유와_같은_가지인_해가_하나라도_있어야_쓸_수_있다()
    {
        var singular = new[] { 0.0, -60, 100, -130, 3, -180 };
        var flipped = new[] { 0.0, -60, 100, 50, -30, -180 };
        var good = new[] { 0.0, -60, 100, -130, 25, -190 };

        Assert.False(CobotInspectionMoveStep.TiltTargetUsable(Reference, new[] { singular, flipped }));
        Assert.True(CobotInspectionMoveStep.TiltTargetUsable(Reference, new[] { singular, good }));
    }

    [Fact]
    public void 손목_여유는_시퀀스와_자세_허용_범위가_같은_값()
    {
        Assert.Equal(10.0, PostureLimits.DefaultWristMarginDeg);
        Assert.Equal(PostureLimits.DefaultWristMarginDeg, CobotInspectionMoveStep.PlanarWristMarginDeg);
        Assert.Equal(PostureLimits.DefaultWristMarginDeg, PostureLimits.Default.WristMarginDeg);
    }
}
