using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 연속 TASK 묶음의 점2 교시. 리더만 Bag 에 켜지고, 실패하면 풀고 현행 1피치 단독 교시로 돌아간다.
/// </summary>
public static class BundleTeach
{
    public const string ActiveKey = "bundle.active";
    public const string DissolvedKey = "bundle.dissolved";
    public const string BundleIdKey = "bundle.id";
    public const string FirstStartKey = "bundle.firstStartW";
    public const string FirstEndKey = "bundle.firstEndW";
    public const string LastStartKey = "bundle.lastStartW";
    public const string Point2WdKey = "bundle.point2Wd";

    public static bool IsActive(SequenceContext context)
        => context.Bag.TryGetValue(ActiveKey, out var v) && v is true
           && !(context.Bag.TryGetValue(DissolvedKey, out var d) && d is true);

    /// <summary>아직 묶음이 살아 있으면 풀고 true. 이미 풀렸거나 묶음이 아니면 false.</summary>
    public static bool TryMarkDissolved(SequenceContext context)
    {
        if (!IsActive(context)) return false;
        context.Bag[DissolvedKey] = true;
        return true;
    }

    public static void Activate(SequenceContext context, BundleAssignment assignment)
    {
        context.Bag[ActiveKey] = true;
        context.Bag[DissolvedKey] = false;
        context.Bag[BundleIdKey] = assignment.BundleId ?? "";
        context.Bag[FirstStartKey] = assignment.FirstStartW!;
        context.Bag[FirstEndKey] = assignment.FirstEndW!;
        context.Bag[LastStartKey] = assignment.LastStartW!;
        context.Bag.Remove(Point2WdKey);
    }

