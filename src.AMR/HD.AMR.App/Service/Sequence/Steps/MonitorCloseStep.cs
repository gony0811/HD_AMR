using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑬ 모니터링 창 닫기 — 풀오토의 최종 스텝. <see cref="SequenceMonitorService.RequestClose"/> 로
/// 모니터 창(별도 서킷)에 닫기를 요청한다. 모션·측정 없음.
/// 정상 완주 시에만 도달하므로 "성공하면 창이 닫히고, 실패하면 남아서 원인 확인" 정책이 성립한다.
/// 세미오토로 이 스텝만 단독 실행해 창을 수동으로 닫을 수도 있다.
/// </summary>
public class MonitorCloseStep : ISequenceStep
{
    private readonly SequenceMonitorService _monitor;
    private readonly ILogger<MonitorCloseStep> _logger;

    public MonitorCloseStep(SequenceMonitorService monitor, ILogger<MonitorCloseStep> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    public string Key => "monitorClose";
    public string DisplayName => "모니터링 창 닫기";
    public int DefaultOrder => 1400;

    public StepValidation Validate(SequenceContext context) => StepValidation.Ok();

    public Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        _monitor.RequestClose();
        _logger.LogInformation("모니터링 창 닫기 요청.");
        return Task.FromResult(StepResult.Ok("모니터링 창 닫기 요청 완료."));
    }
}
