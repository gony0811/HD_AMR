using HD.AMR.App.Communication;

namespace HD.AMR.App.Service;

/// <summary>
/// 물리 EMO 버튼(IN00) ↔ RESET 버튼(IN01) ↔ RESET 버튼 램프(OUT05) 인터록.
///
/// 상태 전이:
///  - EMO rising, 또는 기동 시 EMO 이미 활성 → 소프트웨어 비상정지 트리거(1회) + 램프 ON
///  - EMO latched(ON 유지) → 램프 ON 유지 (멱등 쓰기, 재트리거 없음)
///  - 물리 EMO 해제(IN00 OFF) 상태에서 RESET rising → 소프트웨어 비상정지 해제 + 램프 OFF
///  - EMO 여전히 ON 인데 RESET 눌림 → 가드: 아무 동작 안 함
///
/// 콜백 실패(throw)는 상태를 전진시키지 않고 다음 폴링에서 재시도되도록 상위로 전파한다.
/// </summary>
public sealed class IoEmoResetControl
{
    private bool _emoActive;
    private bool _prevEmoInput;
    private bool _prevResetInput;
    private bool _prevLampWritten;
    private bool _initialized;

    public bool EmoActive => _emoActive;

    public async Task ApplyAsync(
        bool[] inputs,
        Func<string, Task> triggerEmergencyStop,
        Func<string, Task> clearEmergencyStop,
        Func<bool, CancellationToken, Task> setResetLamp,
        CancellationToken ct = default)
    {
        if (inputs.Length <= Math.Max(IoPointMap.In.EmergencyStop, IoPointMap.In.Reset))
            return;

        var emoIn = inputs[IoPointMap.In.EmergencyStop];
        var resetIn = inputs[IoPointMap.In.Reset];

        if (emoIn && (!_initialized || !_prevEmoInput) && !_emoActive)
        {
            await triggerEmergencyStop(_initialized ? "조작반 EMO 버튼" : "기동 시 EMO 버튼 활성");
            _emoActive = true;
        }

        if (_emoActive && _initialized && !_prevResetInput && resetIn && !emoIn)
        {
            await clearEmergencyStop("조작반 RESET 버튼");
            _emoActive = false;
        }

        if (!_initialized || _emoActive != _prevLampWritten)
        {
            await setResetLamp(_emoActive, ct);
            _prevLampWritten = _emoActive;
        }

        _prevEmoInput = emoIn;
        _prevResetInput = resetIn;
        _initialized = true;
    }
}
