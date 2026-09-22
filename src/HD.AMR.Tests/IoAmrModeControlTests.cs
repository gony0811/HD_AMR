using HD.AMR.App.Communication;
using HD.AMR.App.Enums;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class IoAmrModeControlTests
{
    private static bool[] Inputs(bool start, bool stop)
    {
        var inputs = new bool[16];
        inputs[IoPointMap.In.Start] = start;
        inputs[IoPointMap.In.Stop] = stop;
        return inputs;
    }

    [Theory]
    [InlineData(true, false, DrivingMode.Drive)]
    [InlineData(false, true, DrivingMode.Cart)]
    [InlineData(true, true, DrivingMode.Cart)]
    public async Task OnInput_SelectsMode_AndDoesNotRepeat(bool start, bool stop, DrivingMode expected)
    {
        var control = new IoAmrModeControl();
        var commands = new List<DrivingMode>();
        Task Set(DrivingMode mode, CancellationToken ct) { commands.Add(mode); return Task.CompletedTask; }
        await control.ApplyAsync(Inputs(start, stop), Set);
        await control.ApplyAsync(Inputs(start, stop), Set);
        Assert.Equal(new[] { expected }, commands);
        await control.ApplyAsync(Inputs(false, false), Set);
        await control.ApplyAsync(Inputs(start, stop), Set);
        Assert.Equal(new[] { expected, expected }, commands);
    }

    [Fact]
    public async Task StopOverridesHeldStart_AndFailureCanRetry()
    {
        var control = new IoAmrModeControl();
        var commands = new List<DrivingMode>();
        Task Set(DrivingMode mode, CancellationToken ct) { commands.Add(mode); return Task.CompletedTask; }
        await control.ApplyAsync(Inputs(true, false), Set);
        await Assert.ThrowsAsync<IOException>(() => control.ApplyAsync(Inputs(true, true),
            (_, _) => Task.FromException(new IOException("Disconnected"))));
        await control.ApplyAsync(Inputs(true, true), Set);
        Assert.Equal(new[] { DrivingMode.Drive, DrivingMode.Cart }, commands);
    }

    [Fact]
    public async Task OffOrIncompleteInputs_DoNotCommand()
    {
        var control = new IoAmrModeControl();
        Task Set(DrivingMode mode, CancellationToken ct) => throw new InvalidOperationException();
        await control.ApplyAsync(Inputs(false, false), Set);
        await control.ApplyAsync(new[] { false, false, true }, Set);
    }
}

public class IoStartStopLampControlTests
{
    [Theory]
    [InlineData(DrivingMode.Drive, true)]
    [InlineData(DrivingMode.Cart, false)]
    public async Task DrivingMode_SelectsMutuallyExclusiveLamp_AndDoesNotRepeat(
        DrivingMode mode, bool expectedStart)
    {
        var control = new IoStartStopLampControl();
        var commands = new List<bool>();
        Task Set(bool startSelected, CancellationToken ct)
        {
            commands.Add(startSelected);
            return Task.CompletedTask;
        }

        await control.ApplyAsync(mode, Set);
        await control.ApplyAsync(mode, Set);

        Assert.Equal(new[] { expectedStart }, commands);
    }

    [Fact]
    public async Task ModeChangeIsApplied_AndFailedWriteIsRetried()
    {
        var control = new IoStartStopLampControl();
        var commands = new List<bool>();
        Task Set(bool startSelected, CancellationToken ct)
        {
            commands.Add(startSelected);
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<IOException>(() => control.ApplyAsync(
            DrivingMode.Drive, (_, _) => Task.FromException(new IOException("Disconnected"))));
        await control.ApplyAsync(DrivingMode.Drive, Set);
        await control.ApplyAsync(DrivingMode.Cart, Set);

        Assert.Equal(new[] { true, false }, commands);
    }

    [Fact]
    public async Task UnknownMode_DoesNotWrite()
    {
        var control = new IoStartStopLampControl();
        await control.ApplyAsync((DrivingMode)0,
            (_, _) => throw new InvalidOperationException());
    }
}
