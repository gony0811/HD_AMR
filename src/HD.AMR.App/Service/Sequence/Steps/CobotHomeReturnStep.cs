using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑲ 코봇 홈 복귀 — 검사가 끝난 코봇을 티칭된 홈 자세로 되돌린다(MoveJ).
///
/// 순서상 ⑳ 작업물 좌표계 0 복귀(<see cref="WObjResetStep"/>, 1300) <b>다음</b>이다. 활성 작업물
/// 프레임을 먼저 반납해야 홈 복귀가 베이스 기준으로 나가고, 다음 실행이나 조그가 잔류 프레임을
/// 만나지 않는다. 모니터 창 닫기(1400)보다는 앞이라 복귀 과정이 모니터에 남는다.
///
/// 관절 이동이라 TCP 경로는 호를 그린다 — 검사 자세에서 홈까지 사이에 구조물이 없어야 한다.
/// 이미 홈(관절 허용오차 이내)이면 움직이지 않고 통과한다.
///
/// 시퀀스가 <b>실패로 중단되면 이 스텝에는 도달하지 못한다</b>. 그 경우의 좌표계·공구 반납은
/// <see cref="SequenceService"/> 의 best-effort 복구가 맡되, 홈 복귀까지 하지는 않는다
/// (정지 직후 임의 이동은 위험하므로 사람이 판단할 몫).
/// </summary>
public class CobotHomeReturnStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ILogger<CobotHomeReturnStep> _logger;

    public CobotHomeReturnStep(CobotService cobot, ILogger<CobotHomeReturnStep> logger)
    {
        _cobot = cobot;
        _logger = logger;
    }

    public string Key => "cobotHome";
    public string DisplayName => "코봇 홈 복귀";
    public int DefaultOrder => 1350;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        return SequenceEntry.ValidateHome(context);
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var moved = await SequenceEntry.EnsureCobotAtHomeAsync(_cobot, context, ct);
        var msg = moved ? "코봇 홈 복귀 완료 (MoveJ)." : "코봇이 이미 홈 위치에 있습니다.";
        _logger.LogInformation("⑲ {Msg}", msg);
        context.Progress?.Invoke(msg);
        return StepResult.Ok(msg);
    }
}
