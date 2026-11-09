using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class WObjResidualCorrectionTests
{
    [Fact]
    public void Offset_FollowsCrossAxisAndSign()
    {
        Assert.Equal(new[] { 0.0, 2.5, 0, 0, 0, 0 }, WObjPointStep.CrossCorrectionOffset(ToolAxisDir.PlusY, 2.5));
        Assert.Equal(new[] { 0.0, -2.5, 0, 0, 0, 0 }, WObjPointStep.CrossCorrectionOffset(ToolAxisDir.MinusY, 2.5));
        Assert.Equal(new[] { -1.0, 0, 0, 0, 0, 0 }, WObjPointStep.CrossCorrectionOffset(ToolAxisDir.MinusX, 1.0));
    }

    [Theory]
    [InlineData(30.5)]
    [InlineData(-31)]
    [InlineData(double.NaN)]
    public void Offset_RejectsImplausibleResidual(double residual)
        => Assert.Null(WObjPointStep.CrossCorrectionOffset(ToolAxisDir.PlusY, residual));

    [Fact]
    public void ApplyCorrection_MovesAlongToolAxisInBase_KeepsOrientation()
    {
        // 벽을 보는 기울어진 자세 — 툴 Y 가 BASE 에서 어디를 향하든 그 방향으로 정확히 잔차만큼 옮겨야 한다.
        var pose = new[] { 500.0, -200, 1200, 92, 3, -88 };
        var corrected = WObjPointStep.ApplyCorrection(pose, WObjPointStep.CrossCorrectionOffset(ToolAxisDir.PlusY, 4)!);

        var m = FrameMath.PoseToMatrix(pose);
        for (var i = 0; i < 3; i++)
            Assert.Equal(pose[i] + 4 * m[i, 1], corrected[i], 6);
        for (var i = 3; i < 6; i++)
            Assert.Equal(pose[i], corrected[i], 6);
    }

    [Fact]
    public void CorrectedPoints_RemoveFrameYawFromCenteringResiduals()
    {
        // 비드는 BASE X 축(y=0) 위. 툴 자세 = BASE 정렬이라 cross 툴축 +Y = BASE +Y.
        // 센터링 후 TCP 가 비드에서 e1=+1.5, e2=−1.5 mm 옆에 남았다 → 잰 잔차 d = 비드 − 센터 = −e.
        double e1 = 1.5, e2 = -1.5, pitch = 360;
        var p1 = new[] { 0.0, e1, 0, 0, 0, 0 };
        var p2 = new[] { pitch, e2, 0, 0, 0, 0 };

        var raw = InternalWorkpieceFrame.Compute(p1, p2);
        var fixedFrame = InternalWorkpieceFrame.Compute(
            WObjPointStep.ApplyCorrection(p1, WObjPointStep.CrossCorrectionOffset(ToolAxisDir.PlusY, -e1)!),
            WObjPointStep.ApplyCorrection(p2, WObjPointStep.CrossCorrectionOffset(ToolAxisDir.PlusY, -e2)!));

        // 보정 전: X 축이 (e2−e1)/pitch 만큼 돈다 → V04 시작(3 LINE × 360mm)에서 옆 편차 약 3×(e2−e1) = 9mm.
        double rawYaw = Math.Atan2(FrameMath.PoseToMatrix(raw)[1, 0], FrameMath.PoseToMatrix(raw)[0, 0]);
        Assert.Equal(Math.Atan2(e2 - e1, pitch), rawYaw, 6);
        Assert.InRange(Math.Abs(3 * pitch * Math.Tan(rawYaw)), 8.9, 9.1);

        // 보정 후: X 축이 비드 방향과 일치, 원점도 비드 위.
        var fm = FrameMath.PoseToMatrix(fixedFrame);
        Assert.Equal(0, Math.Atan2(fm[1, 0], fm[0, 0]), 9);
        Assert.Equal(0, fixedFrame[1], 6);
    }
}
