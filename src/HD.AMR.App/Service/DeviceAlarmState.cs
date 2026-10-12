namespace HD.AMR.App.Service;

/// <summary>
/// 장치 HEAVY 알람 공유 상태(의존성 없는 싱글톤) — <see cref="DeviceHealthMonitorService"/> 가 쓰고
/// 실행기(order 게이트)·어댑터(state.errors)·IO(타워램프)·UI 가 읽는다. 감시 서비스가 실행기를 참조하므로
/// 실행기가 감시 서비스를 직접 참조하면 DI 순환이 생겨 이 중간 객체를 둔다.
/// </summary>
public sealed class DeviceAlarmState
{
    private readonly object _lock = new();
    private IReadOnlyList<DeviceAlarmEntry> _entries = Array.Empty<DeviceAlarmEntry>();

    /// <summary>알람 장치 목록이나 단계가 바뀔 때.</summary>
    public event Action? Changed;

    /// <summary>RESET 요청(인자 = 요청 출처) — 감시 서비스가 처리한다. IO·UI 가 감시 서비스를 직접 참조하지 않도록
    /// 이 객체를 거친다(IoModuleService ↔ 감시 서비스 DI 순환 방지).</summary>
    public event Action<string>? ResetRequested;

    /// <summary>복구된(RESET 대기) 알람 해제 요청. 아직 끊긴 장치는 유지된다.</summary>
    public void RequestReset(string source) => ResetRequested?.Invoke(source);

    /// <summary>HEAVY 알람이 하나라도 있으면 true — ACS order 수신 불가.</summary>
    public bool HasHeavyAlarm
    {
        get { lock (_lock) return _entries.Count > 0; }
    }

    public IReadOnlyList<DeviceAlarmEntry> Snapshot()
    {
        lock (_lock) return _entries;
    }

    /// <summary>알람 목록 교체 — 내용이 바뀌었을 때만 <see cref="Changed"/> 를 발생시킨다.</summary>
    public void Update(IReadOnlyList<DeviceAlarmEntry> entries)
    {
        bool changed;
        lock (_lock)
        {
            changed = !_entries.SequenceEqual(entries);
            if (changed) _entries = entries;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>"코봇(끊김), AMR(RESET 대기)" 형태 요약.</summary>
    public static string Describe(IEnumerable<DeviceAlarmEntry> entries)
        => string.Join(", ", entries.Select(e =>
            $"{DeviceAlarmTracker.DisplayName(e.Kind)}({(e.Stage == DeviceAlarmStage.Active ? "끊김" : "복구됨·RESET 대기")})"));
}
