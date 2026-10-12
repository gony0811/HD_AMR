namespace HD.AMR.App.Service;

/// <summary>타워램프 세 출력의 상호 배타적인 상태.</summary>
public readonly record struct TowerLampState(bool Red, bool Yellow, bool Green);

/// <summary>조작반 운전 모드·알람·임무 일시정지 상태를 타워램프 상태로 변환한다.</summary>
public static class IoTowerLampPolicy
{
    /// <param name="paused">임무 일시정지(통신 단절 PAUSE/HOLD·startPause) — 황색(통신 단절 사양 §3). 알람보다 낮고 운전 모드보다 높다.</param>
    public static TowerLampState? Resolve(bool auto, bool manual, bool alarm, bool paused = false)
    {
        if (alarm)
            return new TowerLampState(Red: true, Yellow: false, Green: false);
        if (paused)
            return new TowerLampState(Red: false, Yellow: true, Green: false);
        if (manual)
            return new TowerLampState(Red: false, Yellow: true, Green: false);
        if (auto)
            return new TowerLampState(Red: false, Yellow: false, Green: true);

        // 모드 신호가 둘 다 꺼져 있으면 마지막 유효 출력을 유지한다.
        return null;
    }

    /// <summary>
    /// 적색(알람) 조건 — 비상정지 경로 셋 중 하나라도 활성이거나 장비 오류.
    ///  · 조작반 EMO 버튼 입력(IN00)
    ///  · 화면 비상정지 버튼이 켠 EMO 출력(OUT10) — 입력으로는 되돌아오지 않으므로 출력 되읽기로 본다
    ///  · 조작반 EMO 래치(RESET 전까지 유지, <see cref="IoEmoResetControl.EmoActive"/>)
    ///  · AMR/코봇 오류 코드
    ///  · 장치 통신 HEAVY 알람(통신 두절 또는 복구 후 RESET 대기)
    /// </summary>
    public static bool IsAlarm(bool[] inputs, bool[]? outputs, bool emoLatched, bool amrError, bool cobotError,
        bool deviceAlarm = false)
        => (inputs.Length > Communication.IoPointMap.In.EmergencyStop && inputs[Communication.IoPointMap.In.EmergencyStop])
           || (outputs is not null && outputs.Length > Communication.IoPointMap.Out.EmergencyStop
               && outputs[Communication.IoPointMap.Out.EmergencyStop])
           || emoLatched || amrError || cobotError || deviceAlarm;
}
