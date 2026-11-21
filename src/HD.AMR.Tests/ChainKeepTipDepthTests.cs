using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 앞 구역이 검사 팁 진입 깊이를 유지했을 때 연속 이동이 띄우지 않는지(실기 2026-11-21 18:26: 깊이 50mm,
/// 높이 −16mm 로 보여 100mm 까지 들어 올림).
/// </summary>
public class ChainKeepTipDepthTests
{
    private static readonly double[] Origin = { 0, 0, 0 };
    private static readonly double[] WallOut = { 1, 0, 0 };   // 면 바깥 = +X

    [Fact]
    public void 진입_깊이만큼_붙어_있으면_높이를_유지한다()
    {
        var r = CobotInspectionMoveStep.KeepHeightForKeptTipDepth(
            new double[] { -16.2, 0, 1, 0, 0, 0 }, Origin, WallOut, keptDepthMm: 50, tolMm: 50);
        Assert.NotNull(r);
        Assert.Equal(-16.2, r!.Value.HeightMm, 3);
        Assert.Equal(new[] { -16.2, 0, 0 }, r.Value.Target);
    }

    [Fact]
    public void 진입_깊이보다_더_붙어_있으면_띄운다()
        => Assert.Null(CobotInspectionMoveStep.KeepHeightForKeptTipDepth(
            new double[] { -45, 0, 0, 0, 0, 0 }, Origin, WallOut, keptDepthMm: 50, tolMm: 50));

    [Fact]
    public void 옆으로_멀면_띄운다()
        => Assert.Null(CobotInspectionMoveStep.KeepHeightForKeptTipDepth(
            new double[] { -16, 0, 120, 0, 0, 0 }, Origin, WallOut, keptDepthMm: 50, tolMm: 50));

    [Fact]
    public void 유지한_깊이가_없으면_쓰지_않는다()
        => Assert.Null(CobotInspectionMoveStep.KeepHeightForKeptTipDepth(
            new double[] { -16, 0, 0, 0, 0, 0 }, Origin, WallOut, keptDepthMm: 0, tolMm: 50));
}
