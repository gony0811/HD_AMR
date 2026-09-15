namespace HD.AMR.App.Service.Sequence;

/// <summary>
/// 시퀀스 전역 실행 잠금 (singleton). <see cref="SequenceService"/>는 Blazor circuit별 scoped라
/// UI 실행과 ACS(VDA5050) 실행이 서로의 IsBusy를 볼 수 없다 — 실제 장비(코봇·비전)는 하나이므로
/// 프로세스 전역에서 동시 실행을 1건으로 강제한다.
/// </summary>
public sealed class SequenceRunGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    /// <summary>즉시 획득 시도 — 실패(이미 실행 중)면 false.</summary>
    public bool TryEnter() => _sem.Wait(0);

    public void Exit() => _sem.Release();

    /// <summary>다른 실행이 잠금을 보유 중인지 (참고용 스냅샷 — 판정은 TryEnter로).</summary>
    public bool IsBusy => _sem.CurrentCount == 0;
}
