using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// 좌표 환산에 쓸 스트로크 판정. T_A_B 의 tz 가 스트로크만큼(최대 1m) 변하므로 모르는 높이를
/// 0 으로 가정하면 안 된다 — 못 읽음·무효·오래됨은 전부 null 이어야 한다.
/// </summary>
public class TelescopicStrokeTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(3);

    private static TelescopicStatus Status(int heightMm) => new(
        Active: true, Mode: default, Unlocked: true, Imperial: false,
        SoundEnabled: null, ParameterFlag: null, ParameterMode: null, HeightBlinking: null,
        ErrorCode: 0, HeightMm: heightMm, ExactLayout: true, Raw: "");

    [Fact]
    public void 최근_유효값은_그대로_쓴다()
    {
        var s = TelescopicService.ResolveStrokeMm(Status(640), Now.AddSeconds(-1), Now, MaxAge, out var reason);
        Assert.Equal(640, s);
        Assert.Null(reason);
    }

    [Fact]
    public void 완전_하강_0_은_유효하다()
    {
        Assert.Equal(0, TelescopicService.ResolveStrokeMm(Status(0), Now, Now, MaxAge, out _));
    }

    [Fact]
    public void 못_읽었으면_null()
    {
        Assert.Null(TelescopicService.ResolveStrokeMm(null, null, Now, MaxAge, out var reason));
        Assert.Contains("읽지 못했습니다", reason);
    }

    [Fact]
    public void 무효_높이는_null()
    {
        Assert.Null(TelescopicService.ResolveStrokeMm(Status(-1), Now, Now, MaxAge, out var reason));
        Assert.Contains("무효", reason);
    }

    [Fact]
    public void 오래된_값은_null()
    {
        Assert.Null(TelescopicService.ResolveStrokeMm(Status(640), Now.AddSeconds(-10), Now, MaxAge, out var reason));
        Assert.Contains("10.0초 전", reason);
    }
}
