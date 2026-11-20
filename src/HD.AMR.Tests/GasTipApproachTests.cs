using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>검사 팁 진입 — 벽면 쪽 방향 판정과 작업물 Z 이동. 좌표는 <see cref="BundleEndWdTests"/> 와 같은 실기값
/// (작업물 Z ≈ BASE +Z, 툴은 면 위 34mm 에서 광축 −Z 로 면을 본다).</summary>
public class GasTipApproachTests
{
    private static readonly double[] Frame = { -175.6, -1107.1, -1009.6, 1.34, -2.12, -0.61 };
    private static readonly double[] Tool = { -176.8, -1107.9, -975.8, -1.34, 2.12, 179.38 };

    [Fact]
    public void 광축이_면을_향하면_작업물_Z_음수_쪽이_벽면()
    {
        Assert.Equal(-10, GasTipApproachStep.WallwardFrameDz(Frame, Tool, ToolAxisDir.MinusZ, 10));
        // 광축이 반대(+Z)로 설정되면 벽면 쪽도 반대
        Assert.Equal(10, GasTipApproachStep.WallwardFrameDz(Frame, Tool, ToolAxisDir.PlusZ, 10));
    }

    [Fact]
    public void 진입은_자세_그대로_면_법선을_따라_거리만큼()
    {
        var dz = GasTipApproachStep.WallwardFrameDz(Frame, Tool, ToolAxisDir.MinusZ, 10);
        var moved = GasTipApproachStep.ShiftAlongFrameZ(Tool, Frame, dz);
        var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (moved[i] - Tool[i]) * (moved[i] - Tool[i])));
        Assert.Equal(10, d, 6);
        Assert.True(moved[2] < Tool[2] - 9.9);   // 면(아래) 쪽으로
        Assert.Equal(Tool[3..], moved[3..]);

        // 작업물 좌표로 보면 Z 만 dz 만큼 바뀐다 — 경유점 Z 에 dz 를 더한 것과 같은 자리
        var f = FrameMath.PoseToMatrix(Frame);
        var local = Enumerable.Range(0, 3)
            .Select(c => Enumerable.Range(0, 3).Sum(i => (moved[i] - Tool[i]) * f[i, c])).ToArray();
        Assert.Equal(0, local[0], 6);
        Assert.Equal(0, local[1], 6);
        Assert.Equal(dz, local[2], 6);

        var back = GasTipApproachStep.ShiftAlongFrameZ(moved, Frame, -dz);
        for (var i = 0; i < 6; i++) Assert.Equal(Tool[i], back[i], 6);
    }
}
