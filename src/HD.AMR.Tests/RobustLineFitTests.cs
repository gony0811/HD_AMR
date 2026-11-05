using HD.AMR.App.Communication.Weld;
using C = HD.AMR.App.Communication.Weld.RobustLineFit.Candidate;

namespace HD.AMR.Tests;

public class RobustLineFitTests
{
    // cross = 0.05·s + 100 직선 위 비드 + 슬라이스마다 반사 후보.
    private static List<C> BeadWithReflections(int n = 400, double reflectionFrac = 1.0)
    {
        var rnd = new Random(7);
        var list = new List<C>();
        for (int s = 0; s < n; s++)
        {
            list.Add(new C(s, 0.05 * s + 100 + (rnd.NextDouble() - 0.5), list.Count));
            if (rnd.NextDouble() < reflectionFrac) list.Add(new C(s, rnd.Next(0, 480), list.Count));
        }
        return list;
    }

    [Fact]
    public void 슬라이스마다_반사가_있어도_비드선을_찾는다()
    {
        var r = RobustLineFit.Fit(BeadWithReflections());
        Assert.True(r.Ok);
        Assert.Equal(0.05, r.A, 2);
        Assert.Equal(100, r.B, 0);
        Assert.True(r.InlierSlices > 380);
    }

    [Fact]
    public void 최장_run_이_반사여도_선택되지_않는다()
    {
        // 앞 40% 구간은 반사(더 긴 run)가 cross=300 수평으로 연속 — 최소제곱(최장 run)이면 끌려가는 상황.
        var list = new List<C>();
        for (int s = 0; s < 400; s++)
        {
            list.Add(new C(s, 150, list.Count));
            if (s < 160) list.Add(new C(s, 300 + (s % 3), list.Count));
        }
        var r = RobustLineFit.Fit(list);
        Assert.True(r.Ok);
        Assert.Equal(150, r.B, 1);
        Assert.Equal(0, r.A, 3);
        Assert.All(r.Chosen.Values, c => Assert.Equal(150, c.Cross));
    }

    [Fact]
    public void 허용각을_넘는_선은_버린다()
    {
        // 진행축 대비 45° 선만 있으면 maxAngle 20° 에서 실패.
        var list = Enumerable.Range(0, 200).Select(s => new C(s, s)).ToList();
        Assert.False(RobustLineFit.Fit(list, maxAngleDeg: 20).Ok);
        Assert.True(RobustLineFit.Fit(list, maxAngleDeg: 50).Ok);
    }

    [Fact]
    public void 슬라이스당_하나만_선택하고_Tag_를_유지한다()
    {
        var list = new List<C>();
        for (int s = 0; s < 50; s++)
        {
            list.Add(new C(s, 20.0, 1000 + s));
            list.Add(new C(s, 21.0, 2000 + s));   // 둘 다 inlier — 선에 더 가까운 쪽 하나만
        }
        var r = RobustLineFit.Fit(list);
        Assert.True(r.Ok);
        Assert.Equal(50, r.Chosen.Count);
        Assert.All(r.Chosen, kv => Assert.Equal(kv.Key, kv.Value.S));
    }

    [Fact]
    public void 후보가_부족하면_실패()
    {
        Assert.False(RobustLineFit.Fit(new List<C> { new(0, 1) }).Ok);
        Assert.False(RobustLineFit.Fit(new List<C> { new(0, 1), new(1, 1) }, minInlierSlices: 3).Ok);
    }

    [Fact]
    public void 같은_입력이면_같은_결과()
    {
        var a = RobustLineFit.Fit(BeadWithReflections());
        var b = RobustLineFit.Fit(BeadWithReflections());
        Assert.Equal(a.A, b.A);
        Assert.Equal(a.B, b.B);
    }
}
