using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>모델별 조작반 IO 배치. 전역 선택(IoPointMap.Select)은 다른 테스트와 겹치므로 배치 객체만 본다.</summary>
public class IoPanelLayoutTests
{
    [Fact]
    public void 가스검사_AMR_입력은_STOP과_AUTO가_서로_바뀐다()
    {
        var g = IoPanelLayout.GasDetector;
        var inputs = g.BuildInputs();

        Assert.Equal("EMO BUTTON", inputs[0]?.Code);
        Assert.Equal("RESET", inputs[1]?.Code);
        Assert.Equal("START", inputs[2]?.Code);
        Assert.Equal("STOP", inputs[3]?.Code);
        Assert.Equal("MANUAL", inputs[4]?.Code);
        Assert.Equal("AUTO", inputs[5]?.Code);
        Assert.All(inputs.Skip(6), Assert.Null);

        Assert.Equal(3, g.InStop);
        Assert.Equal(5, g.InAuto);
    }

    [Fact]
    public void 가스검사_AMR_출력_램프_배치()
    {
        var g = IoPanelLayout.GasDetector;
        var outputs = g.BuildOutputs();

        Assert.Equal("TOWER LAMP RED", outputs[0]?.Code);
        Assert.Equal("TOWER LAMP YELLOW", outputs[1]?.Code);
        Assert.Equal("TOWER LAMP GREEN", outputs[2]?.Code);
        Assert.Equal("BUZZER", outputs[3]?.Code);
        Assert.Equal("RESET BUTTON LAMP", outputs[4]?.Code);
        Assert.Equal("START BUTTON LAMP", outputs[5]?.Code);
        Assert.Equal("STOP BUTTON LAMP", outputs[6]?.Code);
        Assert.Equal("A/M BUTTON LAMP", outputs[7]?.Code);
        Assert.Null(outputs[8]);
        Assert.Equal("GUIDE LAMP", outputs[9]?.Code);
        Assert.Equal("POWER LAMP", outputs[10]?.Code);
        Assert.All(outputs.Skip(11), Assert.Null);
    }

    [Fact]
    public void 가스검사_AMR_은_EMO_출력이_없고_용접검사_AMR_은_전원_램프가_없다()
    {
        Assert.Equal(-1, IoPanelLayout.GasDetector.OutEmergencyStop);
        Assert.Equal(10, IoPanelLayout.GasDetector.OutPowerLamp);
        Assert.Equal(10, IoPanelLayout.WeldInspection.OutEmergencyStop);
        Assert.Equal(-1, IoPanelLayout.WeldInspection.OutPowerLamp);
    }

    [Fact]
    public void 기본_배치는_용접검사_AMR_이다()
    {
        Assert.Same(IoPanelLayout.WeldInspection, IoPointMap.Layout);
        Assert.Equal("EMO", IoPanelLayout.WeldInspection.BuildOutputs()[10]?.Code);
        Assert.Equal("STOP", IoPanelLayout.WeldInspection.BuildInputs()[5]?.Code);
    }
}
