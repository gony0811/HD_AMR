using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑦⁺/⑪⁺ Bead 센터링 — ⑦⑪이 잰 비드 위치오차 d(진행축과 수직 = cross 축, mm)만큼
/// cross 툴축(진행축이 영상 가로면 ImageY, 세로면 ImageX 매핑)으로 <b>1회</b> 이동해
/// 비드 라인을 depth 영상 중심에 맞춘 뒤, <b>검증까지 끝나면</b> 검사 카메라 설치 오프셋
/// (<see cref="WeldSequenceSupport.InspectCamOffsetXKey"/>)만큼 2D 개루프 시프트해 타깃을
/// 검사 카메라 아래로 옮긴다. 시프트를 센터링 수식에 합성하면 오프셋이 클 때 타깃이 depth 시야를
/// 벗어나 재측정 검증이 불가능하므로, ④의 레이저 중심 시프트와 동일하게 "검증 후 시프트"로 분리한다.
/// 시프트 상태는 <see cref="WeldSequenceSupport.InspectShiftedBagKey"/> 로 ⑧에 전달되어 역보정된다.
///
/// ⑫ 각도 산출과의 정합(비대칭 정책의 이유):
///   - <b>peakId=1</b>: 이동 후(시프트 전) 재측정해 M1 을 잔차(≈0)로 갱신한다. 센터링 이동으로 FOV
///     기준선이 d1 만큼 옮겨지므로, 갱신하지 않으면 ⑫의 (d2−d1)/pitch 에서 d1 이 이중 반영된다.
///   - <b>peakId=2</b>: 이동만 하고 재측정하지 않는다. CapturePeak 재측정은 M2 를 잔차≈0 으로 덮고
///     각도를 자동 재계산해 θ≈0 으로 오염시키기 때문 — ⑫(order 1200)는 이동 전 저장된 M2 를 쓴다.
///   - 검사캠 시프트는 측정이 모두 끝난 뒤(⑦⁺는 재측정 후, ⑧이 역보정) 수행되므로 각도에 개입하지 않는다.
/// </summary>
public class BeadCenteringStep : ISequenceStep
{
    private readonly int _peakId;
    private readonly WeldTrackingService _weld;
    private readonly CameraService _camera;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<BeadCenteringStep> _logger;

    /// <summary>1회 이동 클램프(mm). 매핑 오설정·측정 폭주 가드 — ⑥과 동일 관례.</summary>
    private const double MaxCenteringMoveMm = 100.0;

    /// <summary>이 값 미만이면 이동을 생략한다(mm).</summary>
    private const double SkipMoveMm = 0.5;

    /// <summary>모션 후 안정화 대기(ms).</summary>
    private const int SettleMs = 500;

