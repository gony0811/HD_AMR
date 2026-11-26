using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>피치 이동 실측 법선 — 앵커 광축 기준(실기 2026-11-26 천장 접촉: 도면 법선이 실면보다 1.7° 기울어 피치마다 파고듦).</summary>
public class MeasuredPitchWallOutTests
{
    private static readonly double[] ModelDown = { 0, 0, -1 };   // 천장 — 면 바깥 = BASE −Z

    [Fact]
    public void 광축이_도면과_같으면_도면_법선과_같다()
    {
        var n = WeldSequenceSupport.MeasuredPitchWallOut(new double[] { 0, 0, 1300, 0, 0, 0 }, ToolAxisDir.PlusZ, ModelDown);
        Assert.NotNull(n);
        Assert.Equal(0, n![0], 6);
        Assert.Equal(0, n[1], 6);
        Assert.Equal(-1, n[2], 6);
    }

    [Fact]
    public void 기울어진_광축을_따르고_피치는_실면과_평행하다()
    {
        // 레이저 정렬로 툴이 1.7° 기울었다 — 피치 이동(툴 X 360mm)이 광축 법선에 수직이어야 면 거리가 유지된다.
        var anchor = new double[] { 0, 0, 1300, 0, 1.7, 0 };
        var n = WeldSequenceSupport.MeasuredPitchWallOut(anchor, ToolAxisDir.PlusZ, ModelDown);
        Assert.NotNull(n);
        Assert.InRange(CobotInspectionMoveStep.AngleDeg(n!, ModelDown), 1.6, 1.8);

        var move = WeldSequenceSupport.ToolOffsetParallelToWall(anchor, new double[] { 360, 0, 0, 0, 0, 0 }, n)!;
        Assert.Equal(0, move[0] * n[0] + move[1] * n[1] + move[2] * n[2], 6);
    }

    [Fact]
    public void 광축_부호가_반대여도_도면_쪽으로_맞춘다()
    {
        var n = WeldSequenceSupport.MeasuredPitchWallOut(new double[] { 0, 0, 1300, 0, 0, 0 }, ToolAxisDir.MinusZ, ModelDown);
        Assert.NotNull(n);
        Assert.Equal(-1, n![2], 6);
    }

    [Fact]
    public void 도면과_15도_넘게_다르면_쓰지_않는다()
    {
        var n = WeldSequenceSupport.MeasuredPitchWallOut(new double[] { 0, 0, 1300, 0, 20, 0 }, ToolAxisDir.PlusZ, ModelDown);
        Assert.Null(n);
    }
}
