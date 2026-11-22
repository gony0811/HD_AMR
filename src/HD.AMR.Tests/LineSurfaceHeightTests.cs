using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 다른 줄 좌표계 재사용 — 좌표계 하나를 수직·수평 줄이 같이 쓰고, 줄마다 시작·끝 레이저 면 높이로 높이·기울기를 보정한다.
/// 좌표계 값은 실기 2026-11-17 B L1-R04C13 묶음 좌표계.
/// </summary>
public class LineSurfaceHeightTests
{
    private static readonly double[] Frame = { -175.6, -1107.1, -1009.6, 1.34, -2.12, -0.61 };

    private static double[] At(double[] frame, double x, double y, double z)
    {
        var p = FrameMath.FromFrame(new[] { x, y, z, 0.0, 0.0, 0.0 }, frame);
        return new[] { p[0], p[1], p[2] };
    }

    private static double[] Col(double[] frame, int c)
    {
        var m = FrameMath.PoseToMatrix(frame);
        return new[] { m[0, c], m[1, c], m[2, c] };
    }

    [Fact]
    public void 면이_좌표계와_같으면_그대로()
    {
        var (f, off, tilt, rise, span) = InternalWorkpieceFrame.CorrectBySurfaceHeights(
            Frame, At(Frame, 5, 70, 0), At(Frame, 355, 70, 0), referenceHeightMm: 0);
        Assert.Equal(0, off, 6);
        Assert.Equal(0, tilt, 6);
        Assert.Equal(0, rise, 6);
        Assert.Equal(350, span, 6);
        for (var i = 0; i < 6; i++) Assert.Equal(Frame[i], f[i], 6);
    }

    [Fact]
    public void 시작이_기준보다_높고_끝이_더_높으면_올리고_기울인다()
    {
        // 시작 면 높이 +8mm(기준 0), 끝은 +8+13mm — 면이 좌표계보다 바깥에 있고 X 를 따라 올라간다.
        var start = At(Frame, 0, 70, 8);
        var end = At(Frame, 360, 70, 21);
        var (f, off, tilt, rise, span) = InternalWorkpieceFrame.CorrectBySurfaceHeights(Frame, start, end, referenceHeightMm: 0);

        Assert.Equal(8, off, 6);
        Assert.Equal(13, rise, 6);
        Assert.Equal(Math.Atan2(13, 360) * 180 / Math.PI, tilt, 6);
        // 보정한 좌표계에서는 두 측정점이 모두 기준 높이(0)에 있다 — 면을 따라 눕고 높이가 맞았다.
        Assert.Equal(0, InternalWorkpieceFrame.AlongAndHeight(f, start).Height, 6);
        Assert.Equal(0, InternalWorkpieceFrame.AlongAndHeight(f, end).Height, 6);
        // Y 축(옆 방향)은 그대로 — 기울기는 Y 축 둘레 회전뿐.
        var y0 = Col(Frame, 1);
        var y1 = Col(f, 1);
        for (var i = 0; i < 3; i++) Assert.Equal(y0[i], y1[i], 9);
    }

    [Fact]
    public void 기준이_없으면_시작_높이는_두고_기울기만()
    {
        var start = At(Frame, 0, -40, 5);
        var end = At(Frame, 360, -40, -5);
        var (f, off, tilt, _, _) = InternalWorkpieceFrame.CorrectBySurfaceHeights(Frame, start, end, referenceHeightMm: null);
        Assert.Equal(0, off, 6);
        Assert.True(tilt < 0);
        Assert.Equal(5, InternalWorkpieceFrame.AlongAndHeight(f, start).Height, 6);
        Assert.Equal(5, InternalWorkpieceFrame.AlongAndHeight(f, end).Height, 6);
    }

