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
    private readonly ILogger<PeakApproachStep> _logger;
    private readonly LaserDisplacementSensorService _laser;
    private readonly bool _useGas;

    public PeakApproachStep(CobotService cobot, ParameterService param, ILogger<PeakApproachStep> logger,
        LaserDisplacementSensorService laser, Microsoft.Extensions.Options.IOptions<InspectionSensorSettings> sensor)
    {
        _cobot = cobot;
        _param = param;
        _logger = logger;
        _laser = laser;
        _useGas = sensor.Value.UsesGasDetector;
    }

    /// <summary>들어올린 피치 이동의 레이저 측정 — 미연결이면 측정 없음(들어올린 만큼 그대로 내린다).</summary>
    private async Task<(double? Z, string? Note)> MeasureLaserAsync(CancellationToken ct)
    {
        if (!_laser.IsConnected) return (null, "레이저 미연결");
        double? raw = null, rawStill = null;
        try
        {
            raw = await _param.GetDoubleAsync(LaserWorkingDistanceStep.OutlierKey);
            rawStill = await _param.GetDoubleAsync(CobotStillness.WindowKey);
        }
        catch { /* DB 미준비 — 기본값 */ }
        await CobotStillness.WaitAsync(_cobot, CobotStillness.ResolveWindowMs(rawStill, _useGas), _logger, "피치 이동", ct);
        return await LaserWorkingDistanceStep.SamplePitchZAsync(_laser, _logger, ct,
            LaserWorkingDistanceStep.ResolveOutlierMm(raw, _useGas));
    }

    public string Key => "peak2Approach";
    public string DisplayName => "Peak2 이동 (pitch)";
    public int DefaultOrder => 800;

    public StepValidation Validate(SequenceContext context)
        // 순수 모션 단계 — 카메라/검출기 조건은 필요 없다.
        => _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var normalNote = await BundleTeach.PrepareMeasuredWallNormalAsync(_param, _logger, context, _useGas);
        var r = await RunAsync(context, ct);
        return normalNote.Length > 0 && r.Success ? StepResult.Ok(r.Message.TrimEnd('.') + normalNote + ".") : r;
    }

    private async Task<StepResult> RunAsync(SequenceContext context, CancellationToken ct)
    {
        if (BundleTeach.IsActive(context)
            && context.Bag.TryGetValue(BundleTeach.FirstStartKey, out var fs) && fs is double[] { Length: 3 } firstStart
            && context.Bag.TryGetValue(BundleTeach.FirstEndKey, out var fe) && fe is double[] { Length: 3 } firstEnd
            && context.Bag.TryGetValue(BundleTeach.LastStartKey, out var ls) && ls is double[] { Length: 3 } lastStart)
        {
            var pitchMm = await WeldSequenceSupport.GetPitchMmAsync(_param, context);
            var configured = await WeldSequenceSupport.GetSignAsync(_param, WeldSequenceSupport.PitchDirKey);
            var (count, geom) = TaskBundlePlanner.PitchPlan(firstStart, firstEnd, lastStart, pitchMm);
            var sign = configured * geom;
            _logger.LogInformation(
                "[Peak2 이동] 묶음 점2 추정 — 피치 {Pitch:0.#}mm × {N}회, 부호 {Sign:+0;-0} (기하 {Geom:+0;-0})",
                pitchMm, count, sign, geom);
            context.Progress?.Invoke($"묶음 점2로 면을 따라 피치 {count}회 이동");

            StepResult? failed = null;
            var done = 0;
            double[]? lastGood = null;   // 마지막으로 끝낸 피치 자리(내린 뒤 TCP) — 다음 피치가 실패하면 여기를 점2 로 쓴다
            for (var i = 0; i < count; i++)
            {
                var step = await BundleTeach.MoveOnePitchAsync(
                    _cobot, _param, _logger, context, sign, fromBead1: i == 0, ct, MeasureLaserAsync);
                if (!step.Success) { failed = step; break; }
                done++;
                if (i < count - 1)
                    lastGood = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            }
            if (failed is null)
                return StepResult.Ok($"묶음 점2 추정 완료 — 면 위 피치 {count}회(직선 점프 없음).");

            // 먼 자리가 안 되면 단독(피치 1회)까지 내려가지 않고 가장 먼 성공 자리를 점2 로 써서 묶음을 유지한다 —
            // 실기 2026-11-24 M2: 피치 3회째 자리만 레이저가 34.7mm 가깝게 읽혀 묶음이 풀리고 줄마다 다시 교시했다.
            if (BundleTeach.FallbackPitchCount(done) > 0 && lastGood is not null)
            {
                _logger.LogWarning(
                    "묶음 점2 피치 {N}회째 실패 — 묶음을 풀지 않고 {Done}회째 자리(약 {Mm:0}mm)를 점2 로 씁니다: {Msg}",
                    done + 1, done, done * pitchMm, failed.Message);
                context.Progress?.Invoke($"⚠ 묶음 점2 피치 {done + 1}회째 실패 — {done}회째 자리를 점2 로 사용");
                var returnError = await BundleTeach.ReturnToPitchAsync(_cobot, _param, _logger, context, lastGood, ct);
                if (returnError is null)
                {
                    // 오케스트레이터가 묶음을 이 구간 안의 줄로 줄인다 — 점2 너머 줄은 다시 교시.
                    context.Bag[BundleTeach.Point2PitchesKey] = done;
                    return StepResult.Ok(
                        $"묶음 점2 추정 — 피치 {count}회 중 {done}회 자리(약 {done * pitchMm:0}mm)를 점2 로 사용(그 구간 안의 줄만 묶음 유지). 원인: {failed.Message}");
                }
                _logger.LogWarning("묶음 점2 앞 피치 자리 복귀 실패 — 단독 교시로 전환: {Msg}", returnError);
                failed = StepResult.Fail($"{failed.Message} / {returnError}");
            }

            if (!BundleTeach.TryMarkDissolved(context))
                return failed;
            _logger.LogWarning("묶음 점2 피치 추정 실패 — 단독 교시로 전환: {Msg}", failed.Message);
            context.Progress?.Invoke($"⚠ 묶음 점2 피치 실패, 단독 교시로 진행: {failed.Message}");
            var back = await BundleTeach.RepositionToSoloAsync(_cobot, _param, _logger, context, ct);
            return back.Success
                ? StepResult.Ok($"묶음 해제 — 단독 피치 1회. 원인: {failed.Message}")
                : StepResult.Fail($"{failed.Message} / {back.Message}");
        }

        BundleTeach.ResetSoloPoint2(context);
        var pitchDir = await WeldSequenceSupport.GetSignAsync(_param, WeldSequenceSupport.PitchDirKey);
        var one = await BundleTeach.MoveOnePitchAsync(_cobot, _param, _logger, context, pitchDir, fromBead1: true, ct, MeasureLaserAsync);
        return one.Success
            ? StepResult.Ok("Peak2 위치로 " + one.Message)
            : one;
    }
}
