using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class IoEmoResetControlTests
{
    private static bool[] Inputs(bool emo, bool reset)
    {
        var inputs = new bool[16];
        inputs[IoPointMap.In.EmergencyStop] = emo;
        inputs[IoPointMap.In.Reset] = reset;
        return inputs;
    }

    private sealed class Spy
    {
        public List<string> Triggers { get; } = new();
        public List<string> Clears { get; } = new();
        public List<bool> LampWrites { get; } = new();

        public Func<string, Task> Trigger => reason => { Triggers.Add(reason); return Task.CompletedTask; };
        public Func<string, Task> Clear => reason => { Clears.Add(reason); return Task.CompletedTask; };
        public Func<bool, CancellationToken, Task> Lamp => (on, _) => { LampWrites.Add(on); return Task.CompletedTask; };
    }

    [Fact]
    public async Task EmoRisingEdge_TriggersStopAndTurnsLampOn()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Single(spy.Triggers);
        Assert.Equal("조작반 EMO 버튼", spy.Triggers[0]);
        Assert.Empty(spy.Clears);
        Assert.Equal(new[] { false, true }, spy.LampWrites);
        Assert.True(control.EmoActive);
    }

    [Fact]
    public async Task EmoHeld_DoesNotRetrigger()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Single(spy.Triggers);
        Assert.Equal(new[] { false, true }, spy.LampWrites);
    }

    [Fact]
    public async Task ResetWhileEmoStillPhysicallyPressed_IsIgnored()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: true, reset: true), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Empty(spy.Clears);
        Assert.Equal(new[] { false, true }, spy.LampWrites);
        Assert.True(control.EmoActive);
    }

    [Fact]
    public async Task ResetAfterEmoReleased_ClearsEmoAndTurnsLampOff()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: true), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Single(spy.Triggers);
        Assert.Single(spy.Clears);
        Assert.Equal("조작반 RESET 버튼", spy.Clears[0]);
        Assert.Equal(new[] { false, true, false }, spy.LampWrites);
        Assert.False(control.EmoActive);
    }

    [Fact]
    public async Task ResetHeldAfterRelease_DoesNotTriggerClearTwice()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: true), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: true), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Single(spy.Clears);
    }

    [Fact]
    public async Task ResetWithoutEmoEverActive_IsNoOp()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: true), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Empty(spy.Triggers);
        Assert.Empty(spy.Clears);
        Assert.Equal(new[] { false }, spy.LampWrites);
    }

    [Fact]
    public async Task BootsWithEmoAlreadyPressed_TreatsAsAlreadyActive()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Single(spy.Triggers);
        Assert.Equal("기동 시 EMO 버튼 활성", spy.Triggers[0]);
        Assert.Equal(new[] { true }, spy.LampWrites);
        Assert.True(control.EmoActive);
    }

    [Fact]
    public async Task LampWrite_IsIdempotent()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);
        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Equal(new[] { false }, spy.LampWrites);
    }

    [Fact]
    public async Task TriggerFailure_IsRetriedOnNextPoll()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(Inputs(emo: false, reset: false), spy.Trigger, spy.Clear, spy.Lamp);

        await Assert.ThrowsAsync<IOException>(() => control.ApplyAsync(
            Inputs(emo: true, reset: false),
            _ => Task.FromException(new IOException("broker down")),
            spy.Clear,
            spy.Lamp));

        await control.ApplyAsync(Inputs(emo: true, reset: false), spy.Trigger, spy.Clear, spy.Lamp);

        Assert.Single(spy.Triggers);
        Assert.Equal("조작반 EMO 버튼", spy.Triggers[0]);
        Assert.True(control.EmoActive);
    }

    [Fact]
    public async Task IncompleteInputArray_IsIgnored()
    {
        var control = new IoEmoResetControl();
        var spy = new Spy();

        await control.ApplyAsync(new bool[1],
            _ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException());

        Assert.Empty(spy.Triggers);
        Assert.Empty(spy.Clears);
        Assert.Empty(spy.LampWrites);
    }
}
