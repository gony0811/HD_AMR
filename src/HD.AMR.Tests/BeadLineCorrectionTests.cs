using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class BeadLineCorrectionTests
{
    // 수직 열 좌표계(현장과 비슷한 자세) — X = BASE +Z(위), 면 법선 ≈ BASE +X.
    private static readonly double[] ColumnFrame = { -1484.0, -66.9, -912.3, 90.0, -88.7, 90.0 };

    private static SeamAlongPeakAnchor.BeadProbePoint Point(double at, double? bead, double? coverage = 0.85)
        => new(at, new double[6], bead, bead is null ? null : new double[3], null, "", coverage);

    private static double[] ToFrame(double[] frame, double[] pointBase)
        => FrameMath.ToFrame(new[] { pointBase[0], pointBase[1], pointBase[2], 0, 0, 0 }, frame);

    private static double[] FromFrame(double[] frame, double x, double y, double z)
        => FrameMath.FromFrame(new[] { x, y, z, 0, 0, 0 }, frame);

    [Fact]
    public void Apply_PutsBothMeasuredBeadPointsOnTheNewXAxis()
    {
        // 2026-11-26 C07: 원점 자리 −8.0mm, 1080mm 자리 −18.0mm.
        var rot = InternalWorkpieceFrame.BeadLineRotationDeg(-8.0, -18.0, 1080.0);
        var corrected = InternalWorkpieceFrame.ApplyBeadLine(ColumnFrame, -8.0, rot);

        var near = ToFrame(corrected, FromFrame(ColumnFrame, 0, -8.0, 0));
        var far = ToFrame(corrected, FromFrame(ColumnFrame, 1080.0, -18.0, 0));
        Assert.Equal(0.0, near[0], 6);
        Assert.Equal(0.0, near[1], 6);
        Assert.Equal(0.0, far[1], 6);                       // 먼 점도 새 X축 위
        Assert.Equal(Math.Sqrt(1080.0 * 1080.0 + 100.0), far[0], 6);
        // 면은 그대로 — 면 위의 점은 새 좌표계에서도 z = 0.
        Assert.Equal(0.0, near[2], 6);
        Assert.Equal(0.0, far[2], 6);
    }

    [Fact]
    public void Apply_KeepsPlaneNormal()
    {
        var corrected = InternalWorkpieceFrame.ApplyBeadLine(ColumnFrame, 12.0, 0.6);
        var a = FrameMath.PoseToMatrix(ColumnFrame);
        var b = FrameMath.PoseToMatrix(corrected);
        for (var i = 0; i < 3; i++) Assert.Equal(a[i, 2], b[i, 2], 9);
    }

    [Fact]
    public void Undo_RestoresTheFrame()
    {
        var rot = InternalWorkpieceFrame.BeadLineRotationDeg(4.7, 12.9, 1080.0);
        var corrected = InternalWorkpieceFrame.ApplyBeadLine(ColumnFrame, 4.7, rot);
        var restored = InternalWorkpieceFrame.UndoBeadLine(corrected, 4.7, rot, corrected[..3]);
        var a = FrameMath.PoseToMatrix(ColumnFrame);
        var b = FrameMath.PoseToMatrix(restored);
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 4; j++)
                Assert.Equal(a[i, j], b[i, j], 6);
    }

    [Fact]
    public void Undo_AfterMovingAlongTheLine_ReturnsToTheUncorrectedLine()
    {
        // 같은 줄의 다음 구간으로 360mm 옮긴 뒤 되돌려도, 보정 전 좌표계의 줄(y = 0, 같은 방향) 위로 돌아간다.
        var rot = InternalWorkpieceFrame.BeadLineRotationDeg(-8.0, -18.0, 1080.0);
        var corrected = InternalWorkpieceFrame.ApplyBeadLine(ColumnFrame, -8.0, rot);
        var moved = InternalWorkpieceFrame.ShiftAlongX(corrected, 360.0);
        var restored = InternalWorkpieceFrame.UndoBeadLine(moved, -8.0, rot, corrected[..3]);

        var origin = ToFrame(ColumnFrame, restored);
        Assert.Equal(360.0, origin[0], 6);   // 줄을 따라 간 거리는 그대로
        Assert.Equal(0.0, origin[1], 6);     // 옆 오차는 남지 않는다
        Assert.Equal(0.0, origin[2], 6);
        var a = FrameMath.PoseToMatrix(ColumnFrame);
        var b = FrameMath.PoseToMatrix(restored);
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                Assert.Equal(a[i, j], b[i, j], 6);
    }

    [Fact]
    public void Decide_BothPoints_GivesShiftAndRotation()
    {
        var (fix, _) = SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, -8.0), Point(1080, -18.0) });
        Assert.NotNull(fix);
        Assert.Equal(-8.0, fix!.Value.LateralMm, 9);
        Assert.Equal(Math.Atan2(-10.0, 1080.0) * 180.0 / Math.PI, fix.Value.RotDeg, 9);
    }

    [Fact]
    public void Decide_FarPointUnusable_ShiftOnly()
    {
        foreach (var far in new[] { Point(1080, null), Point(1080, 3.0, coverage: 0.3), Point(1080, 3.0, coverage: null) })
        {
            var (fix, _) = SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, 6.0), far });
            Assert.Equal((6.0, 0.0), fix);
        }
        var (single, _) = SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, 6.0) });
        Assert.Equal((6.0, 0.0), single);
    }

    [Fact]
    public void Decide_RejectsWhenNearPointIsNotTrustworthy()
    {
        Assert.Null(SeamAlongPeakAnchor.DecideBeadCorrection(Array.Empty<SeamAlongPeakAnchor.BeadProbePoint>()).Correction);
        Assert.Null(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, null), Point(1080, 2.0) }).Correction);
        Assert.Null(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, 5.0, coverage: 0.4), Point(1080, 2.0) }).Correction);
        // 원점 자리를 건너뛰고 먼 점만 잰 경우.
        Assert.Null(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(1080, 2.0) }).Correction);
    }

    [Fact]
    public void Decide_RejectsBeyondLimits()
    {
        var overLateral = SeamAlongPeakAnchor.MaxBeadCorrectLateralMm + 0.1;
        Assert.Null(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, overLateral), Point(1080, overLateral) }).Correction);
        Assert.Null(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, -overLateral), Point(1080, 0) }).Correction);
        // 기울기 한계(20mm/m) — 1080mm 에 22mm 넘게 벌어지면 통째로 버린다(옆 이동만 하지 않는다).
        Assert.Null(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, 0.0), Point(1080, 22.0) }).Correction);
        Assert.NotNull(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, 0.0), Point(1080, 21.0) }).Correction);
        // 지금까지 잰 가장 큰 값(시작 +22.5mm, 기울기 −10.8mm/m)은 받아들인다.
        Assert.NotNull(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, 22.5), Point(1080, 18.0) }).Correction);
        Assert.NotNull(SeamAlongPeakAnchor.DecideBeadCorrection(new[] { Point(0, -4.3), Point(1080, -16.0) }).Correction);
    }
}
