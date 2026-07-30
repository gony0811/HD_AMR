using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑱⁺ 작업물 좌표계 0 복귀 — 마지막 스텝. ⑱ 검사 수행이 user=작업물 좌표계 번호로 MoveL 하면서
/// 컨트롤러의 활성 작업물 좌표계가 남는 것을, 현재 포즈로의 <b>무이동 MoveL(user:0)</b> 로
/// 베이스(0)로 되돌린다. 로봇은 움직이지 않는다.
///
/// IK 우회: 활성 프레임이 wobj 인 상태에서는 GetInverseKin 이 base 포즈를 오해석해 112(작업영역 밖)를
/// 반환하는 순환이 생기므로, 현재 관절각을 jointPos 로 직접 넘겨 역기구학 호출 자체를 생략한다.
/// ⑱이 실패/정지로 중단되어 풀오토가 여기까지 오지 못한 경우에는 목록에서 이 스텝만 단독 실행하면 된다.
/// </summary>
public class WObjResetStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ILogger<WObjResetStep> _logger;

    public WObjResetStep(CobotService cobot, ILogger<WObjResetStep> logger)
    {
        _cobot = cobot;
        _logger = logger;
    }

    public string Key => "wobjReset";
    public string DisplayName => "작업물 좌표계 0 복귀";
    public int DefaultOrder => 1300;

    public StepValidation Validate(SequenceContext context)
        => _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        // 현재 포즈로의 무이동 MoveL(user:0) — 이동 없이 활성 프레임만 0 으로.
        // 활성 프레임이 wobj 인 상태에서는 GetInverseKin 이 112 를 내므로(순환),
        // 현재 관절각을 jointPos 로 직접 넘겨 IK 를 우회한다.
        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        double[] joints;
        try
        {
            joints = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
        }
        catch (InvalidOperationException ex)
        {
            return StepResult.Fail($"관절각 조회 실패 — {ex.Message}");
        }

        var rc = await _cobot.Rpc.MoveLAsync(cur, jointPos: joints, tool: context.Tool, user: 0,
            vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail(
                $"작업물 좌표계 0 복귀 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — 티치펜던트에서 확인하세요.");

        _logger.LogInformation("⑱⁺ 활성 작업물 좌표계 0(베이스) 복귀 완료.");
        return StepResult.Ok("활성 작업물 좌표계 0(베이스) 복귀 완료.");
    }
}
