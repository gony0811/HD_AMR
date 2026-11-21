using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>피치 이동 실측 면 법선 — 켜기 해석과, M2 22:33 실측 자세로 벽에서 멀어지지 않는지.</summary>
public class PitchMeasuredNormalTests
{
    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]   // 비전(용접검사기)은 비우면 종전 모델 법선
    [InlineData("", true, true)]
    [InlineData("off", true, false)]
    [InlineData("on", false, true)]
    [InlineData("false", true, false)]
    public void 켜기_파라미터를_해석한다(string? raw, bool gas, bool expected)
        => Assert.Equal(expected, BundleTeach.ResolveUseMeasuredNormal(raw, gas));

    // M2 2026-11-21 22:33 점1: 레이저 정렬 뒤 TCP 자세 RPY [92, 0, 77.8], 광축 툴 -Z.
    private static readonly double[] Point1Pose = { -1371.3, 1296.0, 741.8, 92.0, 0.0, 77.8 };

    [Fact]
    public void 모델_법선이면_360mm_에_57mm_멀어진다()
    {
        // 그날 실제로 간 오프셋(모델 법선 기준) — 실측 면 법선으로 재면 바깥 성분이 ~57mm.
        var measured = CobotInspectionMoveStep.WallOutNormal(Point1Pose, ToolAxisDir.MinusZ);
        var used = new[] { -18.9, -359.5, 0.1 };
        var outMm = used[0] * measured[0] + used[1] * measured[1] + used[2] * measured[2];
        Assert.InRange(outMm, 50, 65);
    }

    [Fact]
    public void 실측_법선이면_360mm_가도_면_거리가_그대로()
    {
        var measured = CobotInspectionMoveStep.WallOutNormal(Point1Pose, ToolAxisDir.MinusZ);
        var move = WeldSequenceSupport.ToolOffsetParallelToWall(Point1Pose, new[] { -360.0, 0, 0 }, measured)!;
        var outMm = move[0] * measured[0] + move[1] * measured[1] + move[2] * measured[2];
        Assert.InRange(outMm, -0.01, 0.01);
        Assert.Equal(360, Math.Sqrt(move[0] * move[0] + move[1] * move[1] + move[2] * move[2]), 3);
    }

    [Fact]
    public void 법선_사이_각()
        => Assert.Equal(90, BundleTeach.AngleBetweenDeg(new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }), 6);
}
