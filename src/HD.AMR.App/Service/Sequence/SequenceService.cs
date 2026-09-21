using HD.AMR.App.Data.Entities;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence;

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
    private readonly SequenceMonitorService _monitor;
    private readonly SequenceRunGate _gate;

    /// <summary>등록된 전체 단계 (DefaultOrder 순).</summary>
    private readonly List<ISequenceStep> _steps;

    /// <summary>각 단계의 현재 상태.</summary>
    private readonly Dictionary<string, StepStatus> _stepStatuses = new();

    private CancellationTokenSource? _runCts;

    public SequenceService(
        IEnumerable<ISequenceStep> steps,
        TeachingService teachingService,
        CobotService cobotService,
        SequenceMonitorService monitor,
        SequenceRunGate gate,
        ILogger<SequenceService> logger)
    {
        _teachingService = teachingService;
        _cobotService = cobotService;
        _monitor = monitor;
        _gate = gate;
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
        => (await RunSequenceAsync(context, stepKeys: null, externalCt)).Outcome == SequenceRunOutcome.Completed;

    /// <summary>
    /// 단계 부분 실행 — <paramref name="stepKeys"/>에 포함된 단계만 DefaultOrder 순으로 실행
    /// (null이면 전체). ACS(VDA5050) 경로용: Busy/Failed를 구분해 보고해야 하므로 결과를 구조체로 반환.
    /// </summary>
    public async Task<SequenceRunResult> RunSequenceAsync(
        SequenceContext context, IReadOnlyCollection<string>? stepKeys, CancellationToken externalCt = default)
    {
        if (IsBusy || !_gate.TryEnter())
            return new SequenceRunResult(SequenceRunOutcome.Busy, null, "다른 시퀀스가 실행 중입니다.");

        try
        {
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var ct = _runCts.Token;

            var selected = stepKeys is null
                ? _steps
                : _steps.Where(s => stepKeys.Contains(s.Key)).ToList();
            if (selected.Count == 0)
                return new SequenceRunResult(SequenceRunOutcome.Failed, null,
                    $"실행할 단계가 없습니다 (요청 키: {string.Join(",", stepKeys ?? Array.Empty<string>())}).");

            RunState = SequenceRunState.Running;
            ResetStatuses();
            await LoadPositionsAsync(context, ct);
            context.Progress = _monitor.Log;   // 스텝 진행 라인 → 모니터 창 콘솔
            _monitor.BeginRun();
            RaiseStateChanged();

            _logger.LogInformation("시퀀스 시작 (단계 {Count}/{Total}개, tool={Tool}, vel={Vel})",
                selected.Count, _steps.Count, context.Tool, context.Velocity);

            SequenceRunResult runResult = new(SequenceRunOutcome.Completed);

            foreach (var step in selected)
            {
                if (ct.IsCancellationRequested)
                {
                    runResult = new SequenceRunResult(SequenceRunOutcome.Failed, step.Key, "실행 취소됨");
                    break;
                }

                var result = await RunSingleStepAsync(step, context, ct);
                if (!result.Success)
                {
                    runResult = new SequenceRunResult(SequenceRunOutcome.Failed, step.Key, result.Message);
                    break;
                }
            }

            // 스텝 실패는 즉시 중단이라 wobjReset(1300)에 못 간다 — 활성 작업물 좌표계가 N 으로 남거나
            // 활성 공구가 context.Tool(통상 1)이 아닌 채 남으면 이후 조그/코봇 페이지가 프레임 불일치를 내므로
            // best-effort 로 반납한다(무변위 MoveJ, 공구도 복원). 취소(emergencyStop·임무 폐기)로 인한 실패는
            // 제외 — 정지 직후 모션 명령 금지. UI 풀오토와 ACS(오케스트레이터) 경로 모두 여기를 지난다.
            if (runResult.Outcome == SequenceRunOutcome.Failed && !ct.IsCancellationRequested)
                await TryResetActiveFrameAsync(context.Tool);

            RunState = SequenceRunState.Idle;
            CurrentStepKey = null;
            _runCts?.Dispose();
            _runCts = null;
            _monitor.EndRun(anyFailure: runResult.Outcome != SequenceRunOutcome.Completed);
            RaiseStateChanged();

            _logger.LogInformation("시퀀스 종료 (결과={Outcome}{Detail})",
                runResult.Outcome,
                runResult.FailedStepKey is null ? "" : $", 실패단계={runResult.FailedStepKey}");
            return runResult;
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>
    /// 세미오토: 특정 단계만 실행.
    /// </summary>
    public async Task<StepResult> RunStepAsync(string stepKey, SequenceContext context, CancellationToken externalCt = default)
    {
        if (IsBusy || !_gate.TryEnter())
            return StepResult.Fail("이미 실행 중입니다.");

        try
        {
            var step = _steps.FirstOrDefault(s => s.Key == stepKey);
            if (step is null)
                return StepResult.Fail($"단계 '{stepKey}'를 찾을 수 없습니다.");

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var ct = _runCts.Token;

            RunState = SequenceRunState.Running;
            await LoadPositionsAsync(context, ct);
            context.Progress = _monitor.Log;   // 스텝 진행 라인 → 모니터 창 콘솔
            _monitor.BeginRun();
            RaiseStateChanged();

            var result = await RunSingleStepAsync(step, context, ct);

            RunState = SequenceRunState.Idle;
            CurrentStepKey = null;
            _runCts?.Dispose();
            _runCts = null;
            _monitor.EndRun(anyFailure: !result.Success);
            RaiseStateChanged();

            return result;
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>활성 작업물 좌표계 0(베이스)·공구 <paramref name="tool"/> 복귀 — 실패 종료 경로의 best-effort 반납.
    /// 무변위 MoveJ(<see cref="Communication.FairinoRpcClient.ResetActiveFrameAsync"/>)라 로봇은 움직이지
    /// 않지만 모션 명령이므로 호출측이 취소 아님을 확인하고 부른다. 실패는 삼키고 로그만.</summary>
    private async Task TryResetActiveFrameAsync(int tool)
    {
        if (!_cobotService.IsConnected) return;
        try
        {
            var rc = await _cobotService.Rpc.ResetActiveFrameAsync(tool, 0, CancellationToken.None);
            if (rc == 0)
                _logger.LogInformation("실패 종료 후 활성 작업물 좌표계 0(베이스)·공구 #{Tool} 반납 완료.", tool);
            else
                _logger.LogWarning("실패 종료 후 활성 좌표계 반납 실패 (rc={Rc}){Desc} — 코봇 페이지의 '활성 좌표계 초기화' 필요할 수 있음",
                    rc, Communication.FairinoErrorCodes.Suffix(rc));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "실패 종료 후 활성 좌표계 반납 중 예외 — 무시");
        }
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
            _monitor.StartStep(step.Key, step.DisplayName, _steps.IndexOf(step) + 1, context.CameraTargetDistanceMm);
            _monitor.EndStep(false, msg);
            _logger.LogWarning("단계 '{Step}' 검증 실패: {Msg}", step.Key, msg);
            return StepResult.Fail(msg);
        }

        // 2) Execute
        CurrentStepKey = step.Key;
        UpdateStatus(step.Key, StepState.Running);
        _monitor.StartStep(step.Key, step.DisplayName, _steps.IndexOf(step) + 1, context.CameraTargetDistanceMm);

        try
        {
            var result = await step.ExecuteAsync(context, ct);

            UpdateStatus(step.Key, result.Success ? StepState.Completed : StepState.Failed, result.Message);
            _monitor.EndStep(result.Success, result.Message);
            _logger.LogInformation("단계 '{Step}' {Result}: {Msg}",
                step.Key, result.Success ? "완료" : "실패", result.Message);

            return result;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(step.Key, StepState.Failed, "사용자 정지");
            _monitor.EndStep(false, "사용자 정지");
            _logger.LogInformation("단계 '{Step}' 사용자 정지", step.Key);
            return StepResult.Fail("사용자 정지");
        }
        catch (Exception ex)
        {
            var errMsg = $"실행 실패: {ex.Message}{StateErrSuffix()}";
            UpdateStatus(step.Key, StepState.Failed, errMsg);
            _monitor.EndStep(false, errMsg);
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

/// <summary>시퀀스(부분) 실행 종합 결과 — ACS 경로에서 Busy(장비 점유)와 Failed(실행 실패)를 구분한다.</summary>
public enum SequenceRunOutcome
{
    Completed,
    Busy,
    Failed,
}

/// <summary><see cref="SequenceService.RunSequenceAsync"/> 결과. 실패 시 어느 단계에서 왜 실패했는지 포함.</summary>
public record SequenceRunResult(SequenceRunOutcome Outcome, string? FailedStepKey = null, string? Message = null);
