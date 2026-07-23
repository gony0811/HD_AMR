using HD_AMR.Data.Entities;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence;

/// <summary>
/// 시퀀스 실행 엔진. DI로 주입된 <see cref="ISequenceStep"/> 목록을 레지스트리로 관리하고,
/// 풀오토(RunAllAsync) 또는 세미오토(RunStepAsync) 모드로 실행한다.
/// 실패 시 즉시 중단. 상태 변경은 <see cref="StateChanged"/> 이벤트로 UI에 알린다.
/// </summary>
public class SequenceService
{
    private readonly ILogger<SequenceService> _logger;
    private readonly TeachingService _teachingService;
    private readonly CobotService _cobotService;

    /// <summary>등록된 전체 단계 (DefaultOrder 순).</summary>
    private readonly List<ISequenceStep> _steps;

    /// <summary>각 단계의 현재 상태.</summary>
    private readonly Dictionary<string, StepStatus> _stepStatuses = new();

    private CancellationTokenSource? _runCts;

    public SequenceService(
        IEnumerable<ISequenceStep> steps,
        TeachingService teachingService,
        CobotService cobotService,
        ILogger<SequenceService> logger)
    {
        _teachingService = teachingService;
        _cobotService = cobotService;
        _logger = logger;
        _steps = steps.OrderBy(s => s.DefaultOrder).ToList();

        foreach (var step in _steps)
            _stepStatuses[step.Key] = new StepStatus(step.Key, step.DisplayName, StepState.Pending);
    }

    /// <summary>상태 변경 시 발생. UI에서 구독하여 StateHasChanged 호출.</summary>
    public event Action? StateChanged;

    /// <summary>현재 실행 상태.</summary>
    public SequenceRunState RunState { get; private set; } = SequenceRunState.Idle;

    /// <summary>현재 실행 중인 단계 키. Idle이면 null.</summary>
    public string? CurrentStepKey { get; private set; }

    /// <summary>등록된 단계 목록 (순서대로).</summary>
    public IReadOnlyList<ISequenceStep> Steps => _steps;

    /// <summary>각 단계의 현재 상태 스냅샷.</summary>
    public IReadOnlyDictionary<string, StepStatus> StepStatuses => _stepStatuses;

    /// <summary>실행 중인지 여부.</summary>
    public bool IsBusy => RunState != SequenceRunState.Idle;

    /// <summary>
    /// 풀오토: 모든 활성 단계를 순서대로 실행. 실패 시 즉시 중단.
    /// </summary>
    public async Task<bool> RunAllAsync(SequenceContext context, CancellationToken externalCt = default)
    {
        if (IsBusy) return false;

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _runCts.Token;

        RunState = SequenceRunState.Running;
        ResetStatuses();
        await LoadPositionsAsync(context, ct);
        RaiseStateChanged();

        _logger.LogInformation("시퀀스 풀오토 시작 (단계 {Count}개, tool={Tool}, vel={Vel})",
            _steps.Count, context.Tool, context.Velocity);

        var allSuccess = true;

        foreach (var step in _steps)
        {
            if (ct.IsCancellationRequested) break;

            var result = await RunSingleStepAsync(step, context, ct);
            if (!result.Success)
            {
                allSuccess = false;
                break;
            }
        }

        RunState = SequenceRunState.Idle;
        CurrentStepKey = null;
        _runCts?.Dispose();
        _runCts = null;
        RaiseStateChanged();

        _logger.LogInformation("시퀀스 풀오토 종료 (성공={Success})", allSuccess);
        return allSuccess;
    }

    /// <summary>
    /// 세미오토: 특정 단계만 실행.
    /// </summary>
    public async Task<StepResult> RunStepAsync(string stepKey, SequenceContext context, CancellationToken externalCt = default)
    {
        if (IsBusy)
            return StepResult.Fail("이미 실행 중입니다.");

        var step = _steps.FirstOrDefault(s => s.Key == stepKey);
        if (step is null)
            return StepResult.Fail($"단계 '{stepKey}'를 찾을 수 없습니다.");

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _runCts.Token;

        RunState = SequenceRunState.Running;
        await LoadPositionsAsync(context, ct);
        RaiseStateChanged();

        var result = await RunSingleStepAsync(step, context, ct);

        RunState = SequenceRunState.Idle;
        CurrentStepKey = null;
        _runCts?.Dispose();
        _runCts = null;
        RaiseStateChanged();

        return result;
    }

    /// <summary>모든 단계 상태를 대기(Pending)로 초기화. 실행 중에는 무시한다.</summary>
    public void Reset()
    {
        if (IsBusy) return;
        ResetStatuses();
        RaiseStateChanged();
    }

    /// <summary>즉시 정지: 현재 실행을 취소하고 코봇 모션을 정지.</summary>
    public async Task StopAsync()
    {
        if (!IsBusy) return;

        RunState = SequenceRunState.Stopping;
        RaiseStateChanged();

        _runCts?.Cancel();

        try
        {
            await _cobotService.StopMotionImmediateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "시퀀스 정지 중 코봇 StopMotion 실패");
        }
    }

    // ── 내부 ─────────────────────────────────────────────────────────

    private async Task<StepResult> RunSingleStepAsync(ISequenceStep step, SequenceContext context, CancellationToken ct)
    {
        // 1) Validate
        var validation = step.Validate(context);
        if (!validation.IsValid)
        {
            var msg = validation.Message ?? "선행조건 미충족";
            UpdateStatus(step.Key, StepState.Failed, msg);
            _logger.LogWarning("단계 '{Step}' 검증 실패: {Msg}", step.Key, msg);
            return StepResult.Fail(msg);
        }

        // 2) Execute
        CurrentStepKey = step.Key;
        UpdateStatus(step.Key, StepState.Running);

        try
        {
            var result = await step.ExecuteAsync(context, ct);

            UpdateStatus(step.Key, result.Success ? StepState.Completed : StepState.Failed, result.Message);
            _logger.LogInformation("단계 '{Step}' {Result}: {Msg}",
                step.Key, result.Success ? "완료" : "실패", result.Message);

            return result;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(step.Key, StepState.Failed, "사용자 정지");
            _logger.LogInformation("단계 '{Step}' 사용자 정지", step.Key);
            return StepResult.Fail("사용자 정지");
        }
        catch (Exception ex)
        {
            var errMsg = $"실행 실패: {ex.Message}{StateErrSuffix()}";
            UpdateStatus(step.Key, StepState.Failed, errMsg);
            _logger.LogError(ex, "단계 '{Step}' 예외", step.Key);
            return StepResult.Fail(errMsg);
        }
    }

    private async Task LoadPositionsAsync(SequenceContext context, CancellationToken ct)
    {
        var list = await _teachingService.ListAsync(ct);
        context.Positions = list.ToDictionary(p => p.Key, p => p);
    }

    private void ResetStatuses()
    {
        foreach (var step in _steps)
            _stepStatuses[step.Key] = new StepStatus(step.Key, step.DisplayName, StepState.Pending);
    }

    private void UpdateStatus(string key, StepState state, string? message = null)
    {
        if (_stepStatuses.TryGetValue(key, out var existing))
            _stepStatuses[key] = existing with { State = state, Message = message };
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    private string StateErrSuffix()
    {
        var sc = _cobotService.State?.ErrorCode;
        return sc is not null and not 0 ? $" (상태코드={sc})" : "";
    }
}
