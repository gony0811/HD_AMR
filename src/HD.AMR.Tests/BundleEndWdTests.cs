using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 묶음 마지막 용접선 끝 WD 로 좌표계 기울기 보정 — 값은 실기 2026-11-17 16:52 B L1-R04C13 #4
/// (점1→점2 기울기 2.9° 를 360mm 연장해 툴이 13mm 올라간 경우).
/// </summary>
public class BundleEndWdTests
{
    private static readonly double[] Frame = { -175.6, -1107.1, -1009.6, 1.34, -2.12, -0.61 };
    private static readonly double[] Tool = { -176.8, -1107.9, -975.8, -1.34, 2.12, 179.38 };

    private static double[] PointAlongX(double[] frame, double mm)
    {
        var m = FrameMath.PoseToMatrix(frame);
        return new[] { frame[0] + mm * m[0, 0], frame[1] + mm * m[1, 0], frame[2] + mm * m[2, 0] };
    }

    [Fact]
    public void 끝점_탐침은_좌표계_X_로_같은_자세()
    {
        var end = BundleEndWdStep.EndProbePose(Tool, Frame, 360);
        var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (end[i] - Tool[i]) * (end[i] - Tool[i])));
        Assert.Equal(360, d, 6);
        Assert.Equal(Tool[3], end[3]);
        Assert.Equal(Tool[5], end[5]);
        Assert.True(end[2] - Tool[2] > 10);   // 실측 기울기(오르막)를 그대로 따라간다
    }

    [Fact]
    public void 차이가_0이면_좌표계_그대로()
    {
        var f = BundleEndWdStep.TiltFrameToEnd(Frame, Tool, ToolAxisDir.MinusZ, 360, 0, out var tilt);
        Assert.Equal(0, tilt, 6);
        var a = PointAlongX(Frame, 360);
        var b = PointAlongX(f, 360);
        for (var i = 0; i < 3; i++) Assert.Equal(a[i], b[i], 3);
    }

    [Fact]
    public void 끝이_멀면_X_끝을_면_쪽으로_내린다()
    {
        // 끝점 레이저가 13mm 더 멀다 = 바닥이 연장선보다 13mm 낮다 → 좌표계 X 끝이 BASE z 로 약 13mm 내려가야 한다.
        var f = BundleEndWdStep.TiltFrameToEnd(Frame, Tool, ToolAxisDir.MinusZ, 360, 13, out var tilt);
        var before = PointAlongX(Frame, 360);
        var after = PointAlongX(f, 360);

        Assert.InRange(before[2] - after[2], 12.0, 14.0);
        Assert.InRange(tilt, -2.2, -1.9);
        Assert.Equal(Frame[0], f[0], 6);   // 원점(시작점)은 그대로
        Assert.Equal(Frame[2], f[2], 6);

        // 좌표계는 여전히 직교 정규
        var m = FrameMath.PoseToMatrix(f);
        for (var c = 0; c < 3; c++)
            Assert.Equal(1.0, Math.Sqrt(m[0, c] * m[0, c] + m[1, c] * m[1, c] + m[2, c] * m[2, c]), 6);
    }

    [Fact]
    public void 끝이_가까우면_올린다()
    {
        var f = BundleEndWdStep.TiltFrameToEnd(Frame, Tool, ToolAxisDir.MinusZ, 360, -10, out var tilt);
        Assert.InRange(PointAlongX(f, 360)[2] - PointAlongX(Frame, 360)[2], 9.0, 11.0);
        Assert.True(tilt > 0);
    }

    [Fact]
    public void 오케스트레이터_기본_스텝에는_끼지_않는다()
    {
        var keys = WeldInspectionOrchestrator.SelectStepKeys(false, null,
            new[] { "cobotInspection", BundleEndWdStep.StepKey, "inspectionRun" }, anchorHit: true, isLastInspection: true);
        Assert.DoesNotContain(BundleEndWdStep.StepKey, keys);
    }
}
