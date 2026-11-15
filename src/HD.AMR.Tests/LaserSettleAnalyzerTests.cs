using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class LaserSettleAnalyzerTests
{
    private const double Dt = 0.05;

    private static List<LaserNoiseSample> Window(Func<int, double[]> values, int n = 20, Func<int, bool[]>? valid = null)
        => Enumerable.Range(0, n)
            .Select(i => new LaserNoiseSample(i * Dt, values(i), valid?.Invoke(i) ?? new[] { true, true, true },
                new[] { false, false, false }, 0, 0))
            .ToList();

    private static double Gauss(Random r, double sigma)
        => sigma * Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    [Fact]
    public void FlatReadings_AreSettled()
    {
        var r = LaserSettleAnalyzer.Assess(Window(_ => new[] { 345.0, 345.0, 345.0 }), 0.05, 2.0);
        Assert.True(r.Settled);
    }

    // 11-09 10:40 재현: CH2 백색잡음 σ 0.2mm 만 있고 흐르지 않음 → 종전 |Δ|<0.05mm 기준은 못 넘지만 정착으로 봐야 한다.
    [Fact]
    public void NoisyButNotDrifting_IsSettled()
    {
        var settledCount = 0;
        for (var seed = 0; seed < 50; seed++)
        {
            var rng = new Random(seed);
            var w = Window(_ => new[] { 346.0 + Gauss(rng, 0.02), 345.8 + Gauss(rng, 0.2), 346.1 + Gauss(rng, 0.02) });
            if (LaserSettleAnalyzer.Assess(w, 0.05, 2.0).Settled) settledCount++;
        }
        Assert.True(settledCount >= 45, $"settled {settledCount}/50");
    }

    // 11-09 10:40 측정 2: CH1 이 −0.19mm/s 로 흐르는 중(잡음 작음) → 미정착.
    [Fact]
    public void ClearDrift_IsNotSettled()
    {
        var rng = new Random(3);
        var w = Window(i => new[] { 346.48 - 0.19 * i * Dt + Gauss(rng, 0.01), 345.8 + Gauss(rng, 0.2), 346.1 + Gauss(rng, 0.01) });
        var r = LaserSettleAnalyzer.Assess(w, 0.05, 2.0);
        Assert.False(r.Settled);
        Assert.Contains(r.Reasons, m => m.StartsWith("CH1 드리프트"));
    }

    // 물리적으로 작은 기울기(0.02mm/s)는 유의해도 정착.
    [Fact]
    public void TinySignificantSlope_IsSettled()
    {
        var w = Window(i => new[] { 345 + 0.02 * i * Dt, 345.0, 345.0 });
        Assert.True(LaserSettleAnalyzer.Assess(w, 0.05, 2.0).Settled);
    }

    [Fact]
    public void MostlyInvalidChannel_IsNotSettled()
    {
        var w = Window(_ => new[] { 345.0, 345.0, 345.0 }, valid: i => new[] { true, i % 4 == 0, true });
        var r = LaserSettleAnalyzer.Assess(w, 0.05, 2.0);
        Assert.False(r.Settled);
        Assert.Contains(r.Reasons, m => m.StartsWith("CH2 유효"));
    }

    [Fact]
    public void SlopeWithError_RecoversExactLine()
    {
        var t = Enumerable.Range(0, 10).Select(i => i * 0.1).ToArray();
        var y = t.Select(x => 2 + 0.3 * x).ToArray();
        var (slope, se) = LaserSettleAnalyzer.SlopeWithError(t, y);
        Assert.Equal(0.3, slope, 9);
        Assert.Equal(0, se, 9);
    }
}