    [Fact]
    public void 끝점이_없거나_짧으면_높이만()
    {
        var start = At(Frame, 0, 0, -6);
        var (f, off, tilt, _, _) = InternalWorkpieceFrame.CorrectBySurfaceHeights(Frame, start, null, referenceHeightMm: 0);
        Assert.Equal(-6, off, 6);
        Assert.Equal(0, tilt, 6);
        Assert.Equal(0, InternalWorkpieceFrame.AlongAndHeight(f, start).Height, 6);
        for (var i = 3; i < 6; i++) Assert.Equal(Frame[i], f[i], 6);

        var (_, _, tilt2, _, _) = InternalWorkpieceFrame.CorrectBySurfaceHeights(
            Frame, start, At(Frame, 30, 0, 10), referenceHeightMm: 0);
        Assert.Equal(0, tilt2, 6);   // 30mm < 50mm — 기울기 보정 안 함
    }

    [Fact]
    public void 수직_줄로_옮기면_좌표계를_90도_돌린다()
    {
        var x = Col(Frame, 0);
        var y = Col(Frame, 1);
        var from = At(Frame, 0, 0, 0);
        var to = At(Frame, 200, 300, 0);
        var (moved, rot, err, _) = InternalWorkpieceFrame.MoveToOtherLine(Frame, from, to, y);
        Assert.NotNull(moved);
        Assert.Equal(90, rot, 6);
        Assert.Equal(0, err, 6);
        var nx = Col(moved!, 0);
        for (var i = 0; i < 3; i++) Assert.Equal(y[i], nx[i], 6);
        // Z(면 법선)는 그대로, 원점은 이번 시작점(면 위)
        var z0 = Col(Frame, 2);
        var z1 = Col(moved!, 2);
        for (var i = 0; i < 3; i++) Assert.Equal(z0[i], z1[i], 6);
        for (var i = 0; i < 3; i++) Assert.Equal(to[i], moved![i], 6);
        _ = x;
    }

    [Theory]
    [InlineData(true, "O1", "G1", "PU", true, 0.0, true)]     // 같은 지령·정차·면, 평행
    [InlineData(true, "O1", "G1", "PU", true, 1.5, true)]     // 수직↔수평(격자 오차 1.5°)도 재사용
    [InlineData(true, "O1", "G1", "PU", true, 25.0, false)]   // 사선 — 축에서 벗어남
    [InlineData(true, "O2", "G1", "PU", true, 0.0, false)]    // 다른 지령(주행 뒤)
    [InlineData(true, "O1", "G2", "PU", true, 0.0, false)]    // 다른 정차
    [InlineData(true, "O1", "G1", "SU", true, 0.0, false)]    // 다른 면
    [InlineData(false, "O1", "G1", "PU", true, 0.0, false)]   // CORNER
    [InlineData(true, "O1", "G1", "PU", false, 0.0, false)]   // 프레임·기준 시작점 없음
    public void 재사용_조건(bool notCorner, string order, string group, string wall, bool hasFrame, double gridErr, bool expected)
        => Assert.Equal(expected, WeldInspectionOrchestrator.CanReuseAcrossLines(
            notCorner, ("O1", "G1", "PU"), order, group, wall, hasFrame, gridErr));

    [Theory]
    [InlineData(false, true, false, false, null)]
    [InlineData(true, false, false, false, null)]
    [InlineData(true, true, false, false, LineSurfaceHeightStep.ModeReference)]
    [InlineData(true, true, true, true, LineSurfaceHeightStep.ModeCorrect)]
    [InlineData(true, true, true, false, null)]   // 같은 줄 anchor 적중 — 기준 그대로 이어 씀
    public void 측정_모드(bool reuse, bool notCorner, bool anchorHit, bool cross, string? expected)
        => Assert.Equal(expected, WeldInspectionOrchestrator.LineSurfaceMode(reuse, notCorner, anchorHit, cross));

    [Fact]
    public void 보정량_한계를_넘으면_멈춘다()
    {
        Assert.Null(LineSurfaceHeightStep.Problem(12, -20));
        Assert.Null(LineSurfaceHeightStep.Problem(50, 0));
        Assert.NotNull(LineSurfaceHeightStep.Problem(81, 0));
        Assert.NotNull(LineSurfaceHeightStep.Problem(0, -31));
    }

