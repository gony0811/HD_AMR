using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class InspectionAlignmentAnchorTests
{
    [Fact]
    public void RepeatedAlignmentKeepsFirstApproachEvenAfterCameraDistanceMove()
    {
        var context = new SequenceContext();
        InspectionAlignmentAnchor.CaptureIfMissing(context, new[] { 100.0, 200, 400, 0, 0, 0 });
        InspectionAlignmentAnchor.CaptureIfMissing(context, new[] { 100.0, 200, 300, 0, 0, 0 });
        InspectionAlignmentAnchor.CaptureIfMissing(context, new[] { 150.0, 160, 300, 0, 0, 0 });
        InspectionAlignmentAnchor.CaptureIfMissing(context, new[] { 180.0, 110, 300, 0, 0, 0 });

        var anchor = (double[])context.Bag[WeldSequenceSupport.InspectAnchorPoseBagKey];
        var current = new[] { 190.0, 100, 250, 0, 0, 0 };
        var target = FrameMath.FromFrame(
            InspectionAlignmentAnchor.ReturnOffset(anchor, current, ToolAxisDir.PlusZ), current);

        Assert.Equal(100, target[0], 8);
        Assert.Equal(200, target[1], 8);
        Assert.Equal(250, target[2], 8); // WD movement is retained.
    }

    [Fact]
    public void CapturedPoseIsNotChangedByCallerMutation()
    {
        var context = new SequenceContext();
        var pose = new[] { 100.0, 200, 400, 0, 0, 0 };
        InspectionAlignmentAnchor.CaptureIfMissing(context, pose);
        pose[0] = 999;
        Assert.Equal(100, ((double[])context.Bag[WeldSequenceSupport.InspectAnchorPoseBagKey])[0]);
    }

    [Fact]
    public void NewApproachAndNewTaskDoNotReuseOldAnchor()
    {
        var context = new SequenceContext();
        InspectionAlignmentAnchor.CaptureIfMissing(context, new[] { 100.0, 200, 400, 0, 0, 0 });
        InspectionAlignmentAnchor.Clear(context);
        Assert.False(context.Bag.ContainsKey(WeldSequenceSupport.InspectAnchorPoseBagKey));
        InspectionAlignmentAnchor.CaptureIfMissing(context, new[] { 500.0, 600, 700, 0, 0, 0 });
        Assert.Equal(500, ((double[])context.Bag[WeldSequenceSupport.InspectAnchorPoseBagKey])[0]);
        Assert.Empty(new SequenceContext().Bag);
    }

    [Theory]
    [InlineData(ToolAxisDir.PlusX)]
    [InlineData(ToolAxisDir.MinusX)]
    [InlineData(ToolAxisDir.PlusY)]
    [InlineData(ToolAxisDir.MinusY)]
    [InlineData(ToolAxisDir.PlusZ)]
    [InlineData(ToolAxisDir.MinusZ)]
    public void TiltedReturnProjectsFixedAnchorAndPreservesNormalDistanceAndAttitude(ToolAxisDir axis)
    {
        var anchor = new[] { 100.0, 200, 400, 15, -10, 25 };
        var current = new[] { 160.0, 120, 280, 23, -17, 61 };
        var axisIndex = (int)axis / 2;
        var rotation = FrameMath.PoseToMatrix(current);
        var normal = Enumerable.Range(0, 3).Select(i => rotation[i, axisIndex]).ToArray();
        var distance = Enumerable.Range(0, 3).Sum(i => (current[i] - anchor[i]) * normal[i]);

        var offset = InspectionAlignmentAnchor.ReturnOffset(anchor, current, axis);
        var target = FrameMath.FromFrame(offset, current);

        Assert.Equal(0, offset[axisIndex]);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(anchor[i] + distance * normal[i], target[i], 8);
            Assert.Equal(current[i + 3], target[i + 3], 8);
        }
        var secondReturn = InspectionAlignmentAnchor.ReturnOffset(anchor, target, axis);
        for (var i = 0; i < 6; i++) Assert.Equal(0, secondReturn[i], 8);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void SeamPointReturnKeepsOpticalAxisOnSeamAfterTiltCorrection(double tiltDeg)
    {
        // 벽 위 용접선 점 S, ②가 400mm 앞(툴 +Z 가 벽을 향함)에 접근, ④가 툴 Rx 를 tiltDeg 보정, WD 300mm 로 전진.
        var anchor = new[] { 600.0, 0, 500, 5, 80, 10 };
        var seam = FrameMath.FromFrame(new[] { 0.0, 0, 400, 0, 0, 0 }, anchor)[..3];   // A 의 광축 400mm 앞
        var tilted = FrameMath.FromFrame(new[] { 0.0, 0, 0, tiltDeg, 0, 0 }, anchor);
        var current = FrameMath.FromFrame(new[] { 40.0, -25, 100, 0, 0, 0 }, tilted); // 평탄면 탐색 횡이동 + 전진

        var context = new SequenceContext();
        InspectionAlignmentAnchor.CaptureIfMissing(context, anchor);
        InspectionAlignmentAnchor.SetSeamPoint(context, seam);
        var (reference, _) = InspectionAlignmentAnchor.GetReturnReference(context);
        Assert.Same(context.Bag[WeldSequenceSupport.InspectAnchorSeamPointBagKey], reference);

        var target = FrameMath.FromFrame(InspectionAlignmentAnchor.ReturnOffset(reference!, current, ToolAxisDir.PlusZ), current);
        Assert.True(LateralMissMm(target, seam) < 1e-6);                // 광축이 S 를 지난다

        var viaAnchor = FrameMath.FromFrame(InspectionAlignmentAnchor.ReturnOffset(anchor, current, ToolAxisDir.PlusZ), current);
        var expectedMiss = 400 * Math.Sin(tiltDeg * Math.PI / 180);   // A 기준이면 이만큼 비켜 간다
        Assert.Equal(expectedMiss, LateralMissMm(viaAnchor, seam), 6);
    }

    [Fact]
    public void ReturnReferenceFallsBackToAnchorAndClearRemovesBoth()
    {
        var context = new SequenceContext();
        Assert.Null(InspectionAlignmentAnchor.GetReturnReference(context).Point);
        InspectionAlignmentAnchor.CaptureIfMissing(context, new[] { 1.0, 2, 3, 0, 0, 0 });
        Assert.Equal(1.0, InspectionAlignmentAnchor.GetReturnReference(context).Point![0]);
        InspectionAlignmentAnchor.SetSeamPoint(context, new[] { 9.0, 8, 7 });
        Assert.Equal(9.0, InspectionAlignmentAnchor.GetReturnReference(context).Point![0]);
        InspectionAlignmentAnchor.Clear(context);
        Assert.Empty(context.Bag);
    }

    /// <summary>TCP 광축(툴 +Z) 직선에서 점까지의 수직 거리.</summary>
    private static double LateralMissMm(double[] tcp, double[] point)
    {
        var r = FrameMath.PoseToMatrix(tcp);
        var d = Enumerable.Range(0, 3).Select(i => point[i] - tcp[i]).ToArray();
        var along = Enumerable.Range(0, 3).Sum(i => d[i] * r[i, 2]);
        return Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(d[i] - along * r[i, 2], 2)));
    }
}
