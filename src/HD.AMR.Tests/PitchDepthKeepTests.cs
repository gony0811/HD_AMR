using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>Peak2 피치 이동 뒤 깊이로 면 거리 유지(실기 2026-11-20 21:04 F: 360mm×2 동안 306→264mm, 최소 234mm).</summary>
public class PitchDepthKeepTests
{
    [Theory]
    [InlineData(305, 0)]       // 목표 ±10mm 안 — 그대로
    [InlineData(291, 0)]
    [InlineData(264, -36)]     // 가까워짐 — 그만큼 물러남
    [InlineData(234, -66)]
    [InlineData(330, 30)]      // 멀어짐 — 다가감
    [InlineData(420, 60)]      // 다가가는 쪽은 60mm 로 자름
    public void 목표_거리로_광축만_맞춘다(double measured, double expectedMove)
    {
        var (move, blocker) = PeakApproachStep.PlanDepthKeep(measured, 300);
        Assert.Null(blocker);
        Assert.Equal(expectedMove, move, 6);
    }

    [Fact]
    public void 한번에_물러날_한계를_넘게_가까우면_움직이지_않고_멈춘다()
    {
        var (move, blocker) = PeakApproachStep.PlanDepthKeep(90, 300);
        Assert.Equal(0, move);
        Assert.NotNull(blocker);
    }
}