    [Fact]
    public void 레이저_평면_점과_기울기()
    {
        // 툴이 좌표계와 같은 자세(광축 툴 Z)일 때 툴 Z 로 400mm 앞 평면 — 좌표계 Z 높이 400(광축 쪽이 −Z 가 아닌 이 가정에서는 +), 기울기 0.
        var tool = (double[])Frame.Clone();
        var plane = new PlanePose(0, 75, 400, 0, 0, 0, new double[] { 0, 0, 1 }, true, null);
        var p = LineSurfaceHeightStep.SurfacePointBase(plane, tool, ToolAxisDir.PlusZ);
        var (a, h) = InternalWorkpieceFrame.AlongAndHeight(Frame, p);
        Assert.Equal(0, a, 6);
        Assert.Equal(400, h, 6);

        // 광축이 툴 −Z(가스 검사 AMR)면 면은 툴 −Z 쪽 400mm — 뒤집어 놓지 않으면 벽이 멀수록 높이가 커져 보정이 반대로 걸린다.
        var minus = LineSurfaceHeightStep.SurfacePointBase(plane, tool, ToolAxisDir.MinusZ);
        Assert.Equal(-400, InternalWorkpieceFrame.AlongAndHeight(Frame, minus).Height, 6);
        var outward = LineSurfaceHeightStep.OutwardNormal(Frame, tool, ToolAxisDir.MinusZ);
        Assert.Equal(-400, LineSurfaceHeightStep.HeightOutMm(Frame, new[] { minus[0], minus[1], minus[2] }, outward), 6);
        Assert.Equal(-400, LineSurfaceHeightStep.WdReferenceHeightMm(Frame, outward, 400), 6);
        // 벽이 5mm 더 멀면 면 점은 바깥 기준으로 5mm 더 깊다(높이가 작아진다).
        var far = LineSurfaceHeightStep.SurfacePointBase(plane with { Z = 405 }, tool, ToolAxisDir.MinusZ);
        Assert.Equal(-405, LineSurfaceHeightStep.HeightOutMm(Frame, new[] { far[0], far[1], far[2] }, outward), 6);
        Assert.Equal(0, LineSurfaceHeightStep.PlaneTiltToFrameDeg(plane, tool, Frame), 6);

        var s = Math.Sin(10 * Math.PI / 180);
        var tilted = plane with { Normal = new[] { s, 0, Math.Cos(10 * Math.PI / 180) } };
        Assert.Equal(10, LineSurfaceHeightStep.PlaneTiltToFrameDeg(tilted, tool, Frame), 6);
        var flipped = plane with { Normal = new double[] { 0, 0, -1 } };
        Assert.Equal(0, LineSurfaceHeightStep.PlaneTiltToFrameDeg(flipped, tool, Frame), 6);
    }

    [Fact]
    public void 들어올림은_면_바깥_법선()
    {
        // 툴 자세 = 좌표계 자세, 광축 툴 −Z → 면 바깥은 좌표계 +Z.
        var outward = LineSurfaceHeightStep.OutwardNormal(Frame, Frame, ToolAxisDir.MinusZ);
        var z = Col(Frame, 2);
        for (var i = 0; i < 3; i++) Assert.Equal(z[i], outward[i], 9);
        var inverse = LineSurfaceHeightStep.OutwardNormal(Frame, Frame, ToolAxisDir.PlusZ);
        for (var i = 0; i < 3; i++) Assert.Equal(-z[i], inverse[i], 9);
    }

    [Fact]
    public void 오케스트레이터_기본_스텝에는_끼지_않는다()
    {
        var keys = WeldInspectionOrchestrator.SelectStepKeys(false, null,
            new[] { "cobotInspection", LineSurfaceHeightStep.StepKey, "inspectionRun" }, anchorHit: false, isLastInspection: true);
        Assert.DoesNotContain(LineSurfaceHeightStep.StepKey, keys);
    }

