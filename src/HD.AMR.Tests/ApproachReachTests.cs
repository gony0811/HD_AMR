using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

/// <summary>
/// 접근점 도달성 — 역기구학을 부르기 <b>전</b>에 배치만으로 거르는 판정.
/// 현장 실측 3회(플랜지 뻗음 230mm 에서 전부 손목 특이점/rc=38)를 그대로 픽스처로 고정한다.
/// </summary>
public class ApproachReachTests
{
    [Fact]
    public void 현장_실측_230mm_는_거부된다()
    {
        // AMR 이 벽에서 1000mm 정차, 후퇴 400, 공구 370 → 플랜지 230mm.
        var r = ApproachReach.Check(normalDistanceMm: 1000, approachMm: 400,
                                    toolLengthMm: 370, minFlangeReachMm: 350);

        Assert.False(r.Ok);
        Assert.Equal(230, r.FlangeReachMm, 6);
        Assert.Equal(1120, r.RequiredNormalDistanceMm, 6);
        Assert.Equal(120, r.ShortfallMm, 6);     // 벽에서 120mm 더 떨어뜨려야 한다
    }

    [Fact]
    public void 최소값_경계는_통과한다()
    {
        var r = ApproachReach.Check(1120, 400, 370, 350);

        Assert.True(r.Ok);
        Assert.Equal(350, r.FlangeReachMm, 6);
        Assert.Equal(0, r.ShortfallMm, 6);
    }

    [Fact]
    public void 최소값_바로_아래는_거부된다()
    {
        Assert.False(ApproachReach.Check(1119, 400, 370, 350).Ok);
    }

    [Fact]
    public void 후퇴_거리를_줄이면_필요_정차거리도_그만큼_준다()
    {
        // ②의 후퇴 거리는 ③ 카메라 목표거리다 — 레시피를 300 으로 낮추면 필요값도 100mm 줄어든다.
        var a = ApproachReach.Check(1000, 400, 370, 350);
        var b = ApproachReach.Check(1000, 300, 370, 350);

        Assert.Equal(a.RequiredNormalDistanceMm - 100, b.RequiredNormalDistanceMm, 6);
        Assert.Equal(a.FlangeReachMm + 100, b.FlangeReachMm, 6);
    }

    [Fact]
    public void 여유가_있으면_부족분은_음수다()
    {
        var r = ApproachReach.Check(1400, 400, 370, 350);

        Assert.True(r.Ok);
        Assert.True(r.ShortfallMm < 0, $"여유 있는데 부족분이 {r.ShortfallMm} 입니다.");
    }

    // ── 공구 길이 ───────────────────────────────────────────────────

    [Fact]
    public void 공구_오프셋이_0_이면_길이도_0()
    {
        // 공구 #0(플랜지) — 호출측은 이 경우 점검을 생략한다.
        Assert.Equal(0.0, ApproachReach.ToolLengthMm(new double[6]), 9);
    }

    [Fact]
    public void 공구_길이는_병진_3성분의_노름이다()
    {
        // 회전 성분(rx,ry,rz)은 길이에 들어가지 않는다.
        Assert.Equal(5.0, ApproachReach.ToolLengthMm(new[] { 3.0, 4.0, 0.0, 90.0, 0.0, 180.0 }), 9);
    }

    [Fact]
    public void 공구_좌표가_null_이거나_짧으면_0()
    {
        Assert.Equal(0.0, ApproachReach.ToolLengthMm(null), 9);
        Assert.Equal(0.0, ApproachReach.ToolLengthMm(new[] { 1.0, 2.0 }), 9);
    }
}
