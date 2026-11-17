using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>텔레스코픽 지정 높이 이동 완료 대기 한도(<see cref="TelescopicService.CompletionTimeoutFor"/>) — 이동 거리에 비례.</summary>
public class TelescopicTimeoutTests
{
    [Fact]
    public void 전체_스트로크는_실측_소요보다_넉넉하다()
    {
        // 2026-11-16 17:21 L3-R03C15: 0→999mm, 실측 약 7~8mm/s(≈135s) — 고정 90s 에서 762mm 로 타임아웃.
        var t = TelescopicService.CompletionTimeoutFor(999);
        Assert.True(t.TotalSeconds >= 999 / 7.0 + 20, $"{t.TotalSeconds}s");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-100)]
    public void 짧은_이동은_종전_90초를_유지한다(double travel)
        => Assert.Equal(90, TelescopicService.CompletionTimeoutFor(travel).TotalSeconds, 6);

    [Fact]
    public void 내려가는_이동도_거리로_계산한다()
        => Assert.Equal(TelescopicService.CompletionTimeoutFor(952), TelescopicService.CompletionTimeoutFor(-952));
}