    [Fact]
    public void 사전_검증만_실패하면_물려받은_좌표계는_유지한다()
    {
        Assert.True(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, "inspectionRun", true));
        Assert.True(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, "inspectionRunDry", true));     // 드라이런 순회
        Assert.False(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(false, "inspectionRun", true));    // 이 시퀀스에서 새로 교시한 좌표계
        Assert.False(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, "inspectionRun", false));    // 움직인 뒤 실패
        Assert.False(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, "cobotInspection", true));   // 다른 단계 실패
        Assert.False(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, null, true));
    }

    [Fact]
    public void 측정_높이로_옮기면_좌표계_면에서_그_높이에_선다()
    {
        // 좌표계 면 위 34mm(순회 대기 높이)에 서 있다가 측정 높이 0mm(교시 높이)로 간다 — 종전처럼 30mm 더 띄우면 레이저가 범위를 넘는다.
        var outward = LineSurfaceHeightStep.OutwardNormal(Frame, Frame, ToolAxisDir.MinusZ);
        var p = At(Frame, 120, -15, 0);
        var start = new[] { p[0] + 34 * outward[0], p[1] + 34 * outward[1], p[2] + 34 * outward[2], Frame[3], Frame[4], Frame[5] };
        Assert.Equal(34, LineSurfaceHeightStep.HeightOutMm(Frame, start, outward), 6);

        var travel = LineSurfaceHeightStep.DefaultMeasureHeightMm - LineSurfaceHeightStep.HeightOutMm(Frame, start, outward);
        var at = LineSurfaceHeightStep.Offset(start, outward, travel);
        Assert.Equal(0, LineSurfaceHeightStep.HeightOutMm(Frame, at, outward), 6);
        // 면을 따라서는 움직이지 않는다.
        var (along, _) = InternalWorkpieceFrame.AlongAndHeight(Frame, at);
        Assert.Equal(120, along, 6);
        for (var i = 3; i < 6; i++) Assert.Equal(start[i], at[i], 9);
    }

    [Fact]
    public void WD_기준이면_시작과_끝에서_좌표계_면이_벽에서_WD_만큼_떨어진다()
    {
        // 물려받은 좌표계가 벽에서 시작점 405mm, 끝점 409mm 떨어져 있다(WD 목표 400) — 보정 뒤에는 두 점 모두 400mm.
        var outward = LineSurfaceHeightStep.OutwardNormal(Frame, Frame, ToolAxisDir.MinusZ);
        var reference = LineSurfaceHeightStep.WdReferenceHeightMm(Frame, outward, 400);
        Assert.Equal(400, Math.Abs(reference), 9);
        var toWall = Math.Sign(reference);   // 벽 쪽 좌표계 Z 부호
        var start = At(Frame, 10, 150, toWall * 405);
        var end = At(Frame, 370, 150, toWall * 409);

        var (f, off, _, rise, _) = InternalWorkpieceFrame.CorrectBySurfaceHeights(Frame, start, end, reference);
        Assert.Equal(toWall * 5, off, 6);
        Assert.Equal(toWall * 4, rise, 6);
        Assert.Null(LineSurfaceHeightStep.Problem(off, rise));
        Assert.Equal(reference, InternalWorkpieceFrame.AlongAndHeight(f, start).Height, 3);
        Assert.Equal(reference, InternalWorkpieceFrame.AlongAndHeight(f, end).Height, 3);
        // 벽은 바깥 법선의 반대쪽 — 바깥이 뒤집히면 기준 부호도 뒤집힌다.
        var inverse = LineSurfaceHeightStep.OutwardNormal(Frame, Frame, ToolAxisDir.PlusZ);
        Assert.Equal(-reference, LineSurfaceHeightStep.WdReferenceHeightMm(Frame, inverse, 400), 9);
    }
}
