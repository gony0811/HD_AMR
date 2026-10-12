using HD.AMR.App.Communication;
using HD.AMR.App.Enums;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>통신 단절 Fail-Safe(docs/HD_AMR_통신단절_FailSafe_사양.md) 판정·단계·재개 확인 로직.</summary>
public class CommLossFailSafeTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double sec) => T0.AddSeconds(sec);

    private static CommLossFailSafe NewFsm() => new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

    // ── 판정 조건(§2.1) ──

    [Fact]
    public void 브로커_두절은_조건A_단절()
    {
        var fsm = NewFsm();
        var ev = fsm.Evaluate(At(0), brokerConnected: false, AcsLiveness.Unknown, applicable: true);

        Assert.Equal(new[] { CommLossEvent.Lost }, ev);
        Assert.Equal(CommLossStage.Grace, fsm.Stage);
        Assert.Equal("A", fsm.Condition);
        Assert.Equal(At(0), fsm.LostSinceUtc);
    }

    [Theory]
    [InlineData(AcsLiveness.Offline)]
    [InlineData(AcsLiveness.Broken)]
    public void 브로커_유지_ACS_OFFLINE_BROKEN은_조건B_단절(AcsLiveness acs)
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), true, acs, true);

        Assert.True(fsm.IsLost);
        Assert.Equal("B", fsm.Condition);
    }

    [Fact]
    public void ACS_Unknown은_단절이_아니다()
    {
        var fsm = NewFsm();
        var ev = fsm.Evaluate(At(0), true, AcsLiveness.Unknown, true);

        Assert.Empty(ev);
        Assert.False(fsm.IsLost);
        Assert.Equal(CommLossStage.Normal, fsm.Stage);
    }

    [Fact]
    public void 단절_중_Unknown은_복구가_아니다_ONLINE이어야_복구()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), false, AcsLiveness.Unknown, true);
        // 브로커 재접속 직후 retained 수신 전
        Assert.Empty(fsm.Evaluate(At(5), true, AcsLiveness.Unknown, true));
        Assert.True(fsm.IsLost);

        Assert.Equal(new[] { CommLossEvent.Recovered }, fsm.Evaluate(At(6), true, AcsLiveness.Online, true));
        Assert.False(fsm.IsLost);
    }

    // ── 단계 거동(§3, FT-11) ──

    [Fact]
    public void FT11_1_GRACE_중_복구되면_정지하지_않는다()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), false, AcsLiveness.Unknown, true);
        Assert.Empty(fsm.Evaluate(At(20), false, AcsLiveness.Unknown, true));

        var ev = fsm.Evaluate(At(20.5), true, AcsLiveness.Online, true);

        Assert.Equal(new[] { CommLossEvent.Recovered }, ev);
        Assert.Equal(CommLossStage.Normal, fsm.Stage);
    }

    [Fact]
    public void FT11_2_4_T1에_PAUSE_T2에_HOLD()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), false, AcsLiveness.Unknown, true);

        Assert.Empty(fsm.Evaluate(At(29.9), false, AcsLiveness.Unknown, true));
        Assert.Equal(new[] { CommLossEvent.EnterPause }, fsm.Evaluate(At(30), false, AcsLiveness.Unknown, true));
        Assert.Equal(CommLossStage.Pause, fsm.Stage);

        Assert.Empty(fsm.Evaluate(At(59.9), false, AcsLiveness.Unknown, true));
        Assert.Equal(new[] { CommLossEvent.EnterHold }, fsm.Evaluate(At(60), false, AcsLiveness.Unknown, true));
        Assert.Equal(CommLossStage.Hold, fsm.Stage);
    }

    [Fact]
    public void FT11_5_PAUSE_후_복구돼도_단계를_유지한다_자동재개_없음()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), false, AcsLiveness.Unknown, true);
        fsm.Evaluate(At(31), false, AcsLiveness.Unknown, true);

        var ev = fsm.Evaluate(At(40), true, AcsLiveness.Online, true);

        Assert.Equal(new[] { CommLossEvent.Recovered }, ev);
        Assert.Equal(CommLossStage.Pause, fsm.Stage);
        // 30 s 이상 관찰해도 단계 그대로 — HOLD 로도 가지 않는다(통신은 정상).
        Assert.Empty(fsm.Evaluate(At(80), true, AcsLiveness.Online, true));
        Assert.Equal(CommLossStage.Pause, fsm.Stage);
    }

    [Fact]
    public void FT11_8_조건B만으로도_같은_단계_거동()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), true, AcsLiveness.Offline, true);
        Assert.Contains(CommLossEvent.EnterPause, fsm.Evaluate(At(30), true, AcsLiveness.Offline, true));
        Assert.Contains(CommLossEvent.EnterHold, fsm.Evaluate(At(60), true, AcsLiveness.Offline, true));
    }

    [Fact]
    public void MANUAL_또는_Order_없음이면_기록만_하고_정지하지_않는다()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), false, AcsLiveness.Unknown, applicable: false);

        Assert.Empty(fsm.Evaluate(At(90), false, AcsLiveness.Unknown, applicable: false));
        Assert.True(fsm.IsLost);
        Assert.Equal(CommLossStage.Grace, fsm.Stage);
    }

    [Fact]
    public void 적용조건이_T2_이후에_성립하면_PAUSE와_HOLD를_한번에()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), false, AcsLiveness.Unknown, applicable: false);

        var ev = fsm.Evaluate(At(70), false, AcsLiveness.Unknown, applicable: true);

        Assert.Equal(new[] { CommLossEvent.EnterPause, CommLossEvent.EnterHold }, ev);
    }

    [Fact]
    public void Reset_복구된_상태면_Normal()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), false, AcsLiveness.Unknown, true);
        fsm.Evaluate(At(30), false, AcsLiveness.Unknown, true);
        fsm.Evaluate(At(35), true, AcsLiveness.Online, true);

        fsm.Reset(At(40));

        Assert.Equal(CommLossStage.Normal, fsm.Stage);
    }

    [Fact]
    public void Reset_아직_단절이면_t0를_다시_잡아_GRACE부터()
    {
        var fsm = NewFsm();
        fsm.Evaluate(At(0), false, AcsLiveness.Unknown, true);
        fsm.Evaluate(At(65), false, AcsLiveness.Unknown, true);   // PAUSE+HOLD

        fsm.Reset(At(70));   // 현장 START 로 재개

        Assert.Equal(CommLossStage.Grace, fsm.Stage);
        Assert.Empty(fsm.Evaluate(At(99), false, AcsLiveness.Unknown, true));
        Assert.Equal(new[] { CommLossEvent.EnterPause }, fsm.Evaluate(At(100), false, AcsLiveness.Unknown, true));
    }

    [Fact]
    public void HoldAfter가_PauseAfter_이하면_보정된다()
    {
        var fsm = new CommLossFailSafe(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20));
        Assert.True(fsm.HoldAfter > fsm.PauseAfter);
    }

    // ── 재개 전 확인(§4.2) ──

    private static ResumePrecheck.Input Ok() => new(
        HasPausedMission: true, EmergencyStopActive: false, PowerMode: PowerMode.Normal,
        PositionInitialized: true, CurrentMapId: "CT1-L1", OrderMapId: "CT1-L1", OperatingMode: "AUTOMATIC");

    [Fact]
    public void Precheck_모두_만족하면_승인() => Assert.Null(ResumePrecheck.Evaluate(Ok()));

    [Fact]
    public void Precheck_일시정지_임무_없으면_거부() =>
        Assert.NotNull(ResumePrecheck.Evaluate(Ok() with { HasPausedMission = false }));

    [Fact]
    public void FT11_9_Precheck_비상정지_해제_전_거부() =>
        Assert.Contains("비상정지", ResumePrecheck.Evaluate(Ok() with { EmergencyStopActive = true }));

    [Theory]
    [InlineData(PowerMode.Low)]
    [InlineData(PowerMode.Critical)]
    public void Precheck_배터리_NORMAL_아니면_거부(PowerMode mode) =>
        Assert.Contains("PowerMode", ResumePrecheck.Evaluate(Ok() with { PowerMode = mode }));

    [Fact]
    public void Precheck_위치추정_비정상_거부() =>
        Assert.Contains("위치", ResumePrecheck.Evaluate(Ok() with { PositionInitialized = false }));

    [Fact]
    public void Precheck_mapId_불일치_거부() =>
        Assert.Contains("mapId", ResumePrecheck.Evaluate(Ok() with { CurrentMapId = "CT1-L2" }));

    [Fact]
    public void Precheck_MANUAL_거부() =>
        Assert.Contains("operatingMode", ResumePrecheck.Evaluate(Ok() with { OperatingMode = "MANUAL" }));

    // ── 일시정지 게이트 ──

    [Fact]
    public async Task Gate_정지_아니면_즉시_통과()
    {
        var gate = new MissionPauseGate();
        Assert.False(await gate.WaitWhilePausedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Gate_Pause후_Resume하면_대기했음을_알린다()
    {
        var gate = new MissionPauseGate();
        gate.Pause();
        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);
        await Task.Delay(20);
        Assert.False(wait.IsCompleted);

        gate.Resume();

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task Gate_대기중_취소되면_예외로_빠져나간다()
    {
        var gate = new MissionPauseGate();
        gate.Pause();
        using var cts = new CancellationTokenSource();
        var wait = gate.WaitWhilePausedAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    // ── IO: 타워램프·부저·START ──

    [Fact]
    public void 일시정지는_운전모드보다_우선하여_황색() =>
        Assert.Equal(new TowerLampState(false, true, false), IoTowerLampPolicy.Resolve(true, false, false, paused: true));

    [Fact]
    public void 알람은_일시정지보다_우선하여_적색() =>
        Assert.Equal(new TowerLampState(true, false, false), IoTowerLampPolicy.Resolve(true, false, true, paused: true));

    [Fact]
    public async Task 부저는_HOLD_진입과_이탈_에지에서만_쓴다()
    {
        var control = new IoCommLossBuzzerControl();
        var writes = new List<bool>();
        Task Set(bool on, CancellationToken _) { writes.Add(on); return Task.CompletedTask; }

        await control.ApplyAsync(false, Set);
        await control.ApplyAsync(true, Set);
        await control.ApplyAsync(true, Set);
        await control.ApplyAsync(false, Set);
        await control.ApplyAsync(false, Set);

        Assert.Equal(new[] { true, false }, writes);
    }

    private static bool[] StartInput(bool start)
    {
        var inputs = new bool[16];
        inputs[IoPointMap.In.Start] = start;
        return inputs;
    }

    [Fact]
    public async Task START_상승에지_일시정지_중에만_재개요청()
    {
        var control = new IoStartResumeControl();
        var calls = 0;
        Task Resume() { calls++; return Task.CompletedTask; }

        await control.ApplyAsync(StartInput(false), paused: true, Resume);
        await control.ApplyAsync(StartInput(true), paused: false, Resume);   // 정지 아님 — 무시
        await control.ApplyAsync(StartInput(false), paused: true, Resume);
        await control.ApplyAsync(StartInput(true), paused: true, Resume);    // 재개 요청
        await control.ApplyAsync(StartInput(true), paused: true, Resume);    // 누르고 있음 — 재요청 없음

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task START_눌린채_기동하면_재개하지_않는다()
    {
        var control = new IoStartResumeControl();
        var calls = 0;

        await control.ApplyAsync(StartInput(true), paused: true, () => { calls++; return Task.CompletedTask; });

        Assert.Equal(0, calls);
    }
}
