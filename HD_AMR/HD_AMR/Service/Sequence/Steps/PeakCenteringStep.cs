using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑥/⑩ Peak 센터링 — 앞 단계가 잰 이격거리만큼 BASE X 로 <b>1회</b> 이동해 Peak 를 FOV 센터로 옮긴다.
///
/// 개루프 1회 이동 후 재측정을 <b>한 번만</b> 수행한다(모션 없음). 반복 수렴 루프는 두지 않는다.
/// 센터링 정밀도 자체는 중요하지 않다 — 후속 비드 찾기가 그 프레임에서 Peak 를 다시 재기 때문이다.
/// 재측정의 실제 목적은 <see cref="WeldSequenceSupport.XSignKey"/> 오설정을 조기에 잡아내는 것이다.
/// 부호가 반대면 반대 방향으로 이동해 이격이 오히려 커지므로, 그때 명시적으로 실패시킨다.
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

        var xSign = await WeldSequenceSupport.GetSignAsync(_param, WeldSequenceSupport.XSignKey);

        // Peak 가 화면 오른쪽(+offset)이면 카메라를 오른쪽으로 보내야 하므로 부호를 뒤집는다.
        // 영상 +X 가 BASE 어느 방향인지는 카메라 장착에 달렸으므로 XSign 을 곱한다.
        var moveMm = -initialOffsetMm * xSign;

        if (Math.Abs(moveMm) < SkipMoveMm)
            return StepResult.Ok(
                $"Peak{_peakId} 이미 센터 근방 ({initialOffsetMm:+0.0;-0.0}mm) — 이동 생략.");

        var clamped = Math.Clamp(moveMm, -MaxCenteringMoveMm, MaxCenteringMoveMm);
        if (Math.Abs(clamped - moveMm) > 1e-6)
            _logger.LogWarning(
                "⑥/⑩ Peak{Id} 센터링 이동량 {Raw:0.0}mm 가 한계 ±{Max:0}mm 로 클램프됨.",
                _peakId, moveMm, MaxCenteringMoveMm);

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = new[] { clamped, 0.0, 0.0, 0.0, 0.0, 0.0 };

        _logger.LogInformation(
            "⑥/⑩ Peak{Id} 센터링: offset={Off:+0.0;-0.0}mm (측정 {Meas:+0.0;-0.0}mm, XSign={Sign:+0;-0})",
            _peakId, clamped, initialOffsetMm, xSign);

        var rc = await _cobot.Rpc.MoveByOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"센터링 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);

        // ── 재측정(모션 없음) — XSign 검증용 ──────────────────────────
        var (roi, _) = await WeldSequenceSupport.GetRoiAsync(_param, _camera);
        if (roi is null)
            return StepResult.Fail("재측정용 깊이 ROI 를 만들 수 없습니다.");

        var r2 = await _weld.FindPeakAsync(roi, ct);
        if (!r2.Found)
            return StepResult.Fail(
                $"이동 후 Peak{_peakId} 미검출 — '{WeldSequenceSupport.XSignKey}'(현재 {xSign:+0;-0}) 부호 " +
                $"또는 ROI 폭을 확인하세요. ({r2.Message})");

        var (residualMm, scaleNote) = WeldSequenceSupport.ResolveOffsetMm(r2, _weld, _camera);

        if (Math.Abs(residualMm) >= Math.Abs(initialOffsetMm))
            return StepResult.Fail(
                $"이격거리가 증가했습니다 ({initialOffsetMm:+0.0;-0.0} → {residualMm:+0.0;-0.0}mm) — " +
                $"'{WeldSequenceSupport.XSignKey}' 를 현재 {xSign:+0;-0} 의 반대 부호로 설정해 보세요.");

        // 후속 단계가 최신 측정값을 쓰도록 갱신.
        context.Bag[WeldSequenceSupport.PeakFindBagKey(_peakId)] = r2;
        context.Bag[WeldSequenceSupport.OffsetMmBagKey(_peakId)] = residualMm;

        return StepResult.Ok(
            $"Peak{_peakId} 센터링 완료 — {clamped:+0.0;-0.0}mm 이동, " +
            $"잔차 {residualMm:+0.0;-0.0}mm (이동 전 {initialOffsetMm:+0.0;-0.0}mm), {scaleNote}");
    }
}
