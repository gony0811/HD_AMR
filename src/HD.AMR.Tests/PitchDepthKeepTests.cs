using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>Peak2 피치 중 면 거리 감시 + 마지막 한 번 맞춤
/// (실기 2026-11-20 21:04 F: 다가감 306→264mm·최소 234mm / 2026-11-22 10:03 F: 멀어짐 342·355·361mm).</summary>
public class PitchDepthKeepTests
{
    [Theory]
    [InlineData(361, 0)]       // 멀어져도 피치 중에는 움직이지 않는다
    [InlineData(264, 0)]
    [InlineData(200, 0)]       // 안전 거리 그대로
    [InlineData(190, -110)]    // 안전 거리보다 가까우면 목표로 물러남
    [InlineData(120, -180)]
    public void 피치_중에는_가까울_때만_물러난다(double measured, double expectedMove)
    {
        var (move, blocker) = PeakApproachStep.PlanPitchGuard(measured, 300);
        Assert.Null(blocker);
        Assert.Equal(expectedMove, move, 6);
    }

    [Fact]
    public void 피치_중_물러날_한계를_넘게_가까우면_멈춘다()
    {
        var (move, blocker) = PeakApproachStep.PlanPitchGuard(90, 300);
        Assert.Equal(0, move);
        Assert.NotNull(blocker);
    }

    [Theory]
    [InlineData(305, 0)]       // 목표 ±10mm 안 — 그대로
    [InlineData(361, 61)]      // 10:03 F 마지막 피치 — 60mm 한계에 걸리던 값도 한 번에
    [InlineData(264, -36)]     // 가까움 — 물러남
    [InlineData(600, 250)]     // 다가가는 쪽은 250mm 로 자름
    public void 마지막에_목표_거리로_맞춘다(double measured, double expectedMove)
    {
        var (move, blocker) = PeakApproachStep.PlanDepthKeep(measured, 300);
        Assert.Null(blocker);
        Assert.Equal(expectedMove, move, 6);
    }

    [Fact]
    public void 마지막_맞춤도_물러날_한계를_넘으면_멈춘다()
    {
        var (move, blocker) = PeakApproachStep.PlanDepthKeep(90, 300);
        Assert.Equal(0, move);
        Assert.NotNull(blocker);
    }
}
