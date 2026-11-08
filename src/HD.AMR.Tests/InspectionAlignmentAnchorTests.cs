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
}
