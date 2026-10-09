using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class IoTowerLampPolicyTests
{
    [Fact]
    public void 자동은_녹색만_켠다() =>
        Assert.Equal(new TowerLampState(false, false, true), IoTowerLampPolicy.Resolve(true, false, false));

    [Fact]
    public void 수동은_황색만_켠다() =>
        Assert.Equal(new TowerLampState(false, true, false), IoTowerLampPolicy.Resolve(false, true, false));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void 알람은_운전모드보다_우선하여_적색만_켠다(bool auto, bool manual) =>
        Assert.Equal(new TowerLampState(true, false, false), IoTowerLampPolicy.Resolve(auto, manual, true));

    [Fact]
    public void 모드_신호가_없으면_기존_출력을_유지한다() =>
        Assert.Null(IoTowerLampPolicy.Resolve(false, false, false));

    // 화면 비상정지(EMO 출력 OUT10)는 입력으로 되돌아오지 않는다 — 출력 되읽기만으로도 적색이어야 한다.
    [Theory]
    [InlineData(true, false, false, true)]    // 조작반 EMO 입력
    [InlineData(false, true, false, true)]    // 화면 EMO 출력
    [InlineData(false, false, true, true)]    // 조작반 EMO 래치(RESET 전)
    [InlineData(false, false, false, false)]
    public void IsAlarm_CoversAllEmergencyStopPaths(bool emoIn, bool emoOut, bool latched, bool expected)
    {
        var inputs = new bool[16];
        inputs[HD.AMR.App.Communication.IoPointMap.In.EmergencyStop] = emoIn;
        var outputs = new bool[16];
        outputs[HD.AMR.App.Communication.IoPointMap.Out.EmergencyStop] = emoOut;

        Assert.Equal(expected, IoTowerLampPolicy.IsAlarm(inputs, outputs, latched, false, false));
    }
}
