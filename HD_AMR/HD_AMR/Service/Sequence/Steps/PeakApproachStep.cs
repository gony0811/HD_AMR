using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑧ Peak2 이동 — Peak1 센터링 위치에서 pitch(공칭 370mm)만큼 BASE X 로 개루프 이동해
/// 인접 Peak 근처로 옮긴다. 이동 방향은 <see cref="WeldSequenceSupport.PitchDirKey"/> 로 결정한다.
///
/// 여기서 <see cref="WeldSequenceSupport.XSignKey"/> 를 곱하지 않는 것에 주의.
/// XSign 은 "영상 +X 가 BASE 어느 쪽인가"(카메라 장착)이고, PitchDir 은 "Peak2 가 어느 쪽인가"(설비 배치)로
/// 서로 독립된 미지수다. 곱해버리면 한쪽을 뒤집을 때 센터링이 같이 뒤집혀 발산한다.
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

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = new[] { moveMm, 0.0, 0.0, 0.0, 0.0, 0.0 };

        _logger.LogInformation(
            "⑧ Peak2 이동: BASE X {Move:+0.0;-0.0}mm (pitch={Pitch:0.#}mm, dir={Dir:+0;-0})",
            moveMm, pitchMm, pitchDir);

        var rc = await _cobot.Rpc.MoveByOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"Peak2 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);

        return StepResult.Ok(
            $"Peak2 위치로 {moveMm:+0.0;-0.0}mm 이동 완료 (pitch={pitchMm:0.#}mm, dir={pitchDir:+0;-0}).");
    }
}
