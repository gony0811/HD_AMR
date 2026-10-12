using HD.AMR.App.Communication;

namespace HD.AMR.App.Service;

/// <summary>
/// 통신 단절 HOLD 부저(통신 단절 사양 §3) — HOLD 진입 시 부저 ON 1회, HOLD 이탈(재개·취소) 시 OFF 1회.
/// 에지에서만 쓰므로 운영자가 화면 "Buzzer Off" 로 끈 부저를 다시 켜지 않는다(다음 HOLD 진입 때까지).
/// 콜백 실패(throw)는 상태를 전진시키지 않고 다음 폴링에서 재시도되도록 상위로 전파한다.
/// </summary>
public sealed class IoCommLossBuzzerControl
{
    private bool _holdApplied;

    public async Task ApplyAsync(bool hold, Func<bool, CancellationToken, Task> setBuzzer, CancellationToken ct = default)
    {
        if (hold == _holdApplied) return;
        await setBuzzer(hold, ct);
        _holdApplied = hold;
    }
}

/// <summary>
/// 조작반 START 버튼(IN02) 재개 경로(통신 단절 사양 §4.1-2) — 임무 일시정지 중 START 상승 에지에서 재개를 요청한다.
/// 기동 첫 스냅샷은 기준선으로만 쓴다(눌린 채 기동해도 재개하지 않음). 재개 전 확인(§4.2)은 요청 받는 쪽이 한다.
/// </summary>
public sealed class IoStartResumeControl
{
    private bool _prevStart;
    private bool _initialized;

    public async Task ApplyAsync(bool[] inputs, bool paused, Func<Task> requestResume)
    {
        if (inputs.Length <= IoPointMap.In.Start)
            return;

        var start = inputs[IoPointMap.In.Start];
        var rose = _initialized && start && !_prevStart;
        _prevStart = start;
        _initialized = true;

        if (rose && paused)
            await requestResume();
    }
}
