using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>코봇 결함 판정 — 즉시 정지 계기와 HEAVY 알람 관측값 유지.</summary>
public class CobotFaultLatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 22, 11, 35, 22, DateTimeKind.Utc);
    private static CobotFaultLatch New() => new(TimeSpan.FromSeconds(5));

    [Fact]
    public void 한_번_튄_값은_결함으로_보지_않는다()
    {
        var l = New();
        Assert.Equal(CobotFaultLatch.Edge.None, l.Update(T0, 4, 3));
        Assert.Equal(CobotFaultLatch.Edge.None, l.Update(T0.AddMilliseconds(50), 0, 0));
        Assert.Null(l.Detail);
    }

    [Fact]
    public void 연속_두_번이면_한_번만_Tripped()
    {
        var l = New();
        l.Update(T0, 4, 3);
        Assert.Equal(CobotFaultLatch.Edge.Tripped, l.Update(T0.AddMilliseconds(50), 4, 3));
        Assert.Equal(CobotFaultLatch.Edge.None, l.Update(T0.AddMilliseconds(100), 4, 3));
        Assert.Equal("결함 코드 main=4, sub=3", l.Detail);
    }

    [Fact]
    public void 결함이_오래_가다_풀리면_Cleared_뒤_바로_정상()
    {
        var l = New();
        l.Update(T0, 4, 3);
        l.Update(T0.AddMilliseconds(50), 4, 3);
        l.Update(T0.AddMinutes(6), 4, 3);
        Assert.NotNull(l.Detail);
        Assert.Equal(CobotFaultLatch.Edge.Cleared, l.Update(T0.AddMinutes(6.5), 0, 0));
        Assert.Null(l.Detail);
    }

    [Fact]
    public void 금방_풀려도_유지_시간_동안은_비정상으로_남는다()
    {
        // 자동 ResetAllError 나 빠른 리셋으로 3초(HEAVY 판정) 안에 풀려도 알람이 떠야 한다.
        var l = New();
        l.Update(T0, 4, 3);
        l.Update(T0.AddMilliseconds(50), 4, 3);
        Assert.Equal(CobotFaultLatch.Edge.Cleared, l.Update(T0.AddMilliseconds(500), 0, 0));
        Assert.NotNull(l.Detail);
        l.Update(T0.AddSeconds(4.9), 0, 0);
        Assert.NotNull(l.Detail);
        l.Update(T0.AddSeconds(5.1), 0, 0);
        Assert.Null(l.Detail);
    }

    [Fact]
    public void 풀린_뒤_다시_나면_다시_Tripped()
    {
        var l = New();
        l.Update(T0, 4, 3);
        l.Update(T0.AddMilliseconds(50), 4, 3);
        l.Update(T0.AddSeconds(10), 0, 0);
        l.Update(T0.AddSeconds(20), 2, 1);
        Assert.Equal(CobotFaultLatch.Edge.Tripped, l.Update(T0.AddSeconds(20.05), 2, 1));
        Assert.Equal("결함 코드 main=2, sub=1", l.Detail);
    }

    [Fact]
    public void HEAVY_알람이_3초_뒤_뜬다()
    {
        // 감시 서비스와 같은 판정(3초) — 결함이 0.5초 만에 풀려도 유지 시간 덕에 알람이 뜬다.
        var tracker = new DeviceAlarmTracker(TimeSpan.FromSeconds(3), TimeSpan.Zero, T0.AddMinutes(-1), new[] { DeviceKind.Cobot });
        var l = New();
        var raised = false;
        for (var ms = 0; ms <= 6000; ms += 50)
        {
            var now = T0.AddMilliseconds(ms);
            l.Update(now, ms < 500 ? 4 : 0, ms < 500 ? 3 : 0);
            var sample = l.Detail is { } d ? new DeviceHealthSample(false, d) : new DeviceHealthSample(true, "");
            var ev = tracker.Evaluate(now, new Dictionary<DeviceKind, DeviceHealthSample> { [DeviceKind.Cobot] = sample });
            raised |= ev.Any(e => e.Event == DeviceAlarmEventKind.Raised);
        }
        Assert.True(raised);
        Assert.True(tracker.HasHeavyAlarm);   // 풀려도 RESET 전까지 유지
    }
}
