using HD.AMR.App.Communication;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.App.Service.Sequence;

/// <summary>같은 TASK의 정렬 재시도 동안 유지할 최초 용접선 접근 기준점.</summary>
internal static class InspectionAlignmentAnchor
{
    public static void Clear(SequenceContext context)
        => context.Bag.Remove(WeldSequenceSupport.InspectAnchorPoseBagKey);

    public static void CaptureIfMissing(SequenceContext context, double[] pose)
    {
        if (!context.Bag.ContainsKey(WeldSequenceSupport.InspectAnchorPoseBagKey))
            context.Bag[WeldSequenceSupport.InspectAnchorPoseBagKey] = (double[])pose.Clone();
    }

    /// <summary>
    /// 최초 접근점을 현재 TCP의 면 평행 평면으로 투영한다.
    /// 광축 성분과 회전은 0으로 두어 WD·정렬 자세를 유지한다.
    /// 목표는 anchor + n * dot(current - anchor, n), n은 보정 후 광축이다.
    /// </summary>
    public static double[] ReturnOffset(double[] anchor, double[] current, ToolAxisDir depthAxis)
    {
        var rel = FrameMath.ToFrame(anchor, current);
        var offset = new[] { rel[0], rel[1], rel[2], 0.0, 0.0, 0.0 };
        offset[(int)depthAxis / 2] = 0.0;
        return offset;
    }
}
