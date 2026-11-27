using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class AmrDriveWarningTests
{
    [Theory]
    [InlineData("W07_001|1795567388265", true)]
    [InlineData("W12_005", true)]
    [InlineData("w12_008|1", true)]
    [InlineData("E13_100|1795579288528", false)]
    [InlineData("E13_101", false)]
    [InlineData("Wheel fault", false)]           // 코드가 아닌 원문 — 모르는 것은 오류로
    [InlineData("{\"msg\":\"x\"}", false)]
    [InlineData("|1795567388265", false)]        // 코드 없음
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWarning_OnlyWPlusDigitCodes(string? key, bool expected)
        => Assert.Equal(expected, AmrDriveWarning.IsWarning(key));

    [Fact]
    public void StallWatch_NotArmed_NeverStalls()
    {
        var w = new AmrDriveWarning.StallWatch();
        var t = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        Assert.False(w.Feed(t, 1, 2, 0));
        Assert.False(w.Feed(t.AddSeconds(120), 1, 2, 0));
    }

    [Fact]
    public void StallWatch_StandingStill_StallsAfterLimit()
    {
        var w = new AmrDriveWarning.StallWatch();
        var t = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        w.Arm(t);
        Assert.False(w.Feed(t, 8.83, 2.50, -3.129));
        Assert.False(w.Feed(t.AddSeconds(AmrDriveWarning.StallSec - 1), 8.831, 2.50, -3.129));
        Assert.True(w.Feed(t.AddSeconds(AmrDriveWarning.StallSec), 8.831, 2.50, -3.129));
    }

    [Fact]
    public void StallWatch_MovingOrTurning_KeepsWaiting()
    {
        var w = new AmrDriveWarning.StallWatch();
        var t = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        w.Arm(t);
        Assert.False(w.Feed(t, 8.83, 2.50, -3.129));
        // 제자리 회전(위치는 그대로) — 움직임으로 본다.
        Assert.False(w.Feed(t.AddSeconds(20), 8.83, 2.50, -2.6));
        Assert.False(w.Feed(t.AddSeconds(40), 8.83, 2.50, -2.0));
        // 직진.
        Assert.False(w.Feed(t.AddSeconds(60), 8.80, 2.50, -2.0));
        // 그 뒤로 서면 마지막 움직임부터 센다.
        Assert.False(w.Feed(t.AddSeconds(60 + AmrDriveWarning.StallSec - 1), 8.80, 2.50, -2.0));
        Assert.True(w.Feed(t.AddSeconds(60 + AmrDriveWarning.StallSec), 8.80, 2.50, -2.0));
    }

    [Fact]
    public void StallWatch_ThetaWrap_IsNotMotion()
    {
        var w = new AmrDriveWarning.StallWatch();
        var t = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        w.Arm(t);
        Assert.False(w.Feed(t, 1, 1, Math.PI - 0.001));
        // +π ↔ −π 경계를 넘나드는 표기 차이는 움직임이 아니다.
        Assert.True(w.Feed(t.AddSeconds(AmrDriveWarning.StallSec), 1, 1, -Math.PI + 0.001));
    }

    [Fact]
    public void StallWatch_RebaseAndReset()
    {
        var w = new AmrDriveWarning.StallWatch();
        var t = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        w.Arm(t);
        Assert.False(w.Feed(t, 1, 1, 0));
        // 허용 오차 안에 머무는 동안 기준을 버렸다가 다시 나오면 그때부터 센다.
        w.Rebase();
        Assert.True(w.Armed);
        Assert.False(w.Feed(t.AddSeconds(100), 1, 1, 0));
        Assert.True(w.Feed(t.AddSeconds(100 + AmrDriveWarning.StallSec), 1, 1, 0));
        // 재지령 — 감시 자체를 끈다.
        w.Reset();
        Assert.False(w.Armed);
        Assert.False(w.Feed(t.AddSeconds(500), 1, 1, 0));
    }

    [Theory]
    [InlineData(null, 120)]       // 미설정 — 기본 120초(30초였을 때 일시 정지를 실패로 봤다, 2026-11-27 09:14)
    [InlineData(45.0, 45)]
    [InlineData(10.0, 10)]
    [InlineData(600.0, 600)]
    [InlineData(5.0, 120)]        // 범위 밖 — 기본값
    [InlineData(601.0, 120)]
    [InlineData(double.NaN, 120)]
    public void ResolveStallSec_FallsBackToDefault(double? raw, double expected)
        => Assert.Equal(expected, AmrDriveWarning.ResolveStallSec(raw));

    [Fact]
    public void StallWatch_UsesConfiguredLimit()
    {
        var w = new AmrDriveWarning.StallWatch(45);
        var t = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
        w.Arm(t);
        Assert.False(w.Feed(t, 1, 1, 0));
        Assert.False(w.Feed(t.AddSeconds(44), 1, 1, 0));
        Assert.True(w.Feed(t.AddSeconds(45), 1, 1, 0));
    }

    [Fact]
    public void SeedDefault_IsDefaultStall()
    {
        var seed = Assert.Single(AmrDriveWarning.SeedDefaults());
        Assert.Equal(AmrDriveWarning.StallSecKey, seed.Name);
        Assert.Equal(120.0, double.Parse(seed.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void StallWatch_ArmTwice_KeepsReference()
    {
        var w = new AmrDriveWarning.StallWatch();
        var t = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        w.Arm(t);
        Assert.False(w.Feed(t, 1, 1, 0));
        // 두 번째 경고가 떠도 이미 세던 정체 시간을 되돌리지 않는다.
        w.Arm(t.AddSeconds(20));
        Assert.True(w.Feed(t.AddSeconds(AmrDriveWarning.StallSec), 1, 1, 0));
    }
}
