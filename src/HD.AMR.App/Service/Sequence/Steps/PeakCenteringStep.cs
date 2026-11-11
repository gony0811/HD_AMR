using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑥/⑩ Peak 센터링 — 앞 단계가 잰 이격거리만큼 <b>툴 프레임</b>으로 <b>1회</b> 이동해 Peak 를 FOV 센터로 옮긴다.
///
/// 이동 축은 진행축(② 검사방향에서 자동 결정 — 영상 가로면 <see cref="WeldSequenceSupport.ImageXAxisKey"/>,
/// 세로면 <see cref="WeldSequenceSupport.ImageYAxisKey"/>)의 툴축 매핑(부호 포함)을 쓴다 —
/// 과거 BASE X 하드코딩은 헤드 방향에 따라 광축(툴 ±Z) 성분이 섞이는 좌표계 오류였음(③의 BASE −Y 버그와 동일 계열).
///
/// 개루프 1회 이동 후 재측정해 ImageXAxis 매핑 오설정(이격이 오히려 커짐)을 잡아낸다.
/// 잔차가 <see cref="ResidualMaxKey"/>(기본 5mm)를 넘으면 최대 2회 보정 이동한다. 그래도 남으면 실패시키지 않고
/// 경고만 남긴 채 진행한다(보정이 발산하거나 Peak 를 놓치면 그 보정 이동은 되돌린다) —
/// 이후 ⑦⁺/⑪⁺는 cross 축만 움직이므로 진행축 잔차가 그대로 작업물 좌표계 점1/점2 의 오차가 된다
/// (예: Peak1 −158mm 측정 → 100mm 클램프 이동 → 잔차 −58mm 가 원점 밀림으로 남던 문제).
/// </summary>
public class PeakCenteringStep : ISequenceStep
{
    private readonly int _peakId;
    private readonly WeldTrackingService _weld;
    private readonly CameraService _camera;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<PeakCenteringStep> _logger;

    /// <summary>1회 이동 클램프(mm). 부호 오설정·측정 폭주 시 가드.</summary>
    private const double MaxCenteringMoveMm = 100.0;

    /// <summary>이 값 미만이면 이동을 생략한다(mm).</summary>
    private const double SkipMoveMm = 0.5;

    /// <summary>모션 후 안정화 대기(ms).</summary>
    private const int SettleMs = 500;

    /// <summary>센터링 후 남아도 되는 진행축 잔차(mm). 넘으면 보정 이동을 더 한다(그래도 남으면 경고 후 진행, 0=보정 안 함).
    /// 잔차는 그대로 작업물 좌표계 점1/점2 의 진행축 오차가 된다(⑦⁺/⑪⁺는 cross 축만 움직인다).</summary>
    public const string ResidualMaxKey = "Sequence.Peak.CenterResidualMaxMm";
    public const double DefaultResidualMaxMm = 5.0;

    /// <summary>잔차 보정 이동 최대 횟수.</summary>
    private const int MaxCorrections = 2;

