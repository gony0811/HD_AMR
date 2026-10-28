using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑲ 코봇 홈 복귀 — 검사가 끝난 코봇을 티칭된 홈 자세로 되돌린다.
///
/// 경로: ① 벽면 직선 후퇴(MoveL, 툴 Z+) → ② 작업 준비 위치 경유(MoveJ) → ③ 홈(MoveJ).
///
/// 검사 자세에서 바로 MoveJ 하면 관절 보간 호가 AMR 차체(높이 ≈900mm)를 쓸고 지나간다.
/// ① 이 벽에서 먼저 떨어지고, ② 가 차체 위로 올라온 뒤, ③ 이 홈으로 접어 넣는다.
///
/// 순서상 ⑳ 작업물 좌표계 0 복귀(<see cref="WObjResetStep"/>, 1300) <b>다음</b>이다.
///
/// 이미 홈(관절 허용오차 이내)이면 움직이지 않고 통과한다.
///
/// 시퀀스가 <b>실패로 중단되면 이 스텝에는 도달하지 못한다</b>. 그 경우의 좌표계·공구 반납은
/// <see cref="SequenceService"/> 의 best-effort 복구가 맡되, 홈 복귀까지 하지는 않는다.
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
        // 벽면 후퇴(MoveL) → 경유점(ready 또는 홈 2 → 홈 1.5, 현재 자세로 선택) → 홈(MoveJ).
        var parts = await SequenceEntry.ReturnHomeAsync(_cobot, context, _logger, ct);
        if (parts.Count == 0)
        {
            const string already = "코봇이 이미 홈 위치에 있습니다.";
            _logger.LogInformation("[코봇 홈 복귀] {Msg}", already);
            context.Progress?.Invoke(already);
            return StepResult.Ok(already);
        }
        var msg = $"코봇 {string.Join(" → ", parts)} 완료.";

        _logger.LogInformation("[코봇 홈 복귀] {Msg}", msg);
        context.Progress?.Invoke(msg);
        return StepResult.Ok(msg);
    }
}
