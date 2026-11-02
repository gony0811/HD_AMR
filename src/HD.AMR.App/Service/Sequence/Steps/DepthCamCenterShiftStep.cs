using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ④⁺⁺ Depth 카메라 센터 평행이동 — ④⁺ 레이저 WD 조정이 끝나면 TCP(검사캠 초점) 축이 ②가 겨눈
/// 용접선 시작점 위에 있지만, depth 카메라는 TCP 와 떨어져 장착돼 있어 용접라인이 depth 영상 중심에서
/// 벗어난다(2026-11 T면 L4-R06C09 에서는 FOV 밖 — ⑤ Peak·⑦ Bead 탐색 불가).
/// 그래서 면과 평행(툴 X/Y, 영상 평면)하게 검사캠 장착 오프셋(<see cref="WeldSequenceSupport.InspectCamOffsetXKey"/>)
/// 만큼 <b>영상 +오프셋 방향</b>으로 1회 이동해, TCP 아래에 있던 지점을 depth 영상 중심으로 가져온다.
///
/// ⑦⁺⑪⁺ 검사캠 시프트(영상 −오프셋)의 정확한 역이동이다 — 같은 파라미터·같은 변환
/// (<see cref="WeldSequenceSupport.ImageShiftToToolOffset"/>)을 쓰므로 센터링 후 시프트가 타깃을 다시 TCP 아래로 보낸다.
/// Z(WD)·회전(평행도)은 건드리지 않는다. 오프셋이 0 이면 이동 없이 통과한다.
/// </summary>
public class DepthCamCenterShiftStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<DepthCamCenterShiftStep> _logger;

    public const string StepKey = "depthCamCenterShift";

    /// <summary>이 값 미만의 성분은 무시한다(mm) — ⑦⁺ 시프트와 동일.</summary>
    private const double SkipMoveMm = 0.5;

    /// <summary>성분별 절대 한계(mm). 초과 시 이동하지 않고 실패 — 파라미터 오입력 가드.</summary>
    private const double MaxShiftMm = 300.0;

    /// <summary>모션 후 안정화 대기(ms).</summary>
    private const int SettleMs = 500;

    public DepthCamCenterShiftStep(CobotService cobot, ParameterService param, ILogger<DepthCamCenterShiftStep> logger)
    {
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => StepKey;
    public string DisplayName => "Depth 카메라 센터 평행이동";
    public int DefaultOrder => 470;   // ④⁺ 레이저 WD(450) 직후, ⑤ Peak1 찾기(500) 이전

    public StepValidation Validate(SequenceContext context)
        // 순수 모션 단계 — 카메라/검출기 조건은 필요 없다.
        => _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var (offX, offY) = await WeldSequenceSupport.GetInspectCamOffsetAsync(_param);
        if (Math.Abs(offX) < SkipMoveMm && Math.Abs(offY) < SkipMoveMm)
            return StepResult.Ok(
                "검사캠 오프셋 0 — 이동 생략 (시퀀스 페이지 '검사캠 X/Y' 미설정이면 용접라인이 depth 중심에 오지 않을 수 있음).");

        if (Math.Abs(offX) > MaxShiftMm || Math.Abs(offY) > MaxShiftMm)
            return StepResult.Fail(
                $"검사캠 오프셋 (X{offX:+0.0;-0.0}, Y{offY:+0.0;-0.0})mm 가 한계 ±{MaxShiftMm:0}mm 초과 — " +
                "시퀀스 페이지 '검사캠 X/Y' 를 확인하세요.");

        var (axX, fromParamX) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
        var (axY, fromParamY) = await WeldSequenceSupport.GetImageYAxisAsync(_param);
        if (!fromParamX || !fromParamY)
            _logger.LogWarning(
                "이미지→툴축 매핑 파라미터 없음/범위 밖 — 기본 +X/+Y 사용. 카메라 페이지에서 매핑을 설정·저장하세요.");

        // 영상 축이 광축(툴 Z)에 매핑돼 있으면 평행이동이 WD 를 바꾼다 — 매핑 오설정으로 보고 거부.
        if (IsDepthAxis(axX) || IsDepthAxis(axY))
            return StepResult.Fail(
                $"영상 축 매핑이 툴 Z 를 가리킵니다 (X→{FlatSurfaceCenteringService.AxisName(axX)}, " +
                $"Y→{FlatSurfaceCenteringService.AxisName(axY)}) — 면과 평행 이동 불가. 카메라 페이지 매핑을 확인하세요.");

        var offset = WeldSequenceSupport.ImageShiftToToolOffset(axX, axY, offX, offY);

        _logger.LogInformation(
            "[Depth 카메라 센터 평행이동] 영상 ({X:+0.0;-0.0}, {Y:+0.0;-0.0})mm → 툴 오프셋 [{Ox:0.#}, {Oy:0.#}, {Oz:0.#}] (Z·자세 유지)",
            offX, offY, offset[0], offset[1], offset[2]);
        context.Progress?.Invoke(
            $"Depth 카메라 센터 평행이동: 툴 X{offset[0]:+0.0;-0.0} / Y{offset[1]:+0.0;-0.0} mm");

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"Depth 카메라 센터 평행이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);

        return StepResult.Ok(
            $"TCP 아래 지점을 depth 영상 중심으로 평행이동 — 툴 X{offset[0]:+0.0;-0.0} / Y{offset[1]:+0.0;-0.0} mm " +
            $"(검사캠 오프셋 영상 {offX:+0.0;-0.0}/{offY:+0.0;-0.0}mm — Bead 센터링 검사캠 시프트의 역이동).");
    }

    private static bool IsDepthAxis(ToolAxisDir axis) => axis is ToolAxisDir.PlusZ or ToolAxisDir.MinusZ;
}
