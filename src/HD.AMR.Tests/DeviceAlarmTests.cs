using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Enums;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class DeviceAlarmTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double sec) => T0.AddSeconds(sec);

    private static readonly DeviceKind[] All = Enum.GetValues<DeviceKind>();

    /// <summary>유예 0, 판정 3 s.</summary>
    private static DeviceAlarmTracker New(TimeSpan? grace = null, IEnumerable<DeviceKind>? enabled = null)
        => new(TimeSpan.FromSeconds(3), grace ?? TimeSpan.Zero, T0, enabled ?? All);

    private static Dictionary<DeviceKind, DeviceHealthSample> AllOk()
        => All.ToDictionary(k => k, _ => new DeviceHealthSample(true, ""));

    private static Dictionary<DeviceKind, DeviceHealthSample> Down(DeviceKind kind, string detail = "미연결")
    {
        var d = AllOk();
        d[kind] = new DeviceHealthSample(false, detail);
        return d;
    }

    [Fact]
    public void 판정_시간_미만_끊김은_알람이_아니다()
    {
        var t = New();
        Assert.Empty(t.Evaluate(At(0), Down(DeviceKind.Cobot)));
        Assert.Empty(t.Evaluate(At(2.5), Down(DeviceKind.Cobot)));
        Assert.Empty(t.Evaluate(At(3), AllOk()));
        Assert.False(t.HasHeavyAlarm);
    }

    [Fact]
    public void 끊김이_지속되면_알람()
    {
        var t = New();
        t.Evaluate(At(0), Down(DeviceKind.Amr, "Modbus 미연결"));
        var ev = Assert.Single(t.Evaluate(At(3), Down(DeviceKind.Amr, "Modbus 미연결")));
        Assert.Equal(new DeviceAlarmEvent(DeviceKind.Amr, DeviceAlarmEventKind.Raised, "Modbus 미연결"), ev);
        Assert.True(t.HasHeavyAlarm);

        var entry = Assert.Single(t.Snapshot());
        Assert.Equal(DeviceAlarmStage.Active, entry.Stage);
        Assert.Equal(At(0), entry.SinceUtc);

        Assert.Empty(t.Evaluate(At(10), Down(DeviceKind.Amr, "Modbus 미연결")));   // 재발생 없음
    }

    [Fact]
    public void 복구돼도_래치되고_RESET_으로만_해제()
    {
        var t = New();
        t.Evaluate(At(0), Down(DeviceKind.Vision));
        t.Evaluate(At(3), Down(DeviceKind.Vision));

        var ev = Assert.Single(t.Evaluate(At(5), AllOk()));
        Assert.Equal(DeviceAlarmEventKind.Recovered, ev.Event);
        Assert.True(t.HasHeavyAlarm);
        Assert.Equal(DeviceAlarmStage.Latched, Assert.Single(t.Snapshot()).Stage);

        Assert.Equal(new[] { DeviceKind.Vision }, t.Reset());
        Assert.False(t.HasHeavyAlarm);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void 아직_끊긴_장치는_RESET_해도_유지()
    {
        var t = New();
        t.Evaluate(At(0), Down(DeviceKind.Laser));
        t.Evaluate(At(3), Down(DeviceKind.Laser));

        Assert.Empty(t.Reset());
        Assert.True(t.HasHeavyAlarm);
        Assert.Equal(DeviceAlarmStage.Active, Assert.Single(t.Snapshot()).Stage);
    }

    [Fact]
    public void RESET_대기_중_재단절이면_다시_Active()
    {
        var t = New();
        t.Evaluate(At(0), Down(DeviceKind.IoModule));
        t.Evaluate(At(3), Down(DeviceKind.IoModule));
        t.Evaluate(At(4), AllOk());
        t.Evaluate(At(5), Down(DeviceKind.IoModule));
        var ev = Assert.Single(t.Evaluate(At(8), Down(DeviceKind.IoModule)));
        Assert.Equal(DeviceAlarmEventKind.Raised, ev.Event);
        Assert.Equal(DeviceAlarmStage.Active, Assert.Single(t.Snapshot()).Stage);
    }

    [Fact]
    public void 기동_유예_중에는_알람_없고_끝나면_미연결_장치는_알람()
    {
        var t = New(grace: TimeSpan.FromSeconds(30));
        Assert.Empty(t.Evaluate(At(0), Down(DeviceKind.DepthCamera)));
        Assert.Empty(t.Evaluate(At(29), Down(DeviceKind.DepthCamera)));
        var ev = Assert.Single(t.Evaluate(At(30), Down(DeviceKind.DepthCamera)));
        Assert.Equal(DeviceKind.DepthCamera, ev.Kind);
    }

    [Fact]
    public void 유예_중_연결되면_알람_없음()
    {
        var t = New(grace: TimeSpan.FromSeconds(30));
        t.Evaluate(At(0), Down(DeviceKind.Cobot));
        t.Evaluate(At(10), AllOk());
        Assert.Empty(t.Evaluate(At(31), AllOk()));
        Assert.False(t.HasHeavyAlarm);
    }

    [Fact]
    public void 비활성_장치는_무시()
    {
        var t = New(enabled: All.Where(k => k != DeviceKind.Vision));
        t.Evaluate(At(0), Down(DeviceKind.Vision));
        Assert.Empty(t.Evaluate(At(10), Down(DeviceKind.Vision)));
        Assert.False(t.HasHeavyAlarm);
    }

    [Fact]
    public void 관측값_없는_장치는_상태_미확인으로_알람()
    {
        var t = New(enabled: new[] { DeviceKind.Amr });
        var empty = new Dictionary<DeviceKind, DeviceHealthSample>();
        t.Evaluate(At(0), empty);
        var ev = Assert.Single(t.Evaluate(At(3), empty));
        Assert.Equal("상태 미확인", ev.Detail);
    }

    [Fact]
    public void 장치별_errorType_은_서로_다르다()
    {
        var types = All.Select(DeviceAlarmTracker.ErrorType).ToList();
        Assert.Equal(types.Count, types.Distinct().Count());
        Assert.Equal("cobotLinkLost", DeviceAlarmTracker.ErrorType(DeviceKind.Cobot));
    }
}

