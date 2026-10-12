using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class ArrivalSettleDetectorTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double sec) => T0.AddSeconds(sec);

    private static ArrivalSettleDetector New() => new(TimeSpan.FromSeconds(2), 0.005, 0.005);

    [Fact]
    public void 진입_후_계속_이동하면_정차가_아니고_멈춘_뒤_window_경과해야_정차()
    {
        // 4회차 재현 — 진입 y=12.212, 1초 뒤 12.338 에서 정지.
        var d = New();
        Assert.Equal(SettleResult.Moving, d.Feed(At(0), 5.0, 12.212, 1.57, true));
        Assert.Equal(SettleResult.Moving, d.Feed(At(1), 5.0, 12.338, 1.57, true));
        Assert.Equal(SettleResult.Moving, d.Feed(At(2), 5.0, 12.338, 1.57, true));
        Assert.Equal(SettleResult.Settled, d.Feed(At(3), 5.0, 12.338, 1.57, true));

        var drift = d.DriftSinceEntry()!.Value;
        Assert.Equal(0.126, drift.Dxy, 3);
        Assert.Equal(At(0), d.FirstInToleranceUtc);
    }

    [Fact]
    public void 각도만_변해도_정차가_아니다()
    {
        // 3회차 재현 — θ 1.588 → 1.493.
        var d = New();
        d.Feed(At(0), 5.0, 12.3, 1.588, true);
        d.Feed(At(1), 5.0, 12.3, 1.540, true);
        Assert.Equal(SettleResult.Moving, d.Feed(At(2), 5.0, 12.3, 1.493, true));
        Assert.Equal(SettleResult.Moving, d.Feed(At(3), 5.0, 12.3, 1.493, true));
        Assert.Equal(SettleResult.Settled, d.Feed(At(4), 5.0, 12.3, 1.493, true));
        Assert.Equal(0.095, d.DriftSinceEntry()!.Value.DTheta, 3);
    }

    [Fact]
    public void 같은_샘플_시각이_반복되면_window를_채우지_않는다()
    {
        var d = New();
        d.Feed(At(0), 5.0, 12.3, 1.57, true);
        for (var i = 0; i < 10; i++)
            Assert.Equal(SettleResult.Moving, d.Feed(At(0), 5.0, 12.3, 1.57, true));
        Assert.Equal(SettleResult.Settled, d.Feed(At(2), 5.0, 12.3, 1.57, true));
    }

    [Fact]
    public void 허용_오차_이탈하면_진입이_리셋된다()
    {
        var d = New();
        d.Feed(At(0), 5.0, 12.2, 1.57, true);
        d.Feed(At(1.5), 5.0, 12.2, 1.57, true);
        Assert.Equal(SettleResult.OutOfTolerance, d.Feed(At(1.9), 5.0, 12.6, 1.57, false));
        Assert.Null(d.FirstInToleranceUtc);

        Assert.Equal(SettleResult.Moving, d.Feed(At(3), 5.0, 12.3, 1.57, true));
        Assert.Equal(At(3), d.FirstInToleranceUtc);
        Assert.Equal(SettleResult.Moving, d.Feed(At(4), 5.0, 12.3, 1.57, true));
        Assert.Equal(SettleResult.Settled, d.Feed(At(5), 5.0, 12.3, 1.57, true));
    }

    [Fact]
    public void 처음부터_정지_상태면_window_경과_후_정차()
    {
        var d = New();
        Assert.Equal(SettleResult.Moving, d.Feed(At(0), 1, 1, 0, true));
        Assert.Equal(SettleResult.Moving, d.Feed(At(1), 1, 1, 0, true));
        Assert.Equal(SettleResult.Settled, d.Feed(At(2), 1, 1, 0, true));
    }

    [Fact]
    public void 엡실론_이내_지터는_정지로_인정한다()
    {
        var d = New();
        d.Feed(At(0), 1.000, 1.000, 0.000, true);
        d.Feed(At(1), 1.003, 0.998, 0.003, true);
        Assert.Equal(SettleResult.Settled, d.Feed(At(2), 0.998, 1.002, -0.002, true));
    }

    [Fact]
    public void 각도_랩어라운드는_작은_변화로_본다()
    {
        var d = New();
        d.Feed(At(0), 1, 1, Math.PI - 0.001, true);
        d.Feed(At(1), 1, 1, -Math.PI + 0.001, true);
        Assert.Equal(SettleResult.Settled, d.Feed(At(2), 1, 1, -Math.PI + 0.001, true));
    }

    [Fact]
    public void Reset_후_처음부터_다시_판정()
    {
        var d = New();
        d.Feed(At(0), 1, 1, 0, true);
        d.Feed(At(2), 1, 1, 0, true);
        d.Reset();
        Assert.Null(d.FirstInToleranceUtc);
        Assert.Equal(SettleResult.Moving, d.Feed(At(3), 1, 1, 0, true));
        Assert.Equal(SettleResult.Settled, d.Feed(At(5), 1, 1, 0, true));
    }
}