    /// <summary>비드1 위치로 돌아가 피치 1회(단독 점2)만 이동한다.</summary>
    public static async Task<StepResult> RepositionToSoloAsync(
        CobotService cobot, ParameterService param, ILogger logger, SequenceContext context, CancellationToken ct)
    {
        if (TaskStartPose(context) is not { } start)
            return StepResult.Fail("단독 교시 복귀 실패 — 비드1 센터링 자세가 없습니다.");

        logger.LogWarning("묶음 점2를 풀고 비드1에서 피치 1회 단독 교시로 돌아갑니다.");
        var rc = await cobot.Rpc.MoveLAsync(start, tool: context.Tool, user: 0,
            vel: context.Velocity, acc: 100, ovl: 100, blendR: -1, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"단독 교시 복귀 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        var pitchDir = await WeldSequenceSupport.GetSignAsync(param, WeldSequenceSupport.PitchDirKey);
        // 방금 비드1에 도착했으므로 다음 피치는 현재 자세에서 1회.
        return await MoveOnePitchAsync(cobot, param, logger, context, pitchDir, fromBead1: false, ct);
    }

    public static async Task<StepResult> RunWithPoint2FallbackAsync(
        int peakId, SequenceContext context, CobotService cobot, ParameterService param, ILogger logger,
        CancellationToken ct, Func<SequenceContext, CancellationToken, Task<StepResult>> run)
    {
        var result = await run(context, ct);
        if (peakId != 2 || result.Success || !TryMarkDissolved(context))
            return result;

        logger.LogWarning("묶음 점2 교시 실패 — 단독 교시로 되돌립니다: {Msg}", result.Message);
        context.Progress?.Invoke($"⚠ 묶음 점2 실패, 단독 교시(시작점·피치 1회)로 진행: {result.Message}");
        var back = await RepositionToSoloAsync(cobot, param, logger, context, ct);
        if (!back.Success)
            return StepResult.Fail($"{result.Message} / {back.Message}");
        return await run(context, ct);
    }

    /// <summary>
    /// 기존 Peak2 이동 1회. <paramref name="fromBead1"/> 이면 비드1 센터링 자세에서 출발(단독·묶음 첫 스텝).
    /// 아니면 현재 TCP 에서 한 피치(묶음 두 번째 이후). 직선으로 목표 좌표에 점프하지 않는다.
    /// </summary>
    public static async Task<StepResult> MoveOnePitchAsync(
        CobotService cobot, ParameterService param, ILogger logger, SequenceContext context,
        int pitchDir, bool fromBead1, CancellationToken ct)
    {
        var pitchMm = await WeldSequenceSupport.GetPitchMmAsync(param, context);
        if (pitchMm <= 0)
            return StepResult.Fail($"'{WeldSequenceSupport.PitchMmKey}' 파라미터가 0 이하입니다.");

        var moveMm = pitchMm * pitchDir;
        var (progressAxis, _) = await WeldSequenceSupport.ResolveProgressAxisAsync(context, cobot, param, ct);
        var (toolAxis, axisFromParam) = await WeldSequenceSupport.GetProgressToolAxisAsync(param, progressAxis);
        if (!axisFromParam)
            logger.LogWarning(
                "'{Key}' 매핑 파라미터 없음/범위 밖 — 기본값 사용. 카메라 페이지에서 매핑을 설정·저장하세요.",
                WeldSequenceSupport.ProgressToolAxisKey(progressAxis));

        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, toolAxis, moveMm);

        double[] anchor;
        var unshiftNote = "";
        if (fromBead1
            && context.Bag.TryGetValue(WeldSequenceSupport.Bead1CenteredPoseBagKey, out var ap)
            && ap is double[] anchorPose && anchorPose.Length == 6)
        {
            anchor = anchorPose;
            context.Bag.Remove(WeldSequenceSupport.InspectShiftedBagKey);
            unshiftNote = ", 앵커 기준(교시 이동 원복)";
        }
        else
        {
            anchor = await cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            if (context.Bag.TryGetValue(WeldSequenceSupport.InspectShiftedBagKey, out var shifted) && shifted is true)
            {
                var (offX, offY) = await WeldSequenceSupport.GetInspectCamOffsetAsync(param);
                var (axX, _) = await WeldSequenceSupport.GetImageXAxisAsync(param);
                var (axY, _) = await WeldSequenceSupport.GetImageYAxisAsync(param);
                FlatSurfaceCenteringService.ApplyAxis(offset, axX, offX);
                FlatSurfaceCenteringService.ApplyAxis(offset, axY, offY);
                context.Bag.Remove(WeldSequenceSupport.InspectShiftedBagKey);
                unshiftNote = $", 검사캠 시프트 역보정(영상 {offX:+0.0;-0.0}/{offY:+0.0;-0.0}mm)";
            }
        }

        int rc;
        var wallNote = "";
        if (context.Bag.TryGetValue(WeldSequenceSupport.WallOutNormalBagKey, out var nv) && nv is double[] { Length: 3 } wallOut
            && WeldSequenceSupport.ToolOffsetParallelToWall(anchor, offset, wallOut) is { } baseMove)
        {
            logger.LogInformation(
                "피치 이동: 면 평행 BASE 오프셋 [{Dx:+0.0;-0.0}, {Dy:+0.0;-0.0}, {Dz:+0.0;-0.0}]mm",
                baseMove[0], baseMove[1], baseMove[2]);
            rc = await cobot.Rpc.MoveByOffsetAsync(anchor, user: 0,
                new[] { baseMove[0], baseMove[1], baseMove[2], 0.0, 0.0, 0.0 },
                tool: context.Tool, vel: context.Velocity, ct: ct);
            wallNote = ", 면 거리 유지";
        }
        else
        {
            logger.LogWarning("피치 이동: ② 면 법선 없음 — 툴축 그대로 이동");
            rc = await cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
                tool: context.Tool, vel: context.Velocity, ct: ct);
        }
        if (rc != 0)
            return StepResult.Fail($"피치 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(500, ct);
        return StepResult.Ok(
            $"피치 이동 툴{FlatSurfaceCenteringService.AxisName(toolAxis)} {moveMm:+0.0;-0.0}mm " +
            $"(pitch={pitchMm:0.#}mm, dir={pitchDir:+0;-0}, 진행축={progressAxis}{unshiftNote}{wallNote}).");
    }

    private static double[]? TaskStartPose(SequenceContext context)
    {
        if (context.Bag.TryGetValue(WeldSequenceSupport.Bead1CenteredPoseBagKey, out var v) && v is double[] { Length: 6 } pose)
            return (double[])pose.Clone();
        return null;
    }
}
