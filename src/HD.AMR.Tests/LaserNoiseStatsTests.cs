using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class LaserNoiseStatsTests
{
    private static LaserNoiseSample S(double t, double a, double b, double c,
        double? rx = 0, double? ry = 0, bool validB = true, bool warnC = false)
        => new(t, new[] { a, b, c }, new[] { true, validB, true }, new[] { false, false, warnC }, rx, ry);

    [Fact]
    public void ConstantReadings_AreStable()
    {
        var samples = Enumerable.Range(0, 20).Select(i => S(i * 0.05, 100, 101, 102)).ToList();
        var r = LaserNoiseAnalyzer.Analyze(samples);

        Assert.All(r.Channels, c => Assert.Equal(0, c.Std, 9));
        Assert.Equal(19, r.Channels[0].Repeats);
        Assert.True(r.IsStable);
    }

    [Fact]
    public void ChannelStats_MatchKnownValues()
    {
        // CH1: 1,2,3,4,5 → 평균 3, σ(n−1)=√2.5, 중앙값 3, MAD=1
        var samples = new[] { 1.0, 2, 3, 4, 5 }.Select((v, i) => S(i, v, 0, 0)).ToList();
        var c = LaserNoiseAnalyzer.Analyze(samples).Channels[0];

        Assert.Equal(3, c.Mean, 9);
        Assert.Equal(Math.Sqrt(2.5), c.Std, 9);
        Assert.Equal(3, c.Median, 9);
        Assert.Equal(1.4826, c.RobustStd, 9);
        Assert.Equal(1.0, c.SlopeMmPerS, 9);   // 1mm/s 선형 증가
    }

    [Fact]
    public void LinearDrift_IsFlaggedAsUnstable()
    {
        // 1초 동안 CH1 이 0.2mm 흘러감 — 정착 덜 된 상태.
        var samples = Enumerable.Range(0, 21).Select(i => S(i * 0.05, 100 + i * 0.01, 100, 100)).ToList();
        var r = LaserNoiseAnalyzer.Analyze(samples);

        Assert.Contains(r.InstabilityReasons(), m => m.Contains("CH1 드리프트"));
    }

    [Fact]
    public void SingleSpike_CountedByHampel_AndRobustStdStaysSmall()
    {
        var values = new double[20];
        for (var i = 0; i < values.Length; i++) values[i] = 100 + (i % 2 == 0 ? 0.01 : -0.01);
        values[10] = 105;   // 단일 스파이크
        var samples = values.Select((v, i) => S(i * 0.05, v, 100, 100)).ToList();
        var c = LaserNoiseAnalyzer.Analyze(samples).Channels[0];

        Assert.Equal(1, c.Spikes);
        Assert.True(c.RobustStd < 0.05);
        Assert.True(c.Std > 1.0);
    }

    [Fact]
    public void InvalidAndWarning_AreCountedAndFlagged()
    {
        var samples = Enumerable.Range(0, 10)
            .Select(i => S(i * 0.05, 100, 100, 100, validB: i != 3, warnC: i < 5)).ToList();
        var r = LaserNoiseAnalyzer.Analyze(samples);

        Assert.Equal(1, r.Channels[1].Invalid);
        Assert.Equal(5, r.Channels[2].Warnings);
        var reasons = r.InstabilityReasons();
        Assert.Contains(reasons, m => m.Contains("CH2 범위 밖"));
        Assert.Contains(reasons, m => m.Contains("CH3 Warning"));
    }

    [Fact]
    public void CommonModeMotion_HasHighCorrelation_IndependentHasLow()
    {
        var common = Enumerable.Range(0, 30)
            .Select(i => { var z = Math.Sin(i); return S(i * 0.05, 100 + z, 100 + z, 100 + z); }).ToList();
        Assert.Equal(1.0, LaserNoiseAnalyzer.Analyze(common).MeanCorrelation, 6);

        var rng = new Random(1);
        var indep = Enumerable.Range(0, 200)
            .Select(i => S(i * 0.05, rng.NextDouble(), rng.NextDouble(), rng.NextDouble())).ToList();
        Assert.True(Math.Abs(LaserNoiseAnalyzer.Analyze(indep).MeanCorrelation) < 0.2);
    }

    [Fact]
    public void LargeTiltScatter_IsFlagged()
    {
        var samples = Enumerable.Range(0, 20)
            .Select(i => S(i * 0.05, 100, 100, 100, rx: i % 2 == 0 ? 0.5 : -0.5, ry: 0)).ToList();
        var r = LaserNoiseAnalyzer.Analyze(samples);

        Assert.True(r.RxStd > LaserNoiseReport.MaxAngleStdDeg);
        Assert.Contains(r.InstabilityReasons(), m => m.StartsWith("틸트 σ"));
    }

    [Theory]
    [InlineData(100, 0.0, 100)]
    [InlineData(100, -0.5, 100)]
    [InlineData(100, 0.5, 33.333333)]
    [InlineData(100, 0.999, 1)]
    public void EffectiveN_FollowsAr1(int n, double rho, double expected)
        => Assert.Equal(expected, LaserNoiseAnalyzer.EffectiveN(n, rho), 4);

    [Fact]
    public void AnnotateFailure_PrefixesWhenUnstable()
    {
        var noisy = LaserNoiseAnalyzer.Analyze(Enumerable.Range(0, 20)
            .Select(i => S(i * 0.05, 100 + (i % 2) * 0.5, 100, 100, rx: (i % 2) * 1.0)).ToList());
        var quiet = LaserNoiseAnalyzer.Analyze(Enumerable.Range(0, 20)
            .Select(i => S(i * 0.05, 100, 100, 100)).ToList());

        Assert.StartsWith("[측정 불안정] 틸트 보정 발산", LaserTiltCorrector.AnnotateFailure("틸트 보정 발산", noisy));
        Assert.StartsWith("틸트 보정 발산 (측정 노이즈 정상", LaserTiltCorrector.AnnotateFailure("틸트 보정 발산", quiet));
        Assert.Equal("x", LaserTiltCorrector.AnnotateFailure("x", null));
    }
}
