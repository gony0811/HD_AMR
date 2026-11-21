using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ④⁺ 레이저 WD 거리 조정 — ④ 평탄면 센터링(틸트 보정) 직후, 레이저 3점 평균 거리
/// (<see cref="LaserDisplacementSensorService.GetPlanePose"/>의 Z, mm)가 파라미터
/// <see cref="WorkingDistanceKey"/> 값과 일치하도록 광축(<see cref="WeldSequenceSupport.DepthAxisKey"/>,
/// 기본 툴 +Z)으로 접근/후퇴한다. ④는 틸트만 보정하고 거리는 조정하지 않으므로 여기서 채운다.
///
/// 기본은 <b>개루프 1회 이동 + 재측정 검증</b>. 이동이 측정에 반영되지 않는 상황(광축 매핑 수직, 센서 미갱신,
/// 부호 반대)에서 잔차만큼 반복 이동하면 효과 없는 방향으로 누적 이동하는 위험이 있으므로, 그런 응답이면
/// 원인별 안내와 함께 즉시 실패시킨다. 측정이 기대 방향으로 정상 반응했고 남은 잔차가
/// <see cref="MaxCorrectionMm"/> 이내일 때만 잔차만큼 <see cref="MaxCorrections"/> 회까지 보정 이동한다 —
/// 이동 전 측정이 기구물 흔들림으로 조금 어긋나 1~2mm 모자라던 오실패(2026-11-16 L3-R02C15 H04:
/// 이동 26.38mm, 측정변화 25.06mm, 잔차 +1.31mm)를 막는다.
/// 1회 이동이 <see cref="MaxTravelMm"/> 를 넘으면 측정/설정 이상으로 보고 이동하지 않고 실패(클램프 아님).
///
/// 측정은 고정 딜레이가 아니라 <b>안정화 대기</b>(<see cref="SampleStableZAsync"/>)로 수행한다 —
/// MoveL rc=0 반환 후에도 모션이 끝나지 않았거나 센서 내부 평균화 필터가 수렴 중이면 고정 딜레이 판정은
/// 과도 상태 값을 읽는다(실기에서 이동 39mm 중 75%만 반영된 시점에 판정해 오실패한 사례).
///
/// WD 판정 성공 후에는 ②가 저장한 용접선 점 S(<see cref="WeldSequenceSupport.InspectAnchorSeamPointBagKey"/>,
/// 없으면 최초 접근 앵커 <see cref="WeldSequenceSupport.InspectAnchorPoseBagKey"/>)를 현재 면 평행 평면으로 투영해
/// 복귀한다 — 평행도 보정 후에도 광축이 용접선을 지난다. ④ 재시도 횟수와 무관하게 동일한 기준점을 쓴다.
/// 설정된 광축 성분(기본 툴 Z)·회전(평행도)은 건드리지 않는다.
/// </summary>
public class LaserWorkingDistanceStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ParameterService _param;
    private readonly ILogger<LaserWorkingDistanceStep> _logger;

    /// <summary>목표 Working Distance(mm) 공통 파라미터 키. 없으면 <see cref="DefaultWorkingDistanceMm"/>.
    /// 면별 값은 뒤에 <c>.{wall_code}</c> 를 붙인 키로 따로 둘 수 있다(<see cref="LaserWorkingDistanceSetting"/>).</summary>
    public const string WorkingDistanceKey = "Sequence.Laser.WorkingDistanceMm";

    /// <summary>파라미터 미설정 시 레이저 WD [mm]. 리치 맵 정렬 준비 거리(고정 300)와는 별개 값이다.</summary>
    public const double DefaultWorkingDistanceMm = 300.0;

    /// <summary>수렴 판정 허용오차(mm).</summary>
    private const double ToleranceMm = 1.0;

    /// <summary>1회 이동 절대 한계(mm). 초과 시 이동하지 않고 실패 — 센서 범위/설정 이상 가드.</summary>
    private const double MaxTravelMm = 200.0;

    /// <summary>이동 전후 측정 변화가 이 값 미만이면 "측정 불변"으로 진단한다(mm).</summary>
    private const double NoResponseMm = 0.5;

    /// <summary>보정 이동을 허용하는 최대 잔차(mm). 이보다 크면 보정하지 않고 실패.</summary>
    internal const double MaxCorrectionMm = 5.0;

    /// <summary>첫 이동 뒤 보정 이동 최대 횟수.</summary>
    internal const int MaxCorrections = 2;

    /// <summary>보정 이동을 허용하는 측정 응답 비율 범위(|측정변화| / |이동량|). 정상 반응일 때만 보정한다.</summary>
    private const double MinResponseRatio = 0.5, MaxResponseRatio = 1.5;

    /// <summary>안정화 폴링 간격(ms).</summary>
    private const int PollMs = 150;

    /// <summary>안정 판정 윈도우 — 최근 연속 유효 샘플 수.</summary>
    private const int StableWindow = 3;

    /// <summary>안정 판정 폭(mm) — 윈도우 내 최대−최소가 이 값 미만이면 안정.</summary>
    private const double StableBandMm = 0.3;

    /// <summary>안정화 대기 한도(ms). 초과 시 측정 실패 처리.</summary>
    private const int StabilizeTimeoutMs = 8000;

    /// <summary>횡복귀 성분별 절대 한계 파라미터 키 [mm]. 초과 시 이동하지 않고 실패 — 앵커/자세 이상 가드.</summary>
    public const string MaxReturnKey = "Sequence.Laser.MaxReturnMm";

    /// <summary>횡복귀 한계 기본값 [mm] — 300 → 400(2026-11-21 T L4-R09C26 H1: 평탄면 자리 재시도 2회 + 재탐색으로
    /// 용접선에서 306.7mm 떨어져 복귀 거부).</summary>
    internal const double DefaultMaxReturnMm = 400.0;

    /// <summary>허용 범위 [mm] — 밖이거나 비면 기본값.</summary>
    internal const double MinMaxReturnMm = 50.0, MaxMaxReturnMm = 1000.0;

    /// <summary>파라미터 값 → 쓸 한계 [mm]. 비거나 범위 밖이면 기본값.</summary>
    internal static double ResolveMaxReturnMm(double? value)
        => value is { } v && double.IsFinite(v) && v >= MinMaxReturnMm && v <= MaxMaxReturnMm ? v : DefaultMaxReturnMm;

    /// <summary>이 값 미만의 횡복귀 성분은 무시한다(mm).</summary>
    private const double SkipReturnMm = 0.5;

    /// <summary>
    /// 레이저 분실 재탐색 최대 거리 파라미터 키 [mm] — 첫 측정에서 3채널이 범위 밖이면 광축 방향(벽 쪽)으로
    /// <see cref="LostSearchStepMm"/> 씩 다가가며 다시 잰다. 0 = 끔. 비우면 가스 <see cref="DefaultLostSearchMm"/> / 비전 끔.
    /// (2026-11-21 M2 S013-1~4: 점2 로 옆 360mm 피치 뒤 벽이 측정 범위(~450mm) 밖이라 점2 WD 가 계속 실패.)
    /// </summary>
    public const string LostSearchKey = "Sequence.Laser.LostSearchMm";
    internal const double DefaultLostSearchMm = 60.0;
    internal const double MaxLostSearchMm = 150.0;

    /// <summary>재탐색 한 번에 다가가는 거리 [mm].</summary>
    internal const double LostSearchStepMm = 10.0;

    /// <summary>재탐색 이동 뒤 유효 측정을 기다리는 시간 [ms] — 안정화가 아니라 "잡히는지"만 본다.</summary>
    private const int LostSearchProbeMs = 1500;

    /// <summary>파라미터 값 → 재탐색 최대 거리 [mm]. 비거나 범위 밖이면 센서 모드 기본값(가스 60 / 비전 0).</summary>
    internal static double ResolveLostSearchMm(double? value, bool usesGas)
        => value is { } v && double.IsFinite(v) && v >= 0 && v <= MaxLostSearchMm ? v
         : usesGas ? DefaultLostSearchMm : 0;

    /// <summary>최대 거리 → 다가갈 단계별 누적 거리 [mm] (마지막 단계는 최대 거리에 맞춘다).</summary>
    internal static IReadOnlyList<double> LostSearchSteps(double maxMm)
    {
        var steps = new List<double>();
        if (!(maxMm > 0)) return steps;
        for (var d = LostSearchStepMm; d < maxMm - 1e-6; d += LostSearchStepMm) steps.Add(d);
        steps.Add(maxMm);
        return steps;
    }

    /// <summary>
    /// 작은 이동 노이즈 한계 파라미터 키 [mm]. 이동량이 이보다 작으면 측정변화가 센서 노이즈(~1mm)에 묻혀
    /// "측정 불변/반대로 변함" 판정이 맞지 않으므로 방향 판정 없이 잔차만큼 보정하고, 남은 잔차가 이 안이면 그대로 진행한다.
    /// 0 = 끔(종전 판정). 비우면 가스 <see cref="DefaultSmallMoveMm"/> / 비전 끔.
    /// (2026-11-21 M2 19:29 이동 +1.18mm·변화 0 → "불변", 21:21 이동 +1.33mm·변화 +1.13mm → "반대" 로 점2 묶음 해제.)
    /// </summary>
    public const string SmallMoveKey = "Sequence.Laser.SmallMoveNoiseMm";
    internal const double DefaultSmallMoveMm = 3.0;
    internal const double MaxSmallMoveMm = MaxCorrectionMm;

    /// <summary>파라미터 값 → 작은 이동 한계 [mm]. 비거나 범위 밖이면 센서 모드 기본값(가스 3 / 비전 0).</summary>
    internal static double ResolveSmallMoveMm(double? value, bool usesGas)
        => value is { } v && double.IsFinite(v) && v >= 0 && v <= MaxSmallMoveMm ? v
         : usesGas ? DefaultSmallMoveMm : 0;

    /// <summary>작은 이동이라 측정 응답과 무관하게 잔차만큼 보정해도 되는지.</summary>
    internal static bool ShouldCorrectSmall(double move, double residual, double smallMm)
        => smallMm > 0 && Math.Abs(move) < smallMm
           && Math.Abs(residual) > ToleranceMm && Math.Abs(residual) <= MaxCorrectionMm;

    /// <summary>작은 이동 뒤 남은 잔차가 노이즈 허용 안이라 그대로 진행해도 되는지.</summary>
    internal static bool AcceptSmallResidual(double move, double residual, double smallMm)
        => smallMm > 0 && Math.Abs(move) < smallMm && Math.Abs(residual) <= smallMm;

    private readonly string _key;
    private readonly string _displayName;
    private readonly int _order;
    private readonly bool _useGas;

    public LaserWorkingDistanceStep(
        string key, string displayName, int order,
        CobotService cobot, LaserDisplacementSensorService laser,
        ParameterService param, Microsoft.Extensions.Options.IOptions<InspectionSensorSettings> sensor,
        ILogger<LaserWorkingDistanceStep> logger)
    {
        _cobot = cobot;
        _laser = laser;
        _param = param;
        _useGas = sensor.Value.UsesGasDetector;
        _logger = logger;
        _key = key;
        _displayName = displayName;
        _order = order;
    }

    public const string BundlePoint2Key = "laserWorkingDistanceBundlePoint2";

    public string Key => _key;
    public string DisplayName => _displayName;
    public int DefaultOrder => _order;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");
        if (!_laser.IsConnected)
            return StepValidation.Fail("레이저 변위센서 미연결 — 거리 측정 불가");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (_key == BundlePoint2Key && !await BundleTeach.RunPoint2FlatWdAsync(_param, context))
            return StepResult.Ok($"묶음 점2가 아님 — WD 재조정 생략('{BundleTeach.SoloPoint2FlatWdKey}'=false).");

        var result = await AdjustAsync(context, ct);
        if (_key != BundlePoint2Key || result.Success || !BundleTeach.TryMarkDissolved(context))
        {
            if (result.Success && _key == BundlePoint2Key)
                context.Bag[BundleTeach.Point2WdKey] = true;
            return result;
        }
        _logger.LogWarning("묶음 점2 WD 실패 — 단독 교시로 전환: {Msg}", result.Message);
        context.Progress?.Invoke($"⚠ 묶음 점2 WD 실패, 단독 교시로 진행: {result.Message}");
        var back = await BundleTeach.RepositionToSoloAsync(_cobot, _param, _logger, context, ct);
        return back.Success
            ? StepResult.Ok($"묶음 해제 — 단독 교시. 원인: {result.Message}")
            : StepResult.Fail($"{result.Message} / {back.Message}");
    }

    private async Task<StepResult> AdjustAsync(SequenceContext context, CancellationToken ct)
    {
        // 면별 값(…WorkingDistanceMm.{wall_code})이 있으면 그것, 없으면 공통 값.
        var wall = LaserWorkingDistanceSetting.WallOf(context);
        var (wd, wdSource) = await LaserWorkingDistanceSetting.ResolveAsync(_param, wall);
        if (wd is < 100 or > 1000)
            return StepResult.Fail($"'{wdSource}' 범위 초과 ({wd:0.#}mm) — 100~1000mm 로 설정하세요.");

        var (depthAxis, axisFromParam) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        if (!axisFromParam)
            return StepResult.Fail(WeldSequenceSupport.OpticalAxisMissingMessage(DisplayName));

        double? rawOutlier = null;
        try { rawOutlier = await _param.GetDoubleAsync(OutlierKey); }
        catch { /* DB 미준비 — 기본값 */ }
        _outlierMm = ResolveOutlierMm(rawOutlier, _useGas);

        // ── 이동 전 측정 (④ 마지막 모션 직후 진입 — 안정화 대기 필수) ──
        var (z1, note1) = await SampleStableZAsync(ct);
        var searchNote = "";
        if (z1 is null && note1?.StartsWith("유효 측정 없음") == true)
        {
            var (zs, approached, sNote) = await SearchLostLaserAsync(context, depthAxis, ct);
            if (zs is null)
                return StepResult.Fail($"레이저 3점 거리 측정 실패: {note1}{sNote}");
            (z1, searchNote) = (zs, $" 레이저 분실 → 벽 쪽 {approached:0.#}mm 다가가 재탐색.");
        }
        if (z1 is null)
            return StepResult.Fail($"레이저 3점 거리 측정 실패: {note1}");

        var delta = z1.Value - wd;
        _logger.LogInformation(
            "[레이저 WD 거리 조정] WD 조정: 면={Wall}, z={Z:0.##}mm, 목표={Wd:0.#}mm({Source}), Δ={Delta:+0.##;-0.##}mm",
            wall ?? "-", z1.Value, wd, wdSource, delta);
        await LogDiagnosticsAsync("이동 전", context, wall, wd, wdSource, depthAxis, ct);

        if (Math.Abs(delta) <= ToleranceMm)
        {
            var (retOk0, retNote0) = await ReturnLateralAsync(context, depthAxis, ct);
            if (retOk0) await LogDiagnosticsAsync("완료(이동 생략)", context, wall, wd, wdSource, depthAxis, ct);
            return retOk0
                ? StepResult.Ok(
                    $"이미 WD 근방 — z={z1.Value:0.##}mm (목표 {wd:0.#}mm, 잔차 {delta:+0.##;-0.##}mm), 이동 생략.{searchNote}{retNote0}")
                : StepResult.Fail(retNote0);
        }

        if (Math.Abs(delta) > MaxTravelMm)
            return StepResult.Fail(
                $"이동량 {delta:+0.#;-0.#}mm 가 한계 ±{MaxTravelMm:0}mm 초과 — " +
                $"측정값(z={z1.Value:0.#}mm) 또는 '{wdSource}' 설정을 확인하세요.");

        // ── 개루프 이동 + 재측정 검증. 정상 반응·작은 잔차면 보정 이동(최대 MaxCorrections 회) ──
        // delta>0 = 대상이 목표보다 멀다 = 전방(광축) 접근. ③ MoveToDistanceAsync 와 동일 부호 규약.
        double? rawSmall = null;
        try { rawSmall = await _param.GetDoubleAsync(SmallMoveKey); }
        catch { /* DB 미준비 — 기본값 */ }
        var smallMm = ResolveSmallMoveMm(rawSmall, _useGas);

        var move = delta;            // 이번 이동량
        var totalMove = 0.0;
        var zBefore = z1.Value;
        double residual, change;
        double z2Value;
        for (int attempt = 0; ; attempt++)
        {
            var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            var offset = new double[6];
            FlatSurfaceCenteringService.ApplyAxis(offset, depthAxis, move);

            _logger.LogInformation(
                "[레이저 WD 거리 조정] WD {Kind}: 툴{Axis} {Delta:+0.##;-0.##}mm",
                attempt == 0 ? "접근" : $"보정 {attempt}회차",
                FlatSurfaceCenteringService.AxisName(depthAxis), move);

            var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
                tool: context.Tool, vel: context.Velocity, ct: ct);
            if (rc == 112)
                return StepResult.Fail(
                    $"WD 접근 이동 실패 (rc=112: 목표 자세 도달 불가) — " +
                    $"광축 매핑(툴{FlatSurfaceCenteringService.AxisName(depthAxis)})/방향을 확인하세요.");
            if (rc != 0)
                return StepResult.Fail($"WD 접근 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
            totalMove += move;

            // ── 재측정 검증(모션 없음) — 안정화 대기 후 실측 응답으로 원인 진단 ──
            // 고정 딜레이 대신 안정화 폴링이 모션 잔여/센서 필터 수렴 대기를 겸한다.
            var (z2, note2) = await SampleStableZAsync(ct);
            if (z2 is null)
                return StepResult.Fail($"이동 후 레이저 재측정 실패: {note2}");

            z2Value = z2.Value;
            residual = z2Value - wd;
            change = z2Value - zBefore;   // 정상이면 ≈ −move (이동한 만큼 거리가 줄거나 늘어남)
            _logger.LogInformation(
                "[레이저 WD 거리 조정] WD 검증: z={Z:0.##}mm, 잔차={Res:+0.##;-0.##}mm, 측정변화={Chg:+0.##;-0.##}mm (기대 {Exp:+0.##;-0.##}mm)",
                z2Value, residual, change, -move);
            await LogDiagnosticsAsync(attempt == 0 ? "이동 후" : $"보정 {attempt}회차 후", context, wall, wd, wdSource, depthAxis, ct);

            var small = ShouldCorrectSmall(move, residual, smallMm);
            if (Math.Abs(residual) <= ToleranceMm || attempt >= MaxCorrections
                || !(ShouldCorrect(move, change, residual) || small))
                break;

            if (small && !ShouldCorrect(move, change, residual))
                _logger.LogInformation(
                    "[레이저 WD 거리 조정] 잔차 {Res:+0.##;-0.##}mm — 작은 이동({Move:+0.##;-0.##}mm < {Small:0.#}mm)이라 " +
                    "측정변화 {Chg:+0.##;-0.##}mm 는 센서 노이즈로 보고 방향 판정 없이 잔차만큼 보정 이동",
                    residual, move, smallMm, change);
            else
                _logger.LogInformation(
                    "[레이저 WD 거리 조정] 잔차 {Res:+0.##;-0.##}mm — 정상 반응(측정변화 {Chg:+0.##;-0.##}mm / 이동 {Move:+0.##;-0.##}mm)이라 잔차만큼 보정 이동",
                    residual, change, move);
            move = residual;
            zBefore = z2Value;
        }

        var smallNote = "";
        if (Math.Abs(residual) > ToleranceMm && AcceptSmallResidual(move, residual, smallMm))
        {
            _logger.LogWarning(
                "[레이저 WD 거리 조정] 작은 이동 뒤 잔차 {Res:+0.##;-0.##}mm 가 노이즈 허용 {Small:0.#}mm 안 — 그대로 진행 ('{Key}')",
                residual, smallMm, SmallMoveKey);
            smallNote = $" 작은 이동 뒤 잔차 {residual:+0.##;-0.##}mm 는 센서 노이즈 범위(±{smallMm:0.#}mm)라 그대로 진행.";
        }

        if (Math.Abs(residual) <= ToleranceMm || smallNote.Length > 0)
        {
            var (retOk, retNote) = await ReturnLateralAsync(context, depthAxis, ct);
            if (retOk) await LogDiagnosticsAsync("완료(횡복귀 후)", context, wall, wd, wdSource, depthAxis, ct);
            return retOk
                ? StepResult.Ok(
                    $"WD 도달 — z={z2Value:0.##}mm (목표 {wd:0.#}mm, 이동 {totalMove:+0.##;-0.##}mm, " +
                    $"잔차 {residual:+0.##;-0.##}mm).{searchNote}{smallNote}{retNote}")
                : StepResult.Fail(retNote);
        }

        var axisName = FlatSurfaceCenteringService.AxisName(depthAxis);
        var head = $"WD 미도달 — 잔차 {residual:+0.##;-0.##}mm > 허용 {ToleranceMm}mm " +
                   $"(이동 {move:+0.##;-0.##}mm, 측정변화 {change:+0.##;-0.##}mm). ";

        if (Math.Abs(change) < NoResponseMm)
            return StepResult.Fail(head +
                $"이동해도 측정 거리가 변하지 않음 — 광축 매핑(툴{axisName})이 측정 방향과 수직이거나 " +
                "센서 값이 갱신되지 않고 있습니다.");

        if (change * move > 0)
            return StepResult.Fail(head +
                $"측정 거리가 기대와 반대로 변함 — '{WeldSequenceSupport.DepthAxisKey}' 를 " +
                $"현재 툴{axisName} 의 반대 부호 축으로 설정하세요.");

        if (Math.Abs(residual) <= MaxCorrectionMm && IsNormalResponse(move, change))
            return StepResult.Fail(head +
                $"보정 이동 {MaxCorrections}회 뒤에도 남음 — 측정값이 계속 흔들립니다(기구물 진동·리프트 정착·면 굴곡). " +
                "잠시 뒤 재시도하세요.");

        return StepResult.Fail(head + "측정이 이동량과 크게 다르게 반응 — 센서 노이즈/스케일 설정을 확인하세요.");
    }

    /// <summary>
    /// 첫 측정에서 3채널이 범위 밖일 때 광축(벽 쪽, +이동 = 접근)으로 <see cref="LostSearchStepMm"/> 씩 다가가며
    /// 유효 측정이 잡히는지 본다. 잡히면 안정화 측정값을 돌려주고, 끝까지 안 잡히면 다가간 만큼 되돌아간다.
    /// 최대 거리는 <see cref="LostSearchKey"/>(가스 60mm / 비전 끔). 이동은 레이저 WD(~400mm) 앞 범위라 벽과 닿지 않는다.
    /// </summary>
    private async Task<(double? Z, double Approached, string Note)> SearchLostLaserAsync(
        SequenceContext context, ToolAxisDir depthAxis, CancellationToken ct)
    {
        double? raw = null;
        try { raw = await _param.GetDoubleAsync(LostSearchKey); }
        catch { /* DB 미준비 — 기본값 */ }
        var maxMm = ResolveLostSearchMm(raw, _useGas);
        var steps = LostSearchSteps(maxMm);
        if (steps.Count == 0)
            return (null, 0, $" (레이저 분실 재탐색 꺼짐 — '{LostSearchKey}')");

        var axisName = FlatSurfaceCenteringService.AxisName(depthAxis);
        _logger.LogWarning(
            "[레이저 WD 거리 조정] 레이저 분실 — 벽 쪽(툴{Axis})으로 {Step:0}mm 씩 최대 {Max:0}mm 다가가며 재탐색",
            axisName, LostSearchStepMm, maxMm);
        context.Progress?.Invoke($"레이저 분실 — 벽 쪽으로 최대 {maxMm:0}mm 다가가며 재탐색");

        var approached = 0.0;
        foreach (var target in steps)
        {
            var move = target - approached;
            var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            var offset = new double[6];
            FlatSurfaceCenteringService.ApplyAxis(offset, depthAxis, move);
            var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
                tool: context.Tool, vel: context.Velocity, ct: ct);
            if (rc != 0)
                return (null, approached, await BackOffAsync(approached,
                    $" / 재탐색 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}"));
            approached = target;

            if (!await ProbeValidAsync(ct))
            {
                _logger.LogInformation("[레이저 WD 거리 조정] 재탐색 {Mm:0.#}mm 다가감 — 아직 측정 없음", approached);
                continue;
            }

            var (z, note) = await SampleStableZAsync(ct);
            if (z is { } zv)
            {
                _logger.LogInformation(
                    "[레이저 WD 거리 조정] 재탐색 성공 — {Mm:0.#}mm 다가가 z={Z:0.##}mm", approached, zv);
                return (zv, approached, "");
            }
            _logger.LogInformation("[레이저 WD 거리 조정] 재탐색 {Mm:0.#}mm — 잡혔지만 안정되지 않음: {Note}", approached, note);
        }

        return (null, approached, await BackOffAsync(approached,
            $" / 벽 쪽으로 {approached:0.#}mm 다가가도 측정 없음 — 레이저가 벽 끝·개구부를 보고 있는지 확인하세요"));

        async Task<string> BackOffAsync(double mm, string note)
        {
            if (mm <= 0) return note;
            try
            {
                var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
                var back = new double[6];
                FlatSurfaceCenteringService.ApplyAxis(back, depthAxis, -mm);
                var rcBack = await _cobot.Rpc.MoveByToolOffsetAsync(cur, user: 0, back,
                    tool: context.Tool, vel: context.Velocity, ct: ct);
                return rcBack == 0 ? $"{note} (다가간 {mm:0.#}mm 되돌림)" : $"{note} (되돌림 실패 rc={rcBack})";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"{note} (되돌림 실패: {ex.Message})";
            }
        }
    }

    /// <summary>유효 측정이 <see cref="LostSearchProbeMs"/> 안에 연속 2번 나오는지 — 잡히는지만 본다.</summary>
    private async Task<bool> ProbeValidAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = 0;
        while (sw.ElapsedMilliseconds < LostSearchProbeMs)
        {
            ct.ThrowIfCancellationRequested();
            run = SampleZ(_laser, _outlierMm).Valid ? run + 1 : 0;
            if (run >= 2) return true;
            await Task.Delay(PollMs, ct);
        }
        return false;
    }

    /// <summary>측정 응답이 정상(기대 방향, 이동량의 <see cref="MinResponseRatio"/>~<see cref="MaxResponseRatio"/>배)인지.</summary>
    internal static bool IsNormalResponse(double move, double change)
    {
        if (Math.Abs(move) < 1e-9 || change * move >= 0)   // 무이동·무반응·반대 방향
            return false;
        var ratio = Math.Abs(change) / Math.Abs(move);
        return ratio is >= MinResponseRatio and <= MaxResponseRatio;
    }

    /// <summary>잔차만큼 보정 이동해도 되는지 — 허용 밖이고, <see cref="MaxCorrectionMm"/> 이내이며, 직전 이동에 측정이 정상 반응했을 때만.</summary>
    internal static bool ShouldCorrect(double move, double change, double residual)
        => Math.Abs(residual) > ToleranceMm
           && Math.Abs(residual) <= MaxCorrectionMm
           && IsNormalResponse(move, change);

    /// <summary>
    /// 면별 WD 차이의 원인 조사용 진단 로그(<see cref="LaserWorkingDistanceSetting.Diagnostics"/>).
    /// 읽기만 하고 로봇을 움직이지 않는다. 읽기 실패는 경고만 남기고 검사를 막지 않는다.
    /// "완료" 단계의 값이 실제 촬영 때의 상태다(횡복귀 뒤라 레이저가 닿는 자리가 "이동 후"와 다를 수 있다).
    /// </summary>
    private async Task LogDiagnosticsAsync(string phase, SequenceContext context, string? wall,
        double wd, string wdSource, ToolAxisDir depthAxis, CancellationToken ct)
    {
        try
        {
            var readings = _laser.GetReadings();
            var raw = _laser.ComputeRawPlanePose(readings);
            var corrected = _laser.ApplyTiltReference(raw);

            double[]? joints = null, tcp = null;
            try
            {
                joints = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
                tcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[WD 진단] 코봇 자세를 읽지 못함 — 레이저 값만 기록: {Msg}", ex.Message);
            }

            _logger.LogInformation("{Line}", LaserWorkingDistanceSetting.Diagnostics(
                phase, wall, context.InspectionDirection, wd, wdSource, readings, raw, corrected, joints, tcp, depthAxis));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[WD 진단] 기록 실패({Phase}): {Msg}", phase, ex.Message);
        }
    }

    /// <summary>
    /// 복귀 기준점(②의 용접선 점 S, 없으면 최초 접근점 A)을 지나는 보정 후 광축 직선 위로, 현재 면 평행 평면 안에서
    /// 복귀한다(목표 = P + n·dot(C−P, n)). 현재 광축 거리·자세는 유지한다. 기준점이 없으면 실패한다.
    /// </summary>
    private async Task<(bool Ok, string Note)> ReturnLateralAsync(SequenceContext context, ToolAxisDir depthAxis, CancellationToken ct)
    {
        double[]? reference;
        string refSource;
        if (_key == BundlePoint2Key)
        {
            // 점2 는 점2 자리로 돌아간다 — ② 용접선 시작점(점1 쪽)으로 가면 안 된다.
            // 평탄면 탐색을 하지 않았으면(자리 기록 없음) 툴은 아직 점2 자리에 있으므로 옆으로 움직일 것이 없다.
            if (!context.Bag.TryGetValue(BundleTeach.Point2ReturnPoseKey, out var p2) || p2 is not double[] { Length: 6 } point2)
                return (true, "");
            (reference, refSource) = (point2, "점2 위치");
        }
        else
            (reference, refSource) = InspectionAlignmentAnchor.GetReturnReference(context);
        if (reference is null)
            return (false, "최초 접근 앵커 없음 — 검사위치 이동 또는 평탄면 정렬 단계를 먼저 실행하세요.");

        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = InspectionAlignmentAnchor.ReturnOffset(reference, cur, depthAxis);
        double dx = offset[0], dy = offset[1], dz = offset[2];

        if (Math.Abs(dx) < SkipReturnMm && Math.Abs(dy) < SkipReturnMm && Math.Abs(dz) < SkipReturnMm)
            return (true, "");

        double? rawLimit = null;
        try { rawLimit = await _param.GetDoubleAsync(MaxReturnKey); }
        catch { /* DB 미준비 — 기본값 */ }
        var maxReturnMm = ResolveMaxReturnMm(rawLimit);
        if (Math.Abs(dx) > maxReturnMm || Math.Abs(dy) > maxReturnMm || Math.Abs(dz) > maxReturnMm)
            return (false,
                $"검사 준비 위치 횡복귀량 (X{dx:+0.0;-0.0}, Y{dy:+0.0;-0.0}, Z{dz:+0.0;-0.0})mm 가 한계 ±{maxReturnMm:0}mm 초과 — " +
                $"앵커 포즈/자세를 확인하세요('{MaxReturnKey}').");

        _logger.LogInformation(
            "[레이저 WD 거리 조정] {Ref} 정면 횡복귀: 툴 X{Dx:+0.0;-0.0} / Y{Dy:+0.0;-0.0} / Z{Dz:+0.0;-0.0} mm (광축 {Axis}·자세 유지)", refSource, dx, dy, dz, depthAxis);

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(cur, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return (false, $"검사 준비 위치 횡복귀 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(300, ct);

        return (true, $" {refSource} 정면 횡복귀(툴 X{dx:+0.0;-0.0}/Y{dy:+0.0;-0.0}/Z{dz:+0.0;-0.0}mm).");
    }

    /// <summary>
    /// 레이저 3점 평면 pose 의 Z 가 <b>안정될 때까지</b> 폴링해 안정 윈도우 평균(mm)을 반환한다.
    /// 최근 <see cref="StableWindow"/>개 연속 유효 샘플의 폭이 <see cref="StableBandMm"/> 미만이면 안정.
    /// 무효 샘플이 나오면 윈도우를 리셋하고 계속 폴링, <see cref="StabilizeTimeoutMs"/> 초과 시 null.
    /// 모션 잔여(rc=0 이후에도 이동 중)와 센서 내부 평균화 필터의 수렴 지연을 모두 흡수한다.
    /// </summary>
    private Task<(double? Z, string? Note)> SampleStableZAsync(CancellationToken ct)
        => SampleStableZAsync(_laser, _logger, ct, _outlierMm);

    /// <summary>이번 실행의 튀는 채널 한계 [mm] — <see cref="AdjustAsync"/> 시작 때 읽는다.</summary>
    private double _outlierMm;

    /// <summary>
    /// 튀는 채널 제외 한계 파라미터 키 [mm]. 3채널 중 둘이 서로 <see cref="ChannelAgreeMm"/> 안에서 맞고 나머지 하나가 그 평균에서
    /// 이 값 넘게 벗어나거나 범위 밖이면, 맞는 두 채널 평균을 거리로 쓴다. 0 = 끔(3채널 평균만). 비우면 가스 <see cref="DefaultOutlierMm"/> / 비전 끔.
    /// (2026-11-21 M2 22:39 S013-2 점2: CH1 400.44·CH2 400.54 인데 CH3 만 291.0(돌출부) → 평균 364 로 WD 실패.)
    /// </summary>
    public const string OutlierKey = "Sequence.Laser.OutlierChannelMm";
    internal const double DefaultOutlierMm = 10.0;
    internal const double MaxOutlierMm = 100.0;

    /// <summary>"맞는 두 채널" 로 볼 최대 차 [mm].</summary>
    internal const double ChannelAgreeMm = 3.0;

    /// <summary>파라미터 값 → 튀는 채널 한계 [mm]. 비거나 범위 밖이면 센서 모드 기본값(가스 10 / 비전 0).</summary>
    internal static double ResolveOutlierMm(double? value, bool usesGas)
        => value is { } v && double.IsFinite(v) && v >= 0 && v <= MaxOutlierMm ? v
         : usesGas ? DefaultOutlierMm : 0;

    /// <summary>
    /// 채널 값(범위 밖은 null) → 튀는 채널 하나를 뺀 두 채널 평균과 뺀 채널 번호(1-based).
    /// 3채널이 모두 한계 안에서 맞으면(또는 끔) null — 평소 3채널 평면을 쓴다. 맞는 짝이 없어도 null.
    /// </summary>
    internal static (double Z, int Excluded)? AgreeingPairZ(IReadOnlyList<double?> ch, double outlierMm)
    {
        if (!(outlierMm > 0) || ch.Count < 3) return null;
        var valid = Enumerable.Range(0, 3).Where(i => ch[i] is not null).ToArray();
        if (valid.Length == 3)
        {
            var vals = valid.Select(i => ch[i]!.Value).ToArray();
            if (vals.Max() - vals.Min() <= outlierMm) return null;
        }
        else if (valid.Length < 2) return null;

        (double Z, int Excluded)? best = null;
        var bestDiff = double.MaxValue;
        for (var a = 0; a < 3; a++)
        for (var b = a + 1; b < 3; b++)
        {
            if (ch[a] is not { } va || ch[b] is not { } vb) continue;
            var diff = Math.Abs(va - vb);
            if (diff > ChannelAgreeMm || diff >= bestDiff) continue;
            var other = 3 - a - b;
            var mean = (va + vb) / 2;
            if (ch[other] is { } vo && Math.Abs(vo - mean) <= outlierMm) continue;   // 셋째도 맞음 — 튀는 채널 아님
            (best, bestDiff) = ((mean, other + 1), diff);
        }
        return best;
    }

    /// <summary>한 번 판독 — 평소엔 3채널 평면 z, 튀는 채널이 하나면 맞는 두 채널 평균.</summary>
    private static (bool Valid, double Z, string? Note, string? Excluded) SampleZ(
        LaserDisplacementSensorService laser, double outlierMm)
    {
        if (outlierMm > 0)
        {
            var r = laser.GetReadings();
            if (r.Count >= 3)
            {
                var ch = new double?[] { r[0].Enabled ? r[0].Value : null, r[1].Enabled ? r[1].Value : null, r[2].Enabled ? r[2].Value : null };
                if (AgreeingPairZ(ch, outlierMm) is { } pair)
                {
                    var o = ch[pair.Excluded - 1];
                    return (true, pair.Z, null,
                        $"CH{pair.Excluded} {(o is { } ov ? $"{ov:0.#}mm" : "범위 밖")} 제외, 나머지 두 채널 평균");
                }
            }
        }
        var p = laser.GetPlanePose();
        return (p.Valid, p.Z, p.Note, null);
    }

    /// <summary>레이저 3점 평균 거리(Z, mm)를 안정화될 때까지 폴링해 돌려준다 — 실패 시 (null, 사유).
    /// ⑧ 피치 이동(들어올림 보정)도 같은 판정으로 잰다.</summary>
    internal static async Task<(double? Z, string? Note)> SampleStableZAsync(
        LaserDisplacementSensorService laser, ILogger logger, CancellationToken ct, double outlierMm = 0)
    {
        var _laser = laser;
        var _logger = logger;
        var window = new Queue<double>(StableWindow);
        string? lastNote = null;
        string? excludeNote = null;
        double? lastZ = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var samples = 0;

        while (sw.ElapsedMilliseconds < StabilizeTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            var (valid, zNow, note, excluded) = SampleZ(_laser, outlierMm);
            samples++;
            if (valid)
            {
                if (excluded is not null) excludeNote = excluded;
                lastZ = zNow;
                window.Enqueue(zNow);
                if (window.Count > StableWindow) window.Dequeue();

                if (window.Count == StableWindow && window.Max() - window.Min() < StableBandMm)
                {
                    var z = window.Average();
                    _logger.LogInformation(
                        "[레이저 WD 거리 조정] 측정 안정화 완료: z={Z:0.##}mm (폭 {Band:0.###}mm, {Elapsed}ms, 샘플 {N}개){Excl}",
                        z, window.Max() - window.Min(), sw.ElapsedMilliseconds, samples,
                        excludeNote is null ? "" : $" — {excludeNote}");
                    return (z, null);
                }
            }
            else
            {
                lastNote = note;
                window.Clear();   // 무효 구간을 사이에 둔 샘플끼리 안정 판정하지 않는다.
            }

            await Task.Delay(PollMs, ct);
        }

        return (null, lastZ is null
            ? $"유효 측정 없음 ({lastNote ?? "원인 미상"})"
            : $"{StabilizeTimeoutMs / 1000.0:0.#}s 내 안정화되지 않음 (마지막 z={lastZ:0.##}mm) — " +
              "로봇이 계속 움직이거나 센서 평균화 설정이 과도한지 확인하세요.");
    }
}
