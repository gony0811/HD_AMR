namespace HD.AMR.App.Service;

/// <summary>HEAVY 알람 감시 대상 장치.</summary>
public enum DeviceKind
{
    Vision,
    DepthCamera,
    Laser,
    IoModule,
    Cobot,
    Amr,
}

/// <summary>장치 알람 단계 — Normal → Active(끊김) → Latched(복구됨, RESET 대기) → Normal.</summary>
public enum DeviceAlarmStage
{
    Normal,
    Active,
    Latched,
}

public enum DeviceAlarmEventKind
{
    Raised,
    Recovered,
}

/// <summary>한 주기 관측값 — 정상 여부와 사유(비정상일 때 알람 설명에 쓴다).</summary>
public readonly record struct DeviceHealthSample(bool Healthy, string Detail);

public sealed record DeviceAlarmEvent(DeviceKind Kind, DeviceAlarmEventKind Event, string Detail);

/// <summary>알람 중인 장치 1건(Active 또는 Latched).</summary>
public sealed record DeviceAlarmEntry(
    DeviceKind Kind, DeviceAlarmStage Stage, DateTime SinceUtc, DateTime? RecoveredUtc, string Detail);

/// <summary>
/// 장치 통신 HEAVY 알람 상태기(순수 로직) — 비정상이 <c>lostAfter</c> 이상 이어지면 알람(Raised).
/// 복구돼도 자동 해제하지 않고 Latched 로 남는다(RESET 으로만 해제). 기동 후 <c>startupGrace</c> 동안은
/// 알람을 내지 않지만, 그 뒤에도 끝내 정상이 안 된 장치는 알람이다(상태 확인 불가 포함).
/// </summary>
public sealed class DeviceAlarmTracker
{
    private sealed class Slot
    {
        public DeviceAlarmStage Stage;
        public DateTime? UnhealthySince;
        public DateTime AlarmSince;
        public DateTime? RecoveredAt;
        public string Detail = "";
    }

    private readonly TimeSpan _lostAfter;
    private readonly TimeSpan _startupGrace;
    private readonly DateTime _startedUtc;
    private readonly Dictionary<DeviceKind, Slot> _slots;

    public DeviceAlarmTracker(TimeSpan lostAfter, TimeSpan startupGrace, DateTime startedUtc, IEnumerable<DeviceKind> enabled)
    {
        _lostAfter = lostAfter < TimeSpan.Zero ? TimeSpan.Zero : lostAfter;
        _startupGrace = startupGrace < TimeSpan.Zero ? TimeSpan.Zero : startupGrace;
        _startedUtc = startedUtc;
        _slots = enabled.Distinct().ToDictionary(k => k, _ => new Slot());
    }

    public IReadOnlyCollection<DeviceKind> Enabled => _slots.Keys;

    /// <summary>Active 또는 Latched 장치가 하나라도 있으면 true — order 수신 불가.</summary>
    public bool HasHeavyAlarm => _slots.Values.Any(s => s.Stage != DeviceAlarmStage.Normal);

    /// <summary>관측값 반영. 감시 대상인데 관측값이 없으면 "상태 미확인"으로 비정상 처리한다.</summary>
    public IReadOnlyList<DeviceAlarmEvent> Evaluate(DateTime now, IReadOnlyDictionary<DeviceKind, DeviceHealthSample> samples)
    {
        var events = new List<DeviceAlarmEvent>();
        var graceOver = now - _startedUtc >= _startupGrace;

        foreach (var (kind, slot) in _slots)
        {
            var sample = samples.TryGetValue(kind, out var s) ? s : new DeviceHealthSample(false, "상태 미확인");

            if (sample.Healthy)
            {
                slot.UnhealthySince = null;
                if (slot.Stage == DeviceAlarmStage.Active)
                {
                    slot.Stage = DeviceAlarmStage.Latched;
                    slot.RecoveredAt = now;
                    events.Add(new DeviceAlarmEvent(kind, DeviceAlarmEventKind.Recovered, slot.Detail));
                }
                continue;
            }

            slot.UnhealthySince ??= now;
            if (slot.Stage == DeviceAlarmStage.Active)
            {
                slot.Detail = sample.Detail;   // 사유 갱신(표시용)
                continue;
            }

            if (graceOver && now - slot.UnhealthySince.Value >= _lostAfter)
            {
                // Normal → Active, 또는 RESET 대기 중 재단절(Latched → Active).
                slot.Stage = DeviceAlarmStage.Active;
                slot.AlarmSince = slot.UnhealthySince.Value;
                slot.RecoveredAt = null;
                slot.Detail = sample.Detail;
                events.Add(new DeviceAlarmEvent(kind, DeviceAlarmEventKind.Raised, sample.Detail));
            }
        }
        return events;
    }

    /// <summary>RESET — 복구된(Latched) 알람만 해제한다. 아직 끊긴(Active) 장치는 유지. 해제된 장치 목록 반환.</summary>
    public IReadOnlyList<DeviceKind> Reset()
    {
        var cleared = new List<DeviceKind>();
        foreach (var (kind, slot) in _slots)
        {
            if (slot.Stage != DeviceAlarmStage.Latched) continue;
            slot.Stage = DeviceAlarmStage.Normal;
            slot.RecoveredAt = null;
            slot.Detail = "";
            cleared.Add(kind);
        }
        return cleared;
    }

    /// <summary>알람 중인 장치(Active·Latched)만.</summary>
    public IReadOnlyList<DeviceAlarmEntry> Snapshot()
        => _slots
            .Where(p => p.Value.Stage != DeviceAlarmStage.Normal)
            .Select(p => new DeviceAlarmEntry(p.Key, p.Value.Stage, p.Value.AlarmSince, p.Value.RecoveredAt, p.Value.Detail))
            .ToList();

    /// <summary>장치 한글 표시명.</summary>
    public static string DisplayName(DeviceKind kind) => kind switch
    {
        DeviceKind.Vision => "검사 비전카메라",
        DeviceKind.DepthCamera => "Depth 카메라",
        DeviceKind.Laser => "3점 레이저 센서",
        DeviceKind.IoModule => "I/O 모듈",
        DeviceKind.Cobot => "코봇",
        DeviceKind.Amr => "AMR",
        _ => kind.ToString(),
    };

    /// <summary>VDA5050 errorType(장치별, FATAL).</summary>
    public static string ErrorType(DeviceKind kind) => kind switch
    {
        DeviceKind.Vision => "visionLinkLost",
        DeviceKind.DepthCamera => "depthCameraLinkLost",
        DeviceKind.Laser => "laserSensorLinkLost",
        DeviceKind.IoModule => "ioModuleLinkLost",
        DeviceKind.Cobot => "cobotLinkLost",
        DeviceKind.Amr => "amrLinkLost",
        _ => "deviceLinkLost",
    };

    /// <summary>errorReferences 의 device 값.</summary>
    public static string ReferenceValue(DeviceKind kind) => kind switch
    {
        DeviceKind.Vision => "vision",
        DeviceKind.DepthCamera => "depthCamera",
        DeviceKind.Laser => "laserSensor",
        DeviceKind.IoModule => "ioModule",
        DeviceKind.Cobot => "cobot",
        DeviceKind.Amr => "amr",
        _ => kind.ToString(),
    };
}
