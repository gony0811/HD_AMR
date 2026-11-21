using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>가스 무정지 사전 IK 검증 솎음 — 5mm 간격 경유점을 위치 50mm·자세 5° 마다만 검증.</summary>
public class NonStopChainSubsampleTests
{
    private static (int, double[]) P(int i, double x, double rx = 0) => (i, new[] { x, 0, 0, rx, 0, 0 });

    [Fact]
    public void 직선_80점은_처음_끝과_50mm_마다만_남긴다()
    {
        var targets = Enumerable.Range(0, 80).Select(i => P(i, i * 5.0)).ToList();
        var kept = InspectionRunStep.SubsampleChain(targets, 50, 5);
        Assert.Equal(0, kept[0].Index);
        Assert.Equal(79, kept[^1].Index);
        Assert.InRange(kept.Count, 8, 10);
        Assert.All(kept.Zip(kept.Skip(1)), p => Assert.True(p.Second.Target[0] - p.First.Target[0] <= 50 + 1e-9));
    }

    [Fact]
    public void 자세가_5도_이상_바뀌면_남긴다()
    {
        var targets = new List<(int, double[])> { P(0, 0), P(1, 5, 2), P(2, 10, 6), P(3, 15, 6) };
        var kept = InspectionRunStep.SubsampleChain(targets, 50, 5);
        Assert.Equal(new[] { 0, 2, 3 }, kept.Select(k => k.Index));
    }

    [Fact]
    public void 각도_감김은_가까운_쪽으로_본다()
    {
        var targets = new List<(int, double[])> { P(0, 0, 179), P(1, 5, -179), P(2, 10, -179) };
        var kept = InspectionRunStep.SubsampleChain(targets, 50, 5);
        Assert.Equal(new[] { 0, 2 }, kept.Select(k => k.Index));
    }
}
