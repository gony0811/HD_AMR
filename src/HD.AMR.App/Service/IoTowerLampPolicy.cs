namespace HD.AMR.App.Service;

/// <summary>타워램프 세 출력의 상호 배타적인 상태.</summary>
public readonly record struct TowerLampState(bool Red, bool Yellow, bool Green);

/// <summary>조작반 운전 모드와 알람 상태를 타워램프 상태로 변환한다.</summary>
public static class IoTowerLampPolicy
{
    public static TowerLampState? Resolve(bool auto, bool manual, bool alarm)
    {
        if (alarm)
            return new TowerLampState(Red: true, Yellow: false, Green: false);
        if (manual)
            return new TowerLampState(Red: false, Yellow: true, Green: false);
        if (auto)
            return new TowerLampState(Red: false, Yellow: false, Green: true);

        // 모드 신호가 둘 다 꺼져 있으면 마지막 유효 출력을 유지한다.
        return null;
    }
}
