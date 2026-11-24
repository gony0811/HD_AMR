using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑧ Peak2 이동 — <b>⑦⁺가 저장한 앵커(Bead1 센터링 완료·검사캠 시프트 전 포즈)</b> 기준으로
/// pitch(공칭 360mm, 챔퍼 경사 방향은 254.6mm — <see cref="WeldSequenceSupport.ResolvePitchMm"/>)만큼 진행축(② 검사방향에서 자동 결정 — 영상 가로면 ImageX, 세로면 ImageY
/// 툴축 매핑, ⑥⑩과 동일)으로 개루프 이동해 인접 Peak 근처로 옮긴다. 앵커 기준 이동이므로
/// 검사캠 시프트·작업물 좌표계 교시용 이동 등 ⑦⁺ 이후 변위가 자동 원복된다
/// (앵커 없으면 현재 포즈 기준 + 시프트 플래그 역보정 폴백).
/// 과거 BASE X 하드코딩은 헤드 방향에 따라 광축 성분이 섞이는 좌표계 오류였음.
/// 이동 방향은 <see cref="WeldSequenceSupport.PitchDirKey"/> 로 결정한다:
/// "Peak2 가 진행축 + 방향이면 +1, 반대면 −1"(설비 배치의 선택)로,
/// 카메라 장착 방향(영상→툴축 매핑)과는 독립된 미지수다.
/// </summary>
public class PeakApproachStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly CameraService _camera;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly ILogger<PeakApproachStep> _logger;

    public PeakApproachStep(CobotService cobot, ParameterService param, CameraService camera,
        FlatSurfaceCenteringService centering, ILogger<PeakApproachStep> logger)
    {
        _cobot = cobot;
        _param = param;
        _camera = camera;
        _centering = centering;
        _logger = logger;
    }

    // ── 피치 중 면 거리 감시 + 마지막에 한 번 맞춤 ─────────────────────
    // 피치 이동은 ② 면 법선(도면 기준)에 평행하게 움직인다. 실제 벽이 그 평면과 비스듬하면 피치를 거듭할수록
    // 벽과의 거리가 변한다 — 다가가는 쪽(실기 2026-11-20 21:04 F L2-R01C06: 피치 2회에 306→264mm, 최소 234mm)과
    // 멀어지는 쪽(2026-11-22 10:03 F L4-R01C04: 피치마다 342·355·361mm, 약 9°) 모두 있다.
    // 종전(ef2df24)엔 피치마다 300mm 로 맞춰, 멀어지는 벽에서 '다가감 → 다음 피치에 다시 멀어짐'을 되풀이했다.
    // 이제 피치 중에는 재기만 하고 GuardMinMm 보다 가까울 때만 물러나며(충돌 방지 — 1440mm·10° 면 250mm 붙는다),
    // 마지막 피치 뒤 점2 자리에서 한 번만 목표 거리(SequenceContext.CameraTargetDistanceMm, 현장 300mm)로 맞춘다.

    /// <summary>피치 중 이보다 가까우면 목표 거리로 물러난다 [mm].</summary>
    internal const double GuardMinMm = 200.0;

    /// <summary>목표에서 이 이내면 움직이지 않는다 [mm].</summary>
    internal const double DepthKeepToleranceMm = 10.0;

    /// <summary>한 번에 물러나는 최대 거리 [mm] — 넘게 가까우면 측정이나 위치가 이상한 것으로 보고 멈춘다.</summary>
    internal const double DepthKeepMaxRetreatMm = 200.0;

    /// <summary>마지막 맞춤 1회 다가감 한계 [mm] — 피치 4회(1440mm)·10° 에서 벌어지는 250mm 를 덮는다.
    /// 멀게 잘못 재면 그만큼 벽 쪽으로 가므로 이 이상은 자르고, 남은 차이는 다음 회차나 점2 평탄면·WD 단계가 맞춘다.</summary>
    internal const double FinalMaxApproachMm = 250.0;

    /// <summary>마지막 맞춤 최대 이동 횟수.</summary>
    internal const int FinalMaxMoves = 3;

    /// <summary>
    /// 피치 중 감시 — 양수 = 면 쪽으로 다가감. <see cref="GuardMinMm"/> 이상이면 0(움직이지 않음),
    /// 가까우면 목표 거리로 물러나는 음수. 물러날 거리가 <see cref="DepthKeepMaxRetreatMm"/> 를 넘으면 움직이지 않고 사유를 돌려준다.
    /// </summary>
    internal static (double MoveMm, string? Blocker) PlanPitchGuard(double measuredMm, double targetMm)
        => measuredMm >= GuardMinMm ? (0, null) : PlanDepthKeep(measuredMm, targetMm, maxApproachMm: 0);

    /// <summary>
    /// 목표 거리 맞춤 이동량 — 양수 = 면 쪽으로 다가감(③ 과 같은 부호). 목표에서 <see cref="DepthKeepToleranceMm"/> 이내면 0.
    /// 다가가는 쪽은 <paramref name="maxApproachMm"/> 로 자르고, 물러날 거리가 <see cref="DepthKeepMaxRetreatMm"/> 를 넘으면
    /// 움직이지 않고 사유를 돌려준다.
    /// </summary>
    internal static (double MoveMm, string? Blocker) PlanDepthKeep(double measuredMm, double targetMm,
        double maxApproachMm = FinalMaxApproachMm)
    {
        var delta = measuredMm - targetMm;
        if (Math.Abs(delta) < DepthKeepToleranceMm) return (0, null);
        if (delta < -DepthKeepMaxRetreatMm)
            return (0, $"면까지 {measuredMm:0}mm — 목표 {targetMm:0}mm 보다 {-delta:0}mm 가까워 한 번에 물러날 한계 " +
                       $"{DepthKeepMaxRetreatMm:0}mm 를 넘습니다. 측정 영역에 다른 물체가 있거나 위치가 크게 어긋났습니다.");
        return (Math.Min(delta, maxApproachMm), null);
    }

    /// <summary>깊이 측정 준비(카메라·광축·ROI). 쓸 수 없으면 null(경고 후 생략 — 종전 동작).</summary>
    private async Task<(DepthDistanceMoveOptions Opts, ToolAxisDir Axis)?> PrepareDepthAsync(SequenceContext context, string label)
    {
        if (!_camera.IsConnected || _camera.LatestDepth is null)
        {
            _logger.LogWarning("[Peak2 이동] {Label}: 깊이 카메라 없음 — 면 거리 확인 생략", label);
            return null;
        }
        var (axis, fromParam) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        if (!fromParam)
        {
            _logger.LogWarning("[Peak2 이동] {Label}: 광축 파라미터 없음 — 면 거리 확인 생략", label);
            return null;
        }
        var (rx, ry, rw, rh, _) = await CameraAlignStep.GetDepthRoiAsync(_param);
        return (new DepthDistanceMoveOptions
        {
            RoiX = rx, RoiY = ry, RoiW = rw, RoiH = rh,
            TargetDistanceMm = context.CameraTargetDistanceMm, Samples = 5, DepthAxis = axis,
            Tool = context.Tool, Velocity = context.Velocity,
        }, axis);
    }

    /// <summary>이동 뒤 새 깊이 프레임을 기다려 ROI 최소 깊이를 잰다 — 이동 전 프레임으로 재면 의미가 없다.</summary>
    private async Task<double?> MeasureAfterMoveAsync(DepthDistanceMoveOptions opts, DateTime movedAt, CancellationToken ct)
    {
        await Task.Delay(300, ct);
        for (var waited = 0; _camera.LastFrameAt <= movedAt && waited < 1500; waited += 50)
            await Task.Delay(50, ct);
        return await _centering.MeasureRoiDistanceAsync(opts, ct);
    }

    /// <summary>광축 방향 이동(양수 = 면 쪽). 실패하면 실패 결과.</summary>
    private async Task<StepResult?> MoveAlongAxisAsync(SequenceContext context, ToolAxisDir axis, double move,
        string label, CancellationToken ct)
    {
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, axis, move);
        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset, tool: context.Tool, vel: context.Velocity, ct: ct);
        return rc != 0
            ? StepResult.Fail($"피치 이동 중 면 거리 맞춤 실패 ({label}, rc={rc}{FairinoErrorCodes.Suffix(rc)}).")
            : null;
    }

    /// <summary>피치 한 번 뒤 감시 — 재기만 하고 <see cref="GuardMinMm"/> 보다 가까울 때만 물러난다.</summary>
    private async Task<StepResult?> GuardPitchDistanceAsync(SequenceContext context, string label, DateTime movedAt,
        CancellationToken ct)
    {
        if (await PrepareDepthAsync(context, label) is not { } prep) return null;
        if (await MeasureAfterMoveAsync(prep.Opts, movedAt, ct) is not { } measured)
        {
            _logger.LogWarning("[Peak2 이동] {Label}: 유효한 깊이 측정값 없음 — 면 거리 확인 생략", label);
            return null;
        }
        var target = context.CameraTargetDistanceMm;
        var (move, blocker) = PlanPitchGuard(measured, target);
        if (blocker is not null)
        {
            _logger.LogWarning("[Peak2 이동] {Label}: {Msg} — 움직이지 않고 정지", label, blocker);
            return StepResult.Fail($"피치 이동 중 면 거리 이상 ({label}): {blocker}");
        }
        if (move == 0)
        {
            _logger.LogInformation("[Peak2 이동] {Label}: 면 거리 {D:0}mm (안전 {Min:0}mm 이상 — 그대로, 마지막에 맞춤)",
                label, measured, GuardMinMm);
            return null;
        }
        if (await MoveAlongAxisAsync(context, prep.Axis, move, label, ct) is { } fail) return fail;
        _logger.LogWarning("[Peak2 이동] {Label}: 면 거리 {D:0}mm < 안전 {Min:0}mm — 목표 {T:0}mm 로 {M:0}mm 물러남",
            label, measured, GuardMinMm, target, -move);
        return null;
    }

    /// <summary>마지막 피치 뒤 점2 자리에서 목표 거리로 한 번 맞춘다(최대 <see cref="FinalMaxMoves"/>회 이동). 못 맞추면 경고만 하고
    /// 넘어간다(점2 평탄면·WD 단계가 다시 맞춘다). 물러날 한계를 넘게 가까우면 실패.</summary>
    private async Task<StepResult?> AlignFinalDistanceAsync(SequenceContext context, DateTime movedAt, CancellationToken ct)
    {
        const string label = "점2 거리 맞춤";
        if (await PrepareDepthAsync(context, label) is not { } prep) return null;
        var target = context.CameraTargetDistanceMm;
        double? first = null;
        for (var i = 0; ; i++)
        {
            if (await MeasureAfterMoveAsync(prep.Opts, movedAt, ct) is not { } measured)
            {
                _logger.LogWarning("[Peak2 이동] {Label}: 유효한 깊이 측정값 없음 — 생략", label);
                return null;
            }
            first ??= measured;
            var (move, blocker) = PlanDepthKeep(measured, target);
            if (blocker is not null)
            {
                _logger.LogWarning("[Peak2 이동] {Label}: {Msg} — 움직이지 않고 정지", label, blocker);
                return StepResult.Fail($"피치 이동 중 면 거리 이상 ({label}): {blocker}");
            }
            if (move == 0)
            {
                _logger.LogInformation("[Peak2 이동] {Label}: {F:0}→{D:0}mm (목표 {T:0}±{Tol:0}mm, 이동 {N}회)",
                    label, first, measured, target, DepthKeepToleranceMm, i);
                return null;
            }
            if (i >= FinalMaxMoves)
            {
                _logger.LogWarning("[Peak2 이동] {Label}: {N}회 이동 후 {D:0}mm (목표 {T:0}mm) — 그대로 진행, 점2 단계에서 다시 맞춤",
                    label, FinalMaxMoves, measured, target);
                return null;
            }
            if (await MoveAlongAxisAsync(context, prep.Axis, move, label, ct) is { } fail) return fail;
            movedAt = DateTime.UtcNow;
            _logger.LogInformation("[Peak2 이동] {Label}: 면 거리 {D:0}mm → 목표 {T:0}mm, 광축 {Dir} {Move:0}mm{Cap}",
                label, measured, target, move > 0 ? "다가감" : "물러남", Math.Abs(move),
                measured - target > FinalMaxApproachMm ? $" (다가감 한계 {FinalMaxApproachMm:0}mm 로 자름)" : "");
        }
    }

    public string Key => "peak2Approach";
    public string DisplayName => "Peak2 이동 (pitch)";
    public int DefaultOrder => 800;

    public StepValidation Validate(SequenceContext context)
        // 순수 모션 단계 — 카메라/검출기 조건은 필요 없다.
        => _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (BundleTeach.IsActive(context)
            && context.Bag.TryGetValue(BundleTeach.FirstStartKey, out var fs) && fs is double[] { Length: 3 } firstStart
            && context.Bag.TryGetValue(BundleTeach.FirstEndKey, out var fe) && fe is double[] { Length: 3 } firstEnd
            && context.Bag.TryGetValue(BundleTeach.Point2Key, out var p2) && p2 is double[] { Length: 3 } point2)
        {
            var pitchMm = await WeldSequenceSupport.GetPitchMmAsync(_param, context);
            var configured = await WeldSequenceSupport.GetSignAsync(_param, WeldSequenceSupport.PitchDirKey);
            var (count, geom) = TaskBundlePlanner.PitchPlan(firstStart, firstEnd, point2, pitchMm);
            var sign = configured * geom;
            _logger.LogInformation(
                "[Peak2 이동] 묶음 점2 추정 — 피치 {Pitch:0.#}mm × {N}회, 부호 {Sign:+0;-0} (기하 {Geom:+0;-0})",
                pitchMm, count, sign, geom);
            context.Progress?.Invoke($"묶음 점2로 면을 따라 피치 {count}회 이동");

            StepResult? failed = null;
            for (var i = 0; i < count; i++)
            {
                var step = await BundleTeach.MoveOnePitchAsync(
                    _cobot, _param, _logger, context, sign, fromBead1: i == 0, ct);
                if (!step.Success) { failed = step; break; }
                // 마지막 피치는 감시 대신 아래 한 번 맞춤으로 처리한다.
                if (i < count - 1
                    && await GuardPitchDistanceAsync(context, $"피치 {i + 1}/{count}", DateTime.UtcNow, ct) is { } guard)
                { failed = guard; break; }
            }
            if (failed is null && await AlignFinalDistanceAsync(context, DateTime.UtcNow, ct) is { } align)
                failed = align;
            if (failed is null)
                return StepResult.Ok($"묶음 점2 추정 완료 — 면 위 피치 {count}회(직선 점프 없음).");

            if (!BundleTeach.TryMarkDissolved(context))
                return failed;
            _logger.LogWarning("묶음 점2 피치 추정 실패 — 단독 교시로 전환: {Msg}", failed.Message);
            context.Progress?.Invoke($"⚠ 묶음 점2 피치 실패, 단독 교시로 진행: {failed.Message}");
            var back = await BundleTeach.RepositionToSoloAsync(_cobot, _param, _logger, context, ct);
            return back.Success
                ? StepResult.Ok($"묶음 해제 — 단독 피치 1회. 원인: {failed.Message}")
                : StepResult.Fail($"{failed.Message} / {back.Message}");
        }

        var pitchDir = await WeldSequenceSupport.GetSignAsync(_param, WeldSequenceSupport.PitchDirKey);
        var one = await BundleTeach.MoveOnePitchAsync(_cobot, _param, _logger, context, pitchDir, fromBead1: true, ct);
        if (!one.Success) return one;
        if (await AlignFinalDistanceAsync(context, DateTime.UtcNow, ct) is { } failedKeep) return failedKeep;
        return StepResult.Ok("Peak2 위치로 " + one.Message);
    }
}