    /// <param name="peakId">1 또는 2. DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public BeadCenteringStep(
        int peakId,
        WeldTrackingService weld,
        CameraService camera,
        CobotService cobot,
        ParameterService param,
        ILogger<BeadCenteringStep> logger)
    {
        _peakId = peakId;
        _weld = weld;
        _camera = camera;
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => $"bead{_peakId}Center";
    public string DisplayName => $"Bead{_peakId} 센터링";
    public int DefaultOrder => _peakId == 1 ? 750 : 1150;

    public StepValidation Validate(SequenceContext context)
    {
        // 재측정(peakId=1)에만 검출기가 필요하다.
        var common = WeldSequenceSupport.ValidateCommon(_cobot, _camera, _weld, requireDetector: _peakId == 1);
        if (!common.IsValid) return common;

        if (!context.Bag.ContainsKey(WeldSequenceSupport.BeadDMmBagKey(_peakId)))
            return StepValidation.Fail(
                $"Bead{_peakId} 측정값 없음 — {(_peakId == 1 ? "⑦" : "⑪")} 단계를 먼저 실행하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (context.Bag[WeldSequenceSupport.BeadDMmBagKey(_peakId)] is not double initialDMm)
            return StepResult.Fail($"Bead{_peakId} 측정값을 읽을 수 없습니다 — 앞 단계를 다시 실행하세요.");

        var progressAxis = WeldSequenceSupport.ApplyProgressAxis(_weld, context);
        var axisKey = WeldSequenceSupport.CrossToolAxisKey(progressAxis);

        // ④와 동일 규약: d = 타깃 − 센터(양수 = cross 축 + 방향)를 부호 반전 없이 그대로 싣는다.
        // 검사캠 오프셋은 여기 합성하지 않는다 — depth 시야 중심에서 검증을 끝낸 뒤 별도 시프트.
        var moveMm = initialDMm;

        if (Math.Abs(moveMm) < SkipMoveMm)
        {
            var (shiftOk, shiftNote) = await ShiftToInspectCamAsync(context, ct);
            return shiftOk
                ? StepResult.Ok($"Bead{_peakId} 이미 센터 근방 (d={initialDMm:+0.0;-0.0}mm) — 이동 생략.{shiftNote}")
                : StepResult.Fail(shiftNote);
        }

        var clamped = Math.Clamp(moveMm, -MaxCenteringMoveMm, MaxCenteringMoveMm);
        if (Math.Abs(clamped - moveMm) > 1e-6)
            _logger.LogWarning(
                "⑦⁺/⑪⁺ Bead{Id} 센터링 이동량 {Raw:0.0}mm 가 한계 ±{Max:0}mm 로 클램프됨.",
                _peakId, moveMm, MaxCenteringMoveMm);
        var (toolAxis, axisFromParam) = await WeldSequenceSupport.GetCrossToolAxisAsync(_param, progressAxis);
        if (!axisFromParam)
            _logger.LogWarning(
                "'{Key}' 매핑 파라미터 없음/범위 밖 — 기본값 사용. 카메라 페이지에서 매핑을 설정·저장하세요.",
                axisKey);

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, toolAxis, clamped);

        _logger.LogInformation(
            "⑦⁺/⑪⁺ Bead{Id} 센터링: 진행축={Prog}, cross 툴{Axis} {Off:+0.0;-0.0}mm (측정 d={Meas:+0.0;-0.0}mm)",
            _peakId, progressAxis, FlatSurfaceCenteringService.AxisName(toolAxis), clamped, initialDMm);

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"비드 센터링 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);

        if (_peakId != 1)
        {
            // ⑫ 각도 정합을 위해 재측정하지 않는다 — 검사캠 시프트만 적용하고 종료.
            var (ok2, note2) = await ShiftToInspectCamAsync(context, ct);
            return ok2
                ? StepResult.Ok(
                    $"Bead2 센터링 완료 — 툴{FlatSurfaceCenteringService.AxisName(toolAxis)} {clamped:+0.0;-0.0}mm 이동 " +
                    $"(⑫ 각도 정합을 위해 재측정하지 않음).{note2}")
                : StepResult.Fail(note2);
        }

        // ── peakId=1: 재측정으로 M1 을 잔차로 갱신 (⑫ 정합) + 매핑 검증 ──
        var (roi, _) = await WeldSequenceSupport.GetRoiAsync(_param, _camera);
        if (roi is null)
            return StepResult.Fail("재측정용 깊이 ROI 를 만들 수 없습니다.");

        var (m2, detect2) = await _weld.CapturePeakAsync(_peakId, roi, roi, ct);
        if (detect2 is null || !detect2.Success || m2 is null)
            return StepResult.Fail(
                $"이동 후 Bead{_peakId} 재검출 실패 — '{axisKey}' 매핑" +
                $"(현재 툴{FlatSurfaceCenteringService.AxisName(toolAxis)}) 또는 ROI 를 확인하세요." +
                (detect2 is null ? "" : $" ({detect2.Message})"));

        var residualMm = _weld.DMm(m2);

        if (Math.Abs(residualMm) >= Math.Abs(initialDMm))
            return StepResult.Fail(
                $"비드 이격이 증가했습니다 (d={initialDMm:+0.0;-0.0} → {residualMm:+0.0;-0.0}mm) — " +
                $"'{axisKey}' 를 현재 툴{FlatSurfaceCenteringService.AxisName(toolAxis)} 의 " +
                "반대 부호 축으로 설정해 보세요 (카메라 페이지).");

        context.Bag[WeldSequenceSupport.BeadDMmBagKey(_peakId)] = residualMm;

        // 측정·검증이 모두 끝났으므로 검사캠 아래로 시프트한다.
        var (shiftedOk, shiftedNote) = await ShiftToInspectCamAsync(context, ct);
        if (!shiftedOk)
            return StepResult.Fail(shiftedNote);

        return StepResult.Ok(
            $"Bead{_peakId} 센터링 완료 — {clamped:+0.0;-0.0}mm 이동, " +
            $"잔차 d={residualMm:+0.0;-0.0}mm (이동 전 {initialDMm:+0.0;-0.0}mm), M1 갱신.{shiftedNote}");
    }

    /// <summary>
    /// 검사 카메라 설치 오프셋만큼 2D 개루프 시프트 — 카메라가 영상 (−X, −Y) 방향으로 움직여
    /// 타깃이 화면상 (+X, +Y) 위치(= 검사 카메라 시야 중심)에 온다. 오프셋이 0 이면 아무것도 안 한다.
    /// 성공 시 <see cref="WeldSequenceSupport.InspectShiftedBagKey"/> 를 세워 ⑧이 역보정하게 한다.
    /// </summary>
    private async Task<(bool Ok, string Note)> ShiftToInspectCamAsync(SequenceContext context, CancellationToken ct)
    {
        var (offX, offY) = await WeldSequenceSupport.GetInspectCamOffsetAsync(_param);
        if (Math.Abs(offX) < SkipMoveMm && Math.Abs(offY) < SkipMoveMm)
            return (true, "");

        var (axX, _) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
        var (axY, _) = await WeldSequenceSupport.GetImageYAxisAsync(_param);

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, axX, -offX);
        FlatSurfaceCenteringService.ApplyAxis(offset, axY, -offY);

        _logger.LogInformation(
            "⑦⁺/⑪⁺ Bead{Id} 검사캠 시프트: 영상 ({X:+0.0;-0.0}, {Y:+0.0;-0.0})mm 역방향 이동 → 툴 오프셋 [{Ox:0.#}, {Oy:0.#}, {Oz:0.#}]",
            _peakId, offX, offY, offset[0], offset[1], offset[2]);

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return (false, $"검사캠 시프트 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);
        context.Bag[WeldSequenceSupport.InspectShiftedBagKey] = true;

        return (true, $" 검사캠 시프트 적용(영상 {offX:+0.0;-0.0}/{offY:+0.0;-0.0}mm).");
    }
}
