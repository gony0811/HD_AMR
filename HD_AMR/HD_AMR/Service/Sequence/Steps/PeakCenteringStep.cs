using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑥/⑩ Peak 센터링 — 앞 단계가 잰 이격거리만큼 <b>툴 프레임</b>으로 <b>1회</b> 이동해 Peak 를 FOV 센터로 옮긴다.
///
/// 이동 축은 진행축(② 검사방향에서 자동 결정 — 영상 가로면 <see cref="WeldSequenceSupport.ImageXAxisKey"/>,
/// 세로면 <see cref="WeldSequenceSupport.ImageYAxisKey"/>)의 툴축 매핑(부호 포함)을 쓴다 —
/// 과거 BASE X 하드코딩은 헤드 방향에 따라 광축(툴 ±Z) 성분이 섞이는 좌표계 오류였음(③의 BASE −Y 버그와 동일 계열).
///
/// 개루프 1회 이동 후 재측정을 <b>한 번만</b> 수행한다(모션 없음). 반복 수렴 루프는 두지 않는다.
/// 센터링 정밀도 자체는 중요하지 않다 — 후속 비드 찾기가 그 프레임에서 Peak 를 다시 재기 때문이다.
/// 재측정의 실제 목적은 ImageXAxis 매핑 오설정을 조기에 잡아내는 것이다.
/// 축/부호가 틀리면 이격이 오히려 커지므로, 그때 명시적으로 실패시킨다.
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
                $"Peak{_peakId} 측정값 없음 — {(_peakId == 1 ? "⑤" : "⑨")} 단계를 먼저 실행하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (context.Bag[WeldSequenceSupport.OffsetMmBagKey(_peakId)] is not double initialOffsetMm)
            return StepResult.Fail($"Peak{_peakId} 측정값을 읽을 수 없습니다 — 앞 단계를 다시 실행하세요.");

        var progressAxis = WeldSequenceSupport.ApplyProgressAxis(_weld, context);
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
                "⑥/⑩ Peak{Id} 센터링 이동량 {Raw:0.0}mm 가 한계 ±{Max:0}mm 로 클램프됨.",
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
            "⑥/⑩ Peak{Id} 센터링: 진행축={Prog}, 툴{Axis} {Off:+0.0;-0.0}mm (측정 {Meas:+0.0;-0.0}mm)",
            _peakId, progressAxis, FlatSurfaceCenteringService.AxisName(toolAxis), clamped, initialOffsetMm);

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"센터링 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);

        // ── 재측정(모션 없음) — ImageXAxis 매핑 검증용 ─────────────────
        var (roi, _) = await WeldSequenceSupport.GetRoiAsync(_param, _camera);
        if (roi is null)
            return StepResult.Fail("재측정용 깊이 ROI 를 만들 수 없습니다.");

        var r2 = await _weld.FindPeakAsync(roi, ct);
        if (!r2.Found)
            return StepResult.Fail(
                $"이동 후 Peak{_peakId} 미검출 — '{axisKey}' 매핑" +
                $"(현재 툴{FlatSurfaceCenteringService.AxisName(toolAxis)}) 또는 ROI 를 확인하세요. ({r2.Message})");

        var (residualMm, scaleNote) = WeldSequenceSupport.ResolveOffsetMm(r2, _weld, _camera);

        if (Math.Abs(residualMm) >= Math.Abs(initialOffsetMm))
            return StepResult.Fail(
                $"이격거리가 증가했습니다 ({initialOffsetMm:+0.0;-0.0} → {residualMm:+0.0;-0.0}mm) — " +
                $"'{axisKey}' 를 현재 툴{FlatSurfaceCenteringService.AxisName(toolAxis)} 의 " +
                "반대 부호 축으로 설정해 보세요 (카메라 페이지).");

        // 후속 단계가 최신 측정값을 쓰도록 갱신.
        context.Bag[WeldSequenceSupport.PeakFindBagKey(_peakId)] = r2;
        context.Bag[WeldSequenceSupport.OffsetMmBagKey(_peakId)] = residualMm;

        return StepResult.Ok(
            $"Peak{_peakId} 센터링 완료 — {clamped:+0.0;-0.0}mm 이동, " +
            $"잔차 {residualMm:+0.0;-0.0}mm (이동 전 {initialOffsetMm:+0.0;-0.0}mm), {scaleNote}");
    }
}