public class DeviceAlarmStateTests
{
    [Fact]
    public void 내용이_같으면_Changed_를_내지_않는다()
    {
        var s = new DeviceAlarmState();
        var count = 0;
        s.Changed += () => count++;
        var since = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        var entries = new[] { new DeviceAlarmEntry(DeviceKind.Amr, DeviceAlarmStage.Active, since, null, "x") };

        s.Update(entries);
        s.Update(new[] { new DeviceAlarmEntry(DeviceKind.Amr, DeviceAlarmStage.Active, since, null, "x") });
        Assert.Equal(1, count);
        Assert.True(s.HasHeavyAlarm);

        s.Update(Array.Empty<DeviceAlarmEntry>());
        Assert.Equal(2, count);
        Assert.False(s.HasHeavyAlarm);
    }

    [Fact]
    public void RESET_요청은_이벤트로_전달()
    {
        var s = new DeviceAlarmState();
        string? got = null;
        s.ResetRequested += src => got = src;
        s.RequestReset("화면");
        Assert.Equal("화면", got);
    }
}

public class DeviceFaultGateTests
{
    private static Vda5050Order Order() => new()
    {
        OrderId = "o1",
        Nodes = new List<OrderNode>
        {
            new()
            {
                NodeId = "n1", Released = true,
                NodePosition = new NodePosition { MapId = "m1" },
                Actions = new List<VdaAction> { new() { ActionId = "a0", ActionType = "batterySwapMove" } },
            },
        },
    };

    [Fact]
    public void 장치_알람이면_거부()
        => Assert.Equal(OrderAdmissionGate.Decision.RejectDeviceFault,
            OrderAdmissionGate.Evaluate(PowerMode.Normal, "AUTOMATIC", Order(), deviceFault: true));

    [Fact]
    public void 장치_알람이_배터리_판정보다_우선()
        => Assert.Equal(OrderAdmissionGate.Decision.RejectDeviceFault,
            OrderAdmissionGate.Evaluate(PowerMode.Low, "MANUAL", Order(), deviceFault: true));

    [Fact]
    public void 알람_없으면_기존_판정()
        => Assert.Equal(OrderAdmissionGate.Decision.Accept,
            OrderAdmissionGate.Evaluate(PowerMode.Low, "AUTOMATIC", Order(), deviceFault: false));
}

public class DeviceAlarmIoTests
{
    [Fact]
    public void 장치_알람이면_타워램프_적색()
    {
        var inputs = new bool[16];
        Assert.True(IoTowerLampPolicy.IsAlarm(inputs, new bool[16], false, false, false, deviceAlarm: true));
        Assert.False(IoTowerLampPolicy.IsAlarm(inputs, new bool[16], false, false, false, deviceAlarm: false));
    }

    [Fact]
    public void RESET_상승_에지에서만_요청_기동_기준선_무시()
    {
        var c = new IoAlarmResetControl();
        var count = 0;
        var pressed = new bool[16];
        pressed[IoPointMap.In.Reset] = true;
        var released = new bool[16];

        c.Apply(pressed, () => count++);    // 기동 시 눌린 상태 — 기준선
        Assert.Equal(0, count);
        c.Apply(pressed, () => count++);    // 유지
        Assert.Equal(0, count);
        c.Apply(released, () => count++);
        c.Apply(pressed, () => count++);    // 상승 에지
        Assert.Equal(1, count);
        c.Apply(pressed, () => count++);
        Assert.Equal(1, count);
    }
}

