using HD.AMR.App.Communication;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.App.Service.Sequence;

/// <summary>같은 TASK의 정렬 재시도 동안 유지할 최초 용접선 접근 기준점.</summary>
internal static class InspectionAlignmentAnchor
{
    public static void Clear(SequenceContext context)
    {
        context.Bag.Remove(WeldSequenceSupport.InspectAnchorPoseBagKey);
        context.Bag.Remove(WeldSequenceSupport.InspectAnchorSeamPointBagKey);
    }

    public static void CaptureIfMissing(SequenceContext context, double[] pose)
    {
        if (!context.Bag.ContainsKey(WeldSequenceSupport.InspectAnchorPoseBagKey))
            context.Bag[WeldSequenceSupport.InspectAnchorPoseBagKey] = (double[])pose.Clone();
    }

    /// <summary>②가 환산한 용접선 점 S(면 위, BASE [x,y,z])를 복귀 기준으로 저장한다.</summary>
    public static void SetSeamPoint(SequenceContext context, double[]? seamPointBase)
    {
        if (seamPointBase is { Length: >= 3 })
            context.Bag[WeldSequenceSupport.InspectAnchorSeamPointBagKey] =
                new[] { seamPointBase[0], seamPointBase[1], seamPointBase[2] };
        else
            context.Bag.Remove(WeldSequenceSupport.InspectAnchorSeamPointBagKey);
    }

    /// <summary>
    /// ④⁺ 복귀 기준점을 고른다. 용접선 점 S가 있으면 S, 없으면(세미오토 단독 ④) 최초 접근 포즈 A의 위치.
    /// S를 우선하는 이유: A는 면에서 접근 거리(기본 400mm)만큼 떨어진 점이라 평행도 보정으로 광축이 θ 바뀌면
    /// A를 지나는 새 광축 직선이 용접선에서 약 400·tanθ 옆으로 비켜 간다. S를 지나는 직선은 항상 S를 지난다.
    /// </summary>
    public static (double[]? Point, string Source) GetReturnReference(SequenceContext context)
    {
        if (context.Bag.TryGetValue(WeldSequenceSupport.InspectAnchorSeamPointBagKey, out var s)
            && s is double[] { Length: >= 3 } seam)
            return (seam, "용접선 점 S");
        if (context.Bag.TryGetValue(WeldSequenceSupport.InspectAnchorPoseBagKey, out var a)
            && a is double[] { Length: 6 } anchor)
            return (anchor, "최초 접근점 A");
        return (null, "");
    }

    /// <summary>
    /// 기준점 P(위치만 사용)를 지나고 보정 후 광축 n에 평행한 직선 위, 현재 TCP와 같은 면 평행 평면의 점으로
    /// 가는 툴 좌표계 오프셋을 구한다. 목표 = P + n·dot(C − P, n).
    /// 광축 성분과 회전은 0으로 두어 WD·정렬 자세를 유지한다.
    /// </summary>
    public static double[] ReturnOffset(double[] reference, double[] current, ToolAxisDir depthAxis)
    {
        // 위치 성분만 쓰므로 회전은 현재 자세를 붙인다 — 결과 위치 = R_cᵀ·(P − C).
        var refPose = new[] { reference[0], reference[1], reference[2], current[3], current[4], current[5] };
        var rel = FrameMath.ToFrame(refPose, current);
        var offset = new[] { rel[0], rel[1], rel[2], 0.0, 0.0, 0.0 };
        offset[(int)depthAxis / 2] = 0.0;
        return offset;
    }
}
