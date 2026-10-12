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
