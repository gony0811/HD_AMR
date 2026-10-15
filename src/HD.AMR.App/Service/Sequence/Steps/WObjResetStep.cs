using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑱⁺ 작업물 좌표계 0 복귀 — <b>동작 없음(호환용)</b>.
///
/// 예전에는 ⑱ 검사 수행이 user=작업물 좌표계 번호로 MoveL 해 컨트롤러의 활성 작업물 좌표계가 남았으므로,
/// 현재 포즈로의 무이동 MoveL(user:0)로 베이스(0)로 되돌렸다. 지금은 내부 작업물 좌표계를 BASE 목표로
/// 직접 환산해 <b>모든 이동을 user 0 으로</b> 보내므로(<see cref="InspectionRunStep"/>·<see cref="WObjPointStep"/>)
/// 되돌릴 컨트롤러 상태가 없다. 그 무이동 MoveL 을 컨트롤러가 rc=74 로 거부해 검사를 다 마치고도 액션이
/// 실패로 보고된 일이 있어(실기 2026-10-15 천장 T-A0001 #2) 이동 지령을 없앴다. 실패 종료 시 좌표계·공구
/// 반납은 SequenceService 가 따로 한다.
/// 현장 레시피 StepKeysJson·드라이런 스텝 집합이 이 키를 참조하므로 스텝 자체는 남겨 둔다.
/// </summary>
public class WObjResetStep : ISequenceStep
{
    private readonly ILogger<WObjResetStep> _logger;

    public WObjResetStep(ILogger<WObjResetStep> logger)
    {
        _logger = logger;
    }

    public string Key => "wobjReset";
    public string DisplayName => "작업물 좌표계 0 복귀";
    public int DefaultOrder => 1300;

    public StepValidation Validate(SequenceContext context) => StepValidation.Ok();

    public Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        _logger.LogInformation("[작업물 좌표계 0 복귀] 컨트롤러 작업물 좌표계 미사용(모든 이동 user 0) — 생략.");
        return Task.FromResult(StepResult.Ok("컨트롤러 작업물 좌표계 미사용(모든 이동 user 0) — 생략."));
    }
}
