using HD.AMR.App.Enums;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// <see cref="PowerModeService"/> 디바운스/히스테리시스 전이 검증.
/// BackgroundService 수명(AMRService 폴링) 은 우회하고 <c>Observe</c> 를 직접 호출한다 —
/// 상태기 로직 그 자체가 검증 대상이기 때문.
/// </summary>
public class PowerModeServiceTests
{
    private static PowerModeService Fresh() => new PowerModeService();

    [Fact]
    public void 초기상태는_Normal()
    {
        Assert.Equal(PowerMode.Normal, Fresh().CurrentMode);
    }

    [Fact]
    public void SoC_19_한번만으로는_Low_전이_안함()
    {
        var s = Fresh();
        s.Observe(19);
        Assert.Equal(PowerMode.Normal, s.CurrentMode);
    }

    [Fact]
    public void SoC_19_연속_두번이면_Low_전이()
    {
        var s = Fresh();
        s.Observe(19);
        s.Observe(19);
        Assert.Equal(PowerMode.Low, s.CurrentMode);
        Assert.True(s.BatteryLow);
        Assert.False(s.BatteryCritical);
    }

    [Fact]
    public void SoC_9_연속_두번이면_Critical_전이()
    {
        var s = Fresh();
        s.Observe(9);
        s.Observe(9);
        Assert.Equal(PowerMode.Critical, s.CurrentMode);
        Assert.True(s.BatteryLow);
        Assert.True(s.BatteryCritical);
    }

    [Fact]
    public void Low_에서_SoC_50_복귀_안함_RESUME_80_이상_필요()
    {
        var s = Fresh();
        s.Observe(19); s.Observe(19); // → Low
        s.Observe(50); s.Observe(60);
        Assert.Equal(PowerMode.Low, s.CurrentMode);
    }

    [Fact]
    public void SoC_85_연속_두번이면_Low에서_Normal_복귀()
    {
        var s = Fresh();
        s.Observe(19); s.Observe(19); // → Low
        s.Observe(85); s.Observe(85);
        Assert.Equal(PowerMode.Normal, s.CurrentMode);
    }

    [Fact]
    public void SoC_85_연속_두번이면_Critical에서도_Normal_복귀()
    {
        var s = Fresh();
        s.Observe(9); s.Observe(9); // → Critical
        s.Observe(85); s.Observe(85);
        Assert.Equal(PowerMode.Normal, s.CurrentMode);
    }

    [Fact]
    public void SoC_20_주변_플래핑은_전이_안함()
    {
        var s = Fresh();
        s.Observe(19); s.Observe(21); s.Observe(19); s.Observe(21);
        Assert.Equal(PowerMode.Normal, s.CurrentMode);
    }

    [Fact]
    public void SoC_null_중간_삽입은_디바운스_카운터_리셋()
    {
        var s = Fresh();
        s.Observe(19); s.Observe(null); s.Observe(19);
        Assert.Equal(PowerMode.Normal, s.CurrentMode);
    }

    [Fact]
    public void SoC_null_만_오면_모드_유지()
    {
        var s = Fresh();
        s.Observe(19); s.Observe(19); // → Low
        s.Observe(null); s.Observe(null);
        Assert.Equal(PowerMode.Low, s.CurrentMode);
    }

    [Fact]
    public void Changed_이벤트는_전이_시에만_발행()
    {
        var s = Fresh();
        int count = 0;
        s.Changed += () => count++;

        s.Observe(19); // 카운터만 증가, 전이 X
        Assert.Equal(0, count);

        s.Observe(19); // 전이 Normal → Low
        Assert.Equal(1, count);

        s.Observe(19); // 유지, 전이 X
        Assert.Equal(1, count);

        s.Observe(9); s.Observe(9); // 전이 Low → Critical
        Assert.Equal(2, count);
    }

    [Fact]
    public void Critical에서_11퍼센트_올라가도_Low로_내려오지_않음()
    {
        // CRITICAL 에서 LOW 밴드 복귀는 명시적 RESUME 경로(≥80%)로만 — Phase 2 Order 게이트의 섯부른 완화 방지.
        var s = Fresh();
        s.Observe(9); s.Observe(9); // → Critical
        s.Observe(15); s.Observe(15);
        Assert.Equal(PowerMode.Critical, s.CurrentMode);
    }

    [Fact]
    public void LastObservedSoc는_마지막_관측값을_그대로_노출()
    {
        var s = Fresh();
        s.Observe(42.5f);
        Assert.Equal(42.5f, s.LastObservedSoc);
        s.Observe(null);
        Assert.Null(s.LastObservedSoc);
    }

    [Theory]
    [InlineData(20)]   // 경계
    [InlineData(10)]   // 경계
    public void 임계값_경계는_포함_즉_같은값도_저전력(float boundary)
    {
        var s = Fresh();
        s.Observe(boundary); s.Observe(boundary);
        // 20 → Low, 10 → Critical
        if (boundary <= PowerModeService.CriticalThresholdPercent)
            Assert.Equal(PowerMode.Critical, s.CurrentMode);
        else
            Assert.Equal(PowerMode.Low, s.CurrentMode);
    }
}