/// <summary>
/// 재시작 시 알람 소실 회귀 — 현장(HD-AMR-M2, 2026-11-20)에서 RESET 안 된 알람(예: 16:43:46 코봇 "상태 패킷 중단")이
/// 16:49:50 재시작으로 메모리에서 사라져, 운영 로그(DB)에는 "발생"만 남고 ACS state.errors 에는 없었다.
/// </summary>
public class DeviceAlarmPersistenceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 7, 43, 0, DateTimeKind.Utc);
    private static DateTime At(double sec) => T0.AddSeconds(sec);
    private static readonly DeviceKind[] All = Enum.GetValues<DeviceKind>();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdamr-alarm-" + Guid.NewGuid().ToString("N"));
    private DeviceAlarmStore Store() => new(Path.Combine(_dir, "device-alarms.json"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Dictionary<DeviceKind, DeviceHealthSample> Samples(params DeviceKind[] down)
        => All.ToDictionary(k => k, k => new DeviceHealthSample(!down.Contains(k), down.Contains(k) ? "끊김" : ""));

    /// <summary>코봇 Active, AMR Latched 상태의 "재시작 전" 상태기.</summary>
    private static DeviceAlarmTracker BeforeRestart()
    {
        var t = new DeviceAlarmTracker(TimeSpan.FromSeconds(3), TimeSpan.Zero, T0, All);
        t.Evaluate(At(0), Samples(DeviceKind.Cobot, DeviceKind.Amr));
        t.Evaluate(At(3), Samples(DeviceKind.Cobot, DeviceKind.Amr));   // 둘 다 Raised
        t.Evaluate(At(4), Samples(DeviceKind.Cobot));                    // AMR Recovered → Latched
        return t;
    }

    [Fact]
    public void 재시작해도_미해제_알람이_기동_유예_중에도_유지된다()
    {
        var before = BeforeRestart();
        var store = Store();
        store.Save(before.Snapshot());

        // 재시작 — 기동 유예 30 s. 예전에는 여기서 알람 0건(ACS state.errors 에서 사라짐).
        var after = new DeviceAlarmTracker(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), At(100), All);
        var (restored, dropped) = after.Restore(store.Load());

        Assert.Equal(2, restored.Count);
        Assert.Empty(dropped);
        Assert.True(after.HasHeavyAlarm);
        Assert.Equal(before.Snapshot().OrderBy(e => e.Kind), after.Snapshot().OrderBy(e => e.Kind));

        // 유예 중 장치가 아직 안 붙었어도(관측 없음) 중복 "발생" 이벤트 없이 그대로 유지.
        Assert.Empty(after.Evaluate(At(101), new Dictionary<DeviceKind, DeviceHealthSample>()));
        Assert.Equal(2, after.Snapshot().Count);
    }

    [Fact]
    public void 복원된_Active_는_복구되면_Latched_이고_RESET_으로만_해제()
    {
        var store = Store();
        store.Save(BeforeRestart().Snapshot());
        var after = new DeviceAlarmTracker(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), At(100), All);
        after.Restore(store.Load());

        var ev = Assert.Single(after.Evaluate(At(101), Samples()));
        Assert.Equal((DeviceKind.Cobot, DeviceAlarmEventKind.Recovered), (ev.Kind, ev.Event));
        Assert.All(after.Snapshot(), e => Assert.Equal(DeviceAlarmStage.Latched, e.Stage));

        Assert.Equal(new[] { DeviceKind.Cobot, DeviceKind.Amr }.OrderBy(k => k), after.Reset().OrderBy(k => k));
        Assert.False(after.HasHeavyAlarm);

        store.Save(after.Snapshot());
        Assert.Empty(store.Load());   // RESET 후 재시작하면 복원할 것 없음
    }

    [Fact]
    public void 감시_대상이_아닌_장치_알람은_복원하지_않고_Dropped()
    {
        var store = Store();
        store.Save(BeforeRestart().Snapshot());
        var after = new DeviceAlarmTracker(TimeSpan.FromSeconds(3), TimeSpan.Zero, At(100),
            new[] { DeviceKind.Amr, DeviceKind.Vision });
        var (restored, dropped) = after.Restore(store.Load());

        Assert.Equal(DeviceKind.Amr, Assert.Single(restored).Kind);
        Assert.Equal(DeviceKind.Cobot, Assert.Single(dropped).Kind);
        Assert.Equal(DeviceKind.Amr, Assert.Single(after.Snapshot()).Kind);
    }

    [Fact]
    public void 저장_파일이_없거나_깨졌으면_빈_목록()
    {
        var store = Store();
        Assert.Empty(store.Load());
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void 저장_복원은_단계_시각_사유를_보존()
    {
        var snap = BeforeRestart().Snapshot();
        var store = Store();
        store.Save(snap);
        var loaded = store.Load();
        Assert.Equal(snap.OrderBy(e => e.Kind), loaded.OrderBy(e => e.Kind));
        var amr = loaded.Single(e => e.Kind == DeviceKind.Amr);
        Assert.Equal(DeviceAlarmStage.Latched, amr.Stage);
        Assert.Equal(At(4), amr.RecoveredUtc);
        Assert.Equal(DateTimeKind.Utc, amr.SinceUtc.Kind);
    }
}
