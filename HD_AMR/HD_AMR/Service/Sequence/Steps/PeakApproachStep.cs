using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑧ Peak2 이동 — Peak1 센터링 위치에서 pitch(공칭 370mm)만큼 진행축(② 검사방향에서 자동 결정 —
/// 영상 가로면 ImageX, 세로면 ImageY 툴축 매핑, ⑥⑩과 동일)으로 개루프 이동해
/// 인접 Peak 근처로 옮긴다 — 과거 BASE X 하드코딩은 헤드 방향에 따라 광축 성분이 섞이는 좌표계 오류였음.
/// 이동 방향은 <see cref="WeldSequenceSupport.PitchDirKey"/> 로 결정한다:
/// "Peak2 가 진행축 + 방향이면 +1, 반대면 −1"(설비 배치의 선택)로,
/// 카메라 장착 방향(영상→툴축 매핑)과는 독립된 미지수다.
/// </summary>
public class PeakApproachStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<PeakApproachStep> _logger;

    /// <summary>모션 후 안정화 대기(ms).</summary>
    private const int SettleMs = 500;

    public PeakApproachStep(CobotService cobot, ParameterService param, ILogger<PeakApproachStep> logger)
    {
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => "peak2Approach";
    public string DisplayName => "Peak2 이동 (pitch)";
    public int DefaultOrder => 800;

    public StepValidation Validate(SequenceContext context)
        // 순수 모션 단계 — 카메라/검출기 조건은 필요 없다.
        => _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var pitchMm = await WeldSequenceSupport.GetPitchMmAsync(_param);
        if (pitchMm <= 0)
            return StepResult.Fail($"'{WeldSequenceSupport.PitchMmKey}' 파라미터가 0 이하입니다.");

        var pitchDir = await WeldSequenceSupport.GetSignAsync(_param, WeldSequenceSupport.PitchDirKey);
        var moveMm = pitchMm * pitchDir;

        var progressAxis = WeldSequenceSupport.GetProgressAxis(context);
        var (toolAxis, axisFromParam) = await WeldSequenceSupport.GetProgressToolAxisAsync(_param, progressAxis);
        if (!axisFromParam)
            _logger.LogWarning(
                "'{Key}' 매핑 파라미터 없음/범위 밖 — 기본값 사용. 카메라 페이지에서 매핑을 설정·저장하세요.",
                WeldSequenceSupport.ProgressToolAxisKey(progressAxis));

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, toolAxis, moveMm);

        // ⑦⁺가 검사캠 오프셋 시프트를 적용한 상태면, 역보정(+off)을 pitch 이동에 합성해
        // ⑨⑪이 타깃을 depth 시야 중앙 근처에서 다시 측정할 수 있게 한다.
        var unshiftNote = "";
        if (context.Bag.TryGetValue(WeldSequenceSupport.InspectShiftedBagKey, out var shifted) && shifted is true)
        {
            var (offX, offY) = await WeldSequenceSupport.GetInspectCamOffsetAsync(_param);
            var (axX, _) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
            var (axY, _) = await WeldSequenceSupport.GetImageYAxisAsync(_param);
            FlatSurfaceCenteringService.ApplyAxis(offset, axX, offX);
            FlatSurfaceCenteringService.ApplyAxis(offset, axY, offY);
            context.Bag.Remove(WeldSequenceSupport.InspectShiftedBagKey);
            unshiftNote = $", 검사캠 시프트 역보정(영상 {offX:+0.0;-0.0}/{offY:+0.0;-0.0}mm)";
        }

        _logger.LogInformation(
            "⑧ Peak2 이동: 진행축={Prog}, 툴{Axis} {Move:+0.0;-0.0}mm (pitch={Pitch:0.#}mm, dir={Dir:+0;-0}{Unshift})",
            progressAxis, FlatSurfaceCenteringService.AxisName(toolAxis), moveMm, pitchMm, pitchDir, unshiftNote);

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"Peak2 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);

        return StepResult.Ok(
            $"Peak2 위치로 툴{FlatSurfaceCenteringService.AxisName(toolAxis)} {moveMm:+0.0;-0.0}mm 이동 완료 " +
            $"(pitch={pitchMm:0.#}mm, dir={pitchDir:+0;-0}{unshiftNote}).");
    }
}
