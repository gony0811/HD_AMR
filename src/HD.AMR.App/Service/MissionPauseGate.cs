namespace HD.AMR.App.Service;

/// <summary>
/// 임무 일시정지 게이트(통신 단절 사양 §3·§4) — Order 실행기·검사 시퀀스가 <b>안전한 경계</b>(주행 대기 루프,
/// action 시작 전, 시퀀스 스텝 사이, 검사 경유점 사이)에서 <see cref="WaitWhilePausedAsync"/> 로 멈춰 선다.
///
/// 진행 중인 동작(MoveL 1구간 등)은 끊지 않는다 — 벽면 근접 자세에서 궤적 중간 정지는 자세 복원 시
/// 간섭 위험이 있기 때문(§3). 정지/재개 판단은 <see cref="Vda5050OrderExecutor"/> 가 하고, 이 클래스는 대기만 제공한다.
/// 대기 중 취소 토큰이 걸리면 즉시 예외로 빠져나가므로 EMO·신규 Order·배터리 CRITICAL 의 기존 취소 경로가
/// 일시정지보다 우선한다(§7).
/// </summary>
public sealed class MissionPauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource? _resumed;   // null = 정지 아님

    public bool IsPaused
    {
        get { lock (_lock) return _resumed is not null; }
    }

    /// <summary>일시정지 — 이후 <see cref="WaitWhilePausedAsync"/> 호출자가 멈춘다. 멱등.</summary>
    public void Pause()
    {
        lock (_lock)
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>재개 — 대기 중인 호출자를 모두 깨운다. 멱등.</summary>
    public void Resume()
    {
        TaskCompletionSource? tcs;
        lock (_lock)
        {
            tcs = _resumed;
            _resumed = null;
        }
        tcs?.TrySetResult();
    }

    /// <summary>정지 중이면 재개될 때까지 대기한다. 실제로 대기했으면 true(호출자가 재개 동작 — 재지령·재촬영 — 을 수행).</summary>
    public async Task<bool> WaitWhilePausedAsync(CancellationToken ct)
    {
        var waited = false;
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_resumed is null) return waited;
                wait = _resumed.Task;
            }
            waited = true;
            await wait.WaitAsync(ct);
        }
    }
}
