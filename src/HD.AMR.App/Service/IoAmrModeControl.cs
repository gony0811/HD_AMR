using HD.AMR.App.Communication;
using HD.AMR.App.Enums;

namespace HD.AMR.App.Service;

/// <summary>새로 읽은 버튼 입력으로 AMR 모드를 전환한다. 동시 입력은 STOP 우선.</summary>
public sealed class IoAmrModeControl
{
    private DrivingMode? _appliedMode;

    public async Task ApplyAsync(bool[] inputs,
        Func<DrivingMode, CancellationToken, Task> setMode, CancellationToken ct = default)
    {
        // 불완전한 입력에서는 START만 보고 주행 모드를 선택하지 않는다.
        if (inputs.Length <= Math.Max(IoPointMap.In.Stop, IoPointMap.In.Start))
            return;

        DrivingMode? requested = inputs[IoPointMap.In.Stop] ? DrivingMode.Cart
            : inputs[IoPointMap.In.Start] ? DrivingMode.Drive : null;
        if (requested is null)
        {
            _appliedMode = null;
            return;
        }

        if (requested == _appliedMode)
            return;

        await setMode(requested.Value, ct);
        // 실패한 명령은 다음 정상 입력 폴링에서 재시도한다.
        _appliedMode = requested;
    }
}

/// <summary>
/// AMR 주행 모드에 맞춰 START/STOP 버튼 램프를 상호 배타적으로 전환한다.
/// </summary>
public sealed class IoStartStopLampControl
{
    private DrivingMode? _appliedMode;

    public async Task ApplyAsync(DrivingMode mode,
        Func<bool, CancellationToken, Task> setLamps, CancellationToken ct = default)
    {
        if (mode is not (DrivingMode.Drive or DrivingMode.Cart))
            return;

        if (mode == _appliedMode)
            return;

        await setLamps(mode == DrivingMode.Drive, ct);
        // 쓰기에 실패하면 다음 입력 폴링에서 다시 시도한다.
        _appliedMode = mode;
    }
}
