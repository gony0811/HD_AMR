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

    /// <summary>이동 도착 판정 — TCP 가 앵커에서 이동량만큼(±<see cref="ArriveToleranceMm"/>) 옮겨졌고, 연속 두 폴링 사이
    /// 위치 변화가 <see cref="ArriveStillMm"/> 미만이면 도착으로 본다.</summary>
    internal const double ArriveToleranceMm = 0.3, ArriveStillMm = 0.05;

    /// <summary>도착 확인 폴링 간격·한도(ms). 한도를 넘으면 경고만 남기고 측정으로 넘어간다(종전 동작).</summary>
    private const int ArrivePollMs = 50, ArriveTimeoutMs = 5000;

    /// <summary>안정화 대기 한도(ms). 초과 시 측정 실패 처리.</summary>
    private const int StabilizeTimeoutMs = 8000;

    /// <summary>횡복귀 성분별 절대 한계(mm). 초과 시 이동하지 않고 실패 — 앵커/자세 이상 가드.</summary>
    private const double MaxReturnMm = 300.0;

    /// <summary>이 값 미만의 횡복귀 성분은 무시한다(mm).</summary>
    private const double SkipReturnMm = 0.5;

    private readonly string _key;
    private readonly string _displayName;
    private readonly int _order;

    public LaserWorkingDistanceStep(
        string key, string displayName, int order,
        CobotService cobot, LaserDisplacementSensorService laser,
        ParameterService param, ILogger<LaserWorkingDistanceStep> logger)
    {
        _cobot = cobot;
        _laser = laser;
        _param = param;
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
        if (_key == BundlePoint2Key && !BundleTeach.IsActive(context))
            return StepResult.Ok("묶음 점2가 아님 — WD 재조정 생략.");

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

        // ── 이동 전 측정 (④ 마지막 모션 직후 진입 — 안정화 대기 필수) ──
        var (z1, note1) = await SampleStableZAsync(ct);
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
                    $"이미 WD 근방 — z={z1.Value:0.##}mm (목표 {wd:0.#}mm, 잔차 {delta:+0.##;-0.##}mm), 이동 생략.{retNote0}")
                : StepResult.Fail(retNote0);
        }

        if (Math.Abs(delta) > MaxTravelMm)
            return StepResult.Fail(
                $"이동량 {delta:+0.#;-0.#}mm 가 한계 ±{MaxTravelMm:0}mm 초과 — " +
                $"측정값(z={z1.Value:0.#}mm) 또는 '{wdSource}' 설정을 확인하세요.");

        // ── 개루프 이동 + 재측정 검증. 정상 반응·작은 잔차면 보정 이동(최대 MaxCorrections 회) ──
        // delta>0 = 대상이 목표보다 멀다 = 전방(광축) 접근. ③ MoveToDistanceAsync 와 동일 부호 규약.
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

            // ── 코봇이 실제로 도착할 때까지 기다린다 ── MoveL 은 rc=0 을 모션 완료 전에 돌려줄 수 있고, 1mm 안팎의 작은
            // 보정 이동은 레이저 값이 안정 폭(0.3mm) 안에서만 변해 아래 안정화 대기가 '움직이기 전 값' 을 바로 확정한다
            // (실기 2026-11-18 11:37 PL R01C13 묶음 점2: +1.03mm 보정에 측정변화 +0.08mm, 309ms 만에 확정 → 묶음 해제).
            await WaitArrivedAsync(anchor, Math.Abs(move), context.Tool, ct);

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

            if (Math.Abs(residual) <= ToleranceMm || attempt >= MaxCorrections
                || !ShouldCorrect(move, change, residual))
                break;

            _logger.LogInformation(
                "[레이저 WD 거리 조정] 잔차 {Res:+0.##;-0.##}mm — 정상 반응(측정변화 {Chg:+0.##;-0.##}mm / 이동 {Move:+0.##;-0.##}mm)이라 잔차만큼 보정 이동",
                residual, change, move);
            move = residual;
            zBefore = z2Value;
        }

        if (Math.Abs(residual) <= ToleranceMm)
        {
            var (retOk, retNote) = await ReturnLateralAsync(context, depthAxis, ct);
            if (retOk) await LogDiagnosticsAsync("완료(횡복귀 후)", context, wall, wd, wdSource, depthAxis, ct);
            return retOk
                ? StepResult.Ok(
                    $"WD 도달 — z={z2Value:0.##}mm (목표 {wd:0.#}mm, 이동 {totalMove:+0.##;-0.##}mm, " +
                    $"잔차 {residual:+0.##;-0.##}mm).{retNote}")
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
            (reference, refSource) = (point2, "묶음 점2 위치");
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

        if (Math.Abs(dx) > MaxReturnMm || Math.Abs(dy) > MaxReturnMm || Math.Abs(dz) > MaxReturnMm)
            return (false,
                $"검사 준비 위치 횡복귀량 (X{dx:+0.0;-0.0}, Y{dy:+0.0;-0.0}, Z{dz:+0.0;-0.0})mm 가 한계 ±{MaxReturnMm:0}mm 초과 — " +
                "앵커 포즈/자세를 확인하세요.");

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
    /// 이동 명령 뒤 TCP 가 <paramref name="anchor"/> 에서 <paramref name="expectedMm"/> 만큼 옮겨지고 멈출 때까지 기다린다.
    /// 한도 안에 도착하지 않으면 경고만 남긴다(측정·진단은 그대로 이어서 한다).
    /// </summary>
    private async Task WaitArrivedAsync(double[] anchor, double expectedMm, int tool, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double[]? prev = null;
        var lastMoved = double.NaN;
        while (sw.ElapsedMilliseconds < ArriveTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, ct);
            lastMoved = Dist3(cur, anchor);
            if (IsArrived(lastMoved, expectedMm, prev is null ? double.PositiveInfinity : Dist3(cur, prev)))
            {
                _logger.LogInformation("[레이저 WD 거리 조정] 이동 도착 확인: {Moved:0.##}mm (지령 {Exp:0.##}mm, {Ms}ms)",
                    lastMoved, expectedMm, sw.ElapsedMilliseconds);
                return;
            }
            prev = cur;
            await Task.Delay(ArrivePollMs, ct);
        }
        _logger.LogWarning("[레이저 WD 거리 조정] 이동 도착 확인 시간 초과({Ms}ms) — 이동 {Moved:0.##}mm / 지령 {Exp:0.##}mm. 그대로 측정합니다.",
            ArriveTimeoutMs, lastMoved, expectedMm);
    }

    /// <summary>도착 판정 — 옮겨진 거리가 지령과 ±<see cref="ArriveToleranceMm"/> 안이고, 직전 폴링 대비 거의 멈췄으면 true.</summary>
    internal static bool IsArrived(double movedMm, double expectedMm, double stepMm)
        => Math.Abs(movedMm - expectedMm) <= ArriveToleranceMm && stepMm < ArriveStillMm;

    private static double Dist3(double[] a, double[] b)
        => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    /// <summary>
    /// 레이저 3점 평면 pose 의 Z 가 <b>안정될 때까지</b> 폴링해 안정 윈도우 평균(mm)을 반환한다.
    /// 최근 <see cref="StableWindow"/>개 연속 유효 샘플의 폭이 <see cref="StableBandMm"/> 미만이면 안정.
    /// 무효 샘플이 나오면 윈도우를 리셋하고 계속 폴링, <see cref="StabilizeTimeoutMs"/> 초과 시 null.
    /// 모션 잔여(rc=0 이후에도 이동 중)와 센서 내부 평균화 필터의 수렴 지연을 모두 흡수한다.
    /// </summary>
    private async Task<(double? Z, string? Note)> SampleStableZAsync(CancellationToken ct)
    {
        var window = new Queue<double>(StableWindow);
        string? lastNote = null;
        double? lastZ = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var samples = 0;

        while (sw.ElapsedMilliseconds < StabilizeTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            var p = _laser.GetPlanePose();
            samples++;
            if (p.Valid)
            {
                lastZ = p.Z;
                window.Enqueue(p.Z);
                if (window.Count > StableWindow) window.Dequeue();

                if (window.Count == StableWindow && window.Max() - window.Min() < StableBandMm)
                {
                    var z = window.Average();
                    _logger.LogInformation(
                        "[레이저 WD 거리 조정] 측정 안정화 완료: z={Z:0.##}mm (폭 {Band:0.###}mm, {Elapsed}ms, 샘플 {N}개)",
                        z, window.Max() - window.Min(), sw.ElapsedMilliseconds, samples);
                    return (z, null);
                }
            }
            else
            {
                lastNote = p.Note;
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