    /// <param name="peakId">1 또는 2. DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public PeakCenteringStep(
        int peakId,
        WeldTrackingService weld,
        CameraService camera,
        CobotService cobot,
        ParameterService param,
        ILogger<PeakCenteringStep> logger)
    {
        _peakId = peakId;
        _weld = weld;
        _camera = camera;
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => $"peak{_peakId}Center";
    public string DisplayName => $"Peak{_peakId} 센터링";
    public int DefaultOrder => _peakId == 1 ? 600 : 1000;

    public StepValidation Validate(SequenceContext context)
    {
        var common = WeldSequenceSupport.ValidateCommon(_cobot, _camera, _weld, requireDetector: false);
        if (!common.IsValid) return common;

        if (!context.Bag.ContainsKey(WeldSequenceSupport.OffsetMmBagKey(_peakId)))
            return StepValidation.Fail(
                $"Peak{_peakId} 측정값 없음 — 'Peak{_peakId} 찾기' 단계를 먼저 실행하세요.");

        return StepValidation.Ok();
    }

    public Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
        => BundleTeach.RunWithPoint2FallbackAsync(_peakId, context, _cobot, _param, _logger, ct, RunAsync);

    private async Task<StepResult> RunAsync(SequenceContext context, CancellationToken ct)
    {
        if (context.Bag[WeldSequenceSupport.OffsetMmBagKey(_peakId)] is not double initialOffsetMm)
            return StepResult.Fail($"Peak{_peakId} 측정값을 읽을 수 없습니다 — 앞 단계를 다시 실행하세요.");

        var (progressAxis, axisSrc) = await WeldSequenceSupport.ApplyProgressAxisAsync(_weld, context, _cobot, _param, ct);
        var axisKey = WeldSequenceSupport.ProgressToolAxisKey(progressAxis);

        // ④와 동일 규약: d = 타깃 − 센터(양수 = 진행축 + 방향)를 부호 반전 없이 그대로 싣는다.
        // 검사캠 오프셋은 여기서 다루지 않는다 — depth 시야 중심에서 센터링·검증을 끝내야
        // 재측정이 가능하고 ⑦⑪ 측정도 시야 중앙에서 이뤄진다. 오프셋 시프트는 ⑦⁺⑪⁺ 검증 후 수행.
        var moveMm = initialOffsetMm;

        if (Math.Abs(moveMm) < SkipMoveMm)
            return StepResult.Ok(
                $"Peak{_peakId} 이미 센터 근방 ({initialOffsetMm:+0.0;-0.0}mm) — 이동 생략.");

        var clamped = Math.Clamp(moveMm, -MaxCenteringMoveMm, MaxCenteringMoveMm);
        if (Math.Abs(clamped - moveMm) > 1e-6)
            _logger.LogWarning(
                "Peak{Id} 센터링 이동량 {Raw:0.0}mm 가 한계 ±{Max:0}mm 로 클램프됨.",
                _peakId, moveMm, MaxCenteringMoveMm);
        var (toolAxis, axisFromParam) = await WeldSequenceSupport.GetProgressToolAxisAsync(_param, progressAxis);
        if (!axisFromParam)
            _logger.LogWarning(
                "'{Key}' 매핑 파라미터 없음/범위 밖 — 기본값 사용. 카메라 페이지에서 매핑을 설정·저장하세요.",
                axisKey);

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, toolAxis, clamped);

        _logger.LogInformation(
            "Peak{Id} 센터링: 진행축={Prog}({Src}), 툴{Axis} {Off:+0.0;-0.0}mm (측정 {Meas:+0.0;-0.0}mm)",
            _peakId, progressAxis, axisSrc, FlatSurfaceCenteringService.AxisName(toolAxis), clamped, initialOffsetMm);

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"센터링 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);

        // ── 재측정(모션 없음) — ImageXAxis 매핑 검증용 ─────────────────
        var (roi, _) = await WeldSequenceSupport.GetPeakRoiAsync(_param, _camera);
        if (roi is null)
            return StepResult.Fail("재측정용 Peak ROI 를 만들 수 없습니다.");

        // 이동 후 같은 Peak 가 있을 예상 이격 = 측정 − 이동. ROI 가 Peak 간격보다 넓으면 옆 Peak 도 들어오므로
        // 재측정은 그 예상 위치에 가장 가까운 Peak 를 고르게 한다(실기 2026-11-06 19:18 PU: −158.6 측정, −100 이동(클램프) 후
        // 기대 −58.6 인데 카메라에 더 가까운 옆 Peak +218.8 을 잡아 '이격 증가'로 오판).
        var expectedMm = initialOffsetMm - clamped;
        var mmPerPx = context.Bag.TryGetValue(WeldSequenceSupport.PeakFindBagKey(_peakId), out var r1Obj)
                      && r1Obj is PeakFindResult { OffsetPx: var px1 } && Math.Abs(px1) > 1.0
            ? initialOffsetMm / px1
            : 0.0;
        var expectedPx = mmPerPx > 0 ? expectedMm / mmPerPx : 0.0;

        var r2 = await _weld.FindPeakAsync(roi, ct, expectedPx);
        if (!r2.Found)
            return StepResult.Fail(
                $"이동 후 Peak{_peakId} 미검출 — '{axisKey}' 매핑" +
                $"(현재 툴{FlatSurfaceCenteringService.AxisName(toolAxis)}) 또는 ROI 를 확인하세요. ({r2.Message})");

        var (residualMm, scaleNote) = WeldSequenceSupport.ResolveOffsetMm(r2, _weld, _camera);

        // 축 부호가 틀렸으면 Peak 는 측정 + 이동 쪽으로 간다. 재측정이 그쪽 예상에 더 가까울 때만 부호 오설정으로 본다.
        var wrongSignMm = initialOffsetMm + clamped;
        _logger.LogInformation(
            "Peak{Id} 재측정: {Res:+0.0;-0.0}mm (예상 {Exp:+0.0;-0.0}mm, 부호 반대면 {Wrong:+0.0;-0.0}mm, ROI 내 Peak {N}개)",
            _peakId, residualMm, expectedMm, wrongSignMm, r2.PeakCount);
        if (Math.Abs(residualMm - wrongSignMm) < Math.Abs(residualMm - expectedMm))
            return StepResult.Fail(
                $"Peak 가 반대로 움직였습니다 (이동 전 {initialOffsetMm:+0.0;-0.0} → {residualMm:+0.0;-0.0}mm, " +
                $"예상 {expectedMm:+0.0;-0.0}mm) — '{axisKey}' 를 현재 툴{FlatSurfaceCenteringService.AxisName(toolAxis)} 의 " +
                "반대 부호 축으로 설정해 보세요 (카메라 페이지).");

        // ── 잔차 보정 — 1회 이동(클램프·개루프 오차)으로 남은 진행축 잔차가 허용치를 넘으면 추가 이동 ──
        // 잔차 때문에 검사를 실패시키지는 않는다: 보정이 안 되면(미검출·발산) 직전 위치로 되돌리고 경고만 남긴 채 진행한다.
        var residualMax = await _param.GetDoubleAsync(ResidualMaxKey) ?? DefaultResidualMaxMm;
        var corrections = 0;
        string? residualWarn = null;
        while (residualMax > 0 && Math.Abs(residualMm) > residualMax)
        {
            if (corrections >= MaxCorrections)
            {
                residualWarn = $"잔차 {residualMm:+0.0;-0.0}mm 가 허용 ±{residualMax:0.#}mm 초과(보정 {corrections}회 후) — 그대로 진행";
                break;
            }

            var corr = Math.Clamp(residualMm, -MaxCenteringMoveMm, MaxCenteringMoveMm);
            var corrOffset = new double[6];
            FlatSurfaceCenteringService.ApplyAxis(corrOffset, toolAxis, corr);
            var here = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            _logger.LogInformation("Peak{Id} 잔차 보정 {N}회차: 툴{Axis} {Off:+0.0;-0.0}mm (잔차 {Res:+0.0;-0.0}mm)",
                _peakId, corrections + 1, FlatSurfaceCenteringService.AxisName(toolAxis), corr, residualMm);
            var rcCorr = await _cobot.Rpc.MoveByToolOffsetAsync(here, user: 0, corrOffset,
                tool: context.Tool, vel: context.Velocity, ct: ct);
            if (rcCorr != 0)
                return StepResult.Fail($"센터링 잔차 보정 이동 실패 (rc={rcCorr}){FairinoErrorCodes.Suffix(rcCorr)}.");
            await Task.Delay(SettleMs, ct);
            corrections++;

            var expectedCorrPx = mmPerPx > 0 ? (residualMm - corr) / mmPerPx : 0.0;
            var r3 = await _weld.FindPeakAsync(roi, ct, expectedCorrPx);
            double? next = r3.Found ? WeldSequenceSupport.ResolveOffsetMm(r3, _weld, _camera).Item1 : null;
            if (next is not { } n || Math.Abs(n) >= Math.Abs(residualMm))
            {
                // 보정이 오히려 나빠졌거나 Peak 를 놓쳤다 — 보정 이동을 되돌려 직전(더 나은) 위치에서 진행한다.
                var back = new double[6];
                FlatSurfaceCenteringService.ApplyAxis(back, toolAxis, -corr);
                var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
                var rcBack = await _cobot.Rpc.MoveByToolOffsetAsync(cur, user: 0, back,
                    tool: context.Tool, vel: context.Velocity, ct: ct);
                if (rcBack != 0)
                    return StepResult.Fail($"센터링 잔차 보정 되돌림 이동 실패 (rc={rcBack}){FairinoErrorCodes.Suffix(rcBack)}.");
                await Task.Delay(SettleMs, ct);
                residualWarn = next is null
                    ? $"잔차 보정 후 Peak 미검출 — 보정 이동을 되돌리고 잔차 {residualMm:+0.0;-0.0}mm 로 진행"
                    : $"잔차 보정이 수렴하지 않음({residualMm:+0.0;-0.0}→{next:+0.0;-0.0}mm) — 되돌리고 잔차 {residualMm:+0.0;-0.0}mm 로 진행";
                break;
            }
            residualMm = n;
            r2 = r3;
        }
        if (residualWarn is not null)
            _logger.LogWarning("Peak{Id} 센터링: {Warn} — 작업물 좌표계 원점이 진행축으로 그만큼 밀릴 수 있습니다.", _peakId, residualWarn);

        // 후속 단계가 최신 측정값을 쓰도록 갱신.
        context.Bag[WeldSequenceSupport.PeakFindBagKey(_peakId)] = r2;
        context.Bag[WeldSequenceSupport.OffsetMmBagKey(_peakId)] = residualMm;

        return StepResult.Ok(
            $"Peak{_peakId} 센터링 완료 — {clamped:+0.0;-0.0}mm 이동, " +
            $"잔차 {residualMm:+0.0;-0.0}mm (이동 전 {initialOffsetMm:+0.0;-0.0}mm, 예상 {expectedMm:+0.0;-0.0}mm" +
            (corrections > 0 ? $", 잔차 보정 {corrections}회" : "") + $"), {scaleNote}" +
            (residualWarn is not null ? $" ⚠ {residualWarn}" : ""));
    }
}
