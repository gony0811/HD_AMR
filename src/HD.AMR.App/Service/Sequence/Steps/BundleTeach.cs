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

    /// <summary>단독 교시에서도 점2 평탄면 탐색·WD 조정(⑧⁺)을 할지 파라미터 키(bool, 기본 true). false = 묶음 점2 에서만.
    /// 피치 이동이 모델 면 법선 기준이라 점2 면 거리·틸트가 점1 과 달라질 수 있어(실기 2026-11-21 M2 ±10mm) 점2 에서 다시 맞춘다.</summary>
    public const string SoloPoint2FlatWdKey = "Sequence.Point2.SoloFlatWd";

    /// <summary>점2 평탄면 탐색·WD 조정을 이번 실행에서 할지 — 묶음 점2 이거나, 단독이면 <see cref="SoloPoint2FlatWdKey"/>(기본 true).</summary>
    public static async Task<bool> RunPoint2FlatWdAsync(ParameterService param, SequenceContext context)
    {
        if (IsActive(context)) return true;
        try { return await param.GetBoolAsync(SoloPoint2FlatWdKey) ?? true; }
        catch { return true; }
    }

    /// <summary>단독 피치 이동 시작 — 이전 실행의 점2 자리·WD 표식을 지운다(시퀀스 화면은 컨텍스트를 재사용한다).</summary>
    public static void ResetSoloPoint2(SequenceContext context)
    {
        if (IsActive(context)) return;
        context.Bag.Remove(Point2WdKey);
        context.Bag.Remove(Point2ReturnPoseKey);
    }

    /// <summary>⑧ 피치 이동 들어올림 거리 [mm] 파라미터 키 — 면 바깥(모델 면 법선)으로 이만큼 띄운 채 피치를 이동하고,
    /// 도착 뒤 레이저로 점1 과 같은 면 거리까지 내린다. 0 = 종전(면 평행 직선 이동).</summary>
    public const string PitchLiftKey = "Sequence.Pitch.LiftMm";
    /// <summary>들어올림 기본값 [mm] — 실기 면 거리 변화 ±10mm(모델 면과 실제 면 1.6° 차, 2026-11-21 M2)의 3배 여유.</summary>
    public const double DefaultPitchLiftMm = 30.0;
    /// <summary>들어올린 채 잰 점1→점2 레이저 거리 변화가 이 값을 넘으면 측정 이상으로 보고 내리지 않고 정지 [mm].</summary>
    public const double MaxPitchDriftMm = 40.0;

    /// <summary>
    /// 들어올린 피치 이동 뒤 내릴 거리 [mm] — 점1·점2 에서 들어올린 채 잰 레이저 거리(z1·z2)로 점1 과 같은 면 거리를 만든다:
    /// 내림 = lift + (z2 − z1). 레이저 값이 없으면 lift 그대로. 변화가 <see cref="MaxPitchDriftMm"/> 를 넘거나
    /// 내림이 음수(점2 가 들어올린 높이보다도 가깝다)면 null + 사유 — 움직이지 않는다.
    /// </summary>
    internal static (double? Down, string Note) PlanPitchLower(double liftMm, double? z1, double? z2)
    {
        if (z1 is not { } a || z2 is not { } b)
            return (liftMm, "레이저 측정 없음 — 들어올린 만큼 그대로 내림");
        var drift = b - a;
        if (Math.Abs(drift) > MaxPitchDriftMm)
            return (null, $"점1→점2 레이저 거리 변화 {drift:+0.0;-0.0}mm 가 한계 ±{MaxPitchDriftMm:0}mm 초과 — 측정 이상");
        var down = liftMm + drift;
        if (down < 0)
            return (null, $"점2 가 들어올린 높이보다 {-down:0.0}mm 더 가깝다(변화 {drift:+0.0;-0.0}mm) — 들어올림 여유 부족");
        return (down, $"레이저 거리 변화 {drift:+0.0;-0.0}mm 반영 — {down:0.0}mm 내림");
    }

    /// <summary>
    /// 묶음 점2 평탄면 탐색을 시작한 자리의 TCP 포즈(double[6], BASE) — 점2 WD 조정 뒤 돌아갈 자리.
    /// 점2 WD 가 ④⁺ 와 같은 기준(② 용접선 시작점 S)으로 복귀하면 점1 쪽으로 수백 mm 를 되돌아가려다 한계(±300mm)에
    /// 걸려 묶음이 항상 풀린다(실기 2026-11-12 T L4-R10C09: 횡복귀량 X+793mm).
    /// </summary>
    public const string Point2ReturnPoseKey = "bundle.point2ReturnPose";

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
        context.Bag.Remove(Point2ReturnPoseKey);
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
        int pitchDir, bool fromBead1, CancellationToken ct,
        Func<CancellationToken, Task<(double? Z, string? Note)>>? measureLaser = null)
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

        // 방향은 용접선이 정한다 — 툴 진행축이 끝점 반대쪽을 향하게 정렬됐으면 이동을 뒤집는다.
        var seamNote = "";
        if (context.Bag.TryGetValue(WeldSequenceSupport.SeamDirBaseBagKey, out var sd) && sd is double[] { Length: 3 } seamDir
            && WeldSequenceSupport.PitchMoveOpposesSeam(
                WeldSequenceSupport.ToolOffsetInBase(anchor, PitchOnly(toolAxis, moveMm)), seamDir, pitchDir))
        {
            // 진행축 성분(moveMm)만 뒤집는다 — 검사캠 시프트 역보정 성분은 방향과 무관하다.
            var flip = new double[6];
            FlatSurfaceCenteringService.ApplyAxis(flip, toolAxis, -2 * moveMm);
            for (var i = 0; i < 3; i++) offset[i] += flip[i];
            moveMm = -moveMm;
            seamNote = ", 용접선 방향에 맞춰 뒤집음";
            logger.LogInformation(
                "피치 이동: 툴 진행축이 용접선 끝점 반대쪽 — 이동을 뒤집습니다 (용접선 BASE 방향 [{X:+0.00;-0.00}, {Y:+0.00;-0.00}, {Z:+0.00;-0.00}])",
                seamDir[0], seamDir[1], seamDir[2]);
        }

        int rc;
        var wallNote = "";
        if (context.Bag.TryGetValue(WeldSequenceSupport.WallOutNormalBagKey, out var nv) && nv is double[] { Length: 3 } wallOut
            && WeldSequenceSupport.ToolOffsetParallelToWall(anchor, offset, wallOut) is { } baseMove)
        {
            logger.LogInformation(
                "피치 이동: 면 평행 BASE 오프셋 [{Dx:+0.0;-0.0}, {Dy:+0.0;-0.0}, {Dz:+0.0;-0.0}]mm",
                baseMove[0], baseMove[1], baseMove[2]);
            var liftMm = await GetPitchLiftMmAsync(param);
            if (liftMm > 0)
            {
                // 모델 면 법선은 AMR 정차 자세에서 계산한 값이라 실제 면과 1~2° 어긋날 수 있다 — 360mm 를 면 평행으로
                // 가면 ±10mm 가까워져 검사 팁이 코로게이션에 닿았다(실기 2026-11-21 M2). 띄운 채 이동하고 레이저로 내린다.
                var lifted = await MoveLiftedPitchAsync(cobot, logger, context, anchor, baseMove, wallOut, liftMm, measureLaser, ct);
                if (lifted.Error is not null) return StepResult.Fail(lifted.Error);
                rc = 0;
                wallNote = $", {liftMm:0}mm 들어올려 이동 — {lifted.Note}";
            }
            else
            {
                rc = await cobot.Rpc.MoveByOffsetAsync(anchor, user: 0,
                    new[] { baseMove[0], baseMove[1], baseMove[2], 0.0, 0.0, 0.0 },
                    tool: context.Tool, vel: context.Velocity, ct: ct);
                wallNote = ", 면 거리 유지";
            }
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
            $"(pitch={pitchMm:0.#}mm, dir={pitchDir:+0;-0}, 진행축={progressAxis}{unshiftNote}{wallNote}{seamNote}).");
    }

    /// <summary>
    /// 들어올린 피치 이동 — ① 점1 앵커에서 면 바깥으로 <paramref name="liftMm"/> 상승(레이저 z1) ② 같은 높이로 피치 이동(레이저 z2)
    /// ③ <see cref="PlanPitchLower"/> 만큼 면 쪽으로 내림. 모두 BASE 오프셋 직선 이동, 자세 유지.
    /// </summary>
    private static async Task<(string? Error, string Note)> MoveLiftedPitchAsync(
        CobotService cobot, ILogger logger, SequenceContext context, double[] anchor, double[] baseMove, double[] wallOut,
        double liftMm, Func<CancellationToken, Task<(double? Z, string? Note)>>? measureLaser, CancellationToken ct)
    {
        var nLen = Math.Sqrt(wallOut[0] * wallOut[0] + wallOut[1] * wallOut[1] + wallOut[2] * wallOut[2]);
        var n = new[] { wallOut[0] / nLen, wallOut[1] / nLen, wallOut[2] / nLen };
        double[] Off(double along, double outMm) => new[]
        {
            along * baseMove[0] + outMm * n[0], along * baseMove[1] + outMm * n[1], along * baseMove[2] + outMm * n[2], 0.0, 0.0, 0.0,
        };
        Task<int> Go(double[] off) => cobot.Rpc.MoveByOffsetAsync(anchor, user: 0, off, tool: context.Tool, vel: context.Velocity, ct: ct);

        async Task<double?> Measure(string at)
        {
            if (measureLaser is null) return null;
            await Task.Delay(300, ct);
            var (z, note) = await measureLaser(ct);
            if (z is null) logger.LogWarning("피치 이동: {At} 레이저 측정 실패 — {Note}", at, note);
            return z;
        }

        var rc = await Go(Off(0, liftMm));
        if (rc != 0) return ($"피치 이동 들어올림 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.", "");
        var z1 = await Measure("점1(들어올림)");

        rc = await Go(Off(1, liftMm));
        if (rc != 0) return ($"피치 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.", "");
        var z2 = await Measure("점2(들어올림)");

        var (down, note) = PlanPitchLower(liftMm, z1, z2);
        logger.LogInformation("피치 이동: 들어올림 {Lift:0}mm, 레이저 점1 {Z1} → 점2 {Z2} — {Note}",
            liftMm, z1?.ToString("0.0") ?? "-", z2?.ToString("0.0") ?? "-", note);
        if (down is not { } d)
            return ($"피치 이동 후 내림 중단 — {note}. 들어올린 위치에서 정지합니다.", note);

        rc = await Go(Off(1, liftMm - d));
        if (rc != 0) return ($"피치 이동 후 내림 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.", "");
        return (null, note);
    }

    private static async Task<double> GetPitchLiftMmAsync(ParameterService param)
    {
        try
        {
            var v = await param.GetDoubleAsync(PitchLiftKey);
            return v is { } x && double.IsFinite(x) ? Math.Clamp(x, 0, 100) : DefaultPitchLiftMm;
        }
        catch { return DefaultPitchLiftMm; }
    }

    /// <summary>진행축 성분만 담은 툴 이동량(x,y,z) — 방향 판정에 검사캠 시프트 역보정이 섞이지 않게 한다.</summary>
    private static double[] PitchOnly(ToolAxisDir toolAxis, double moveMm)
    {
        var o = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(o, toolAxis, moveMm);
        return new[] { o[0], o[1], o[2] };
    }

    private static double[]? TaskStartPose(SequenceContext context)
    {
        if (context.Bag.TryGetValue(WeldSequenceSupport.Bead1CenteredPoseBagKey, out var v) && v is double[] { Length: 6 } pose)
            return (double[])pose.Clone();
        return null;
    }
}
