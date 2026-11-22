namespace HD.AMR.App.Service.Sequence;

/// <summary>
/// 시퀀스 전역 실행 잠금 (singleton). <see cref="SequenceService"/>는 Blazor circuit별 scoped라
/// UI 실행과 ACS(VDA5050) 실행이 서로의 IsBusy를 볼 수 없다 — 실제 장비(코봇·비전)는 하나이므로
/// 프로세스 전역에서 동시 실행을 1건으로 강제한다.
/// 잠금을 쥔 실행(<see cref="SetActive"/>)도 기억해, 다른 화면(예: 시퀀스 페이지 정지 버튼)이 ACS 가 시작한 실행을
/// 볼 수 있고 멈출 수 있게 한다(2026-11-22 M2 — ACS 검사 중 정지 버튼이 비활성이라 멈출 수 없었음).
/// </summary>
public sealed class SequenceRunGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly object _lock = new();
    private SequenceService? _active;
    private string? _activeOrderId;

    /// <summary>잠금·실행 주체가 바뀔 때 — UI 정지 버튼 갱신용(임의 스레드에서 발생).</summary>
    public event Action? Changed;

    /// <summary>즉시 획득 시도 — 실패(이미 실행 중)면 false.</summary>
    public bool TryEnter()
    {
        var ok = _sem.Wait(0);
        if (ok) Changed?.Invoke();
        return ok;
    }

    public void Exit()
    {
        lock (_lock) { _active = null; _activeOrderId = null; }
        _sem.Release();
        Changed?.Invoke();
    }

    /// <summary>다른 실행이 잠금을 보유 중인지 (참고용 스냅샷 — 판정은 TryEnter로).</summary>
    public bool IsBusy => _sem.CurrentCount == 0;

    /// <summary>잠금을 쥔 실행 등록 — <see cref="TryEnter"/> 성공 직후 호출. ACS 실행이면 order id 를 함께.</summary>
    public void SetActive(SequenceService owner, string? acsOrderId)
    {
        lock (_lock) { _active = owner; _activeOrderId = acsOrderId; }
        Changed?.Invoke();
    }

    /// <summary>잠금을 쥔 시퀀스가 지금 실행 중인지 — 조그·핸드아이 등 시퀀스가 아닌 잠금은 false.</summary>
    public bool HasActiveSequence { get { lock (_lock) return _active?.IsBusy ?? false; } }

    /// <summary>지금 실행 중인 시퀀스가 ACS order 로 시작됐으면 그 order id(아니면 null).</summary>
    public string? ActiveAcsOrderId { get { lock (_lock) return _active is null ? null : _activeOrderId; } }

    /// <summary>지금 실행 중인 시퀀스를 즉시 정지(취소 + 코봇 모션 정지). 실행 중이 아니면 false.</summary>
    public async Task<bool> StopActiveAsync()
    {
        SequenceService? owner;
        lock (_lock) owner = _active;
        if (owner is null || !owner.IsBusy) return false;
        await owner.StopAsync();
        return true;
    }
}
