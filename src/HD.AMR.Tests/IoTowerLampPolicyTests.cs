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
}
