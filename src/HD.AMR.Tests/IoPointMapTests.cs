using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

public class IoPointMapTests
{
    // 2026-09-18 현장 IO 리스트 — docs/IO_MODULE_POINTS.md 표와 1:1. 배선표가 바뀌면 이 테스트로 드러난다.
    [Theory]
    [InlineData(0, "EMO BUTTON")]
    [InlineData(1, "RESET")]
    [InlineData(2, "START")]
    [InlineData(3, "AUTO")]
    [InlineData(4, "MANUAL")]
    [InlineData(5, "STOP")]
    public void Input_MatchesFieldWiringList(int index, string code)
    {
        Assert.Equal(code, IoPointMap.Input(index)?.Code);
    }

    [Theory]
    [InlineData(0, "TOWER LAMP RED")]
    [InlineData(1, "TOWER LAMP YELLOW")]
    [InlineData(2, "TOWER LAMP GREEN")]
    [InlineData(3, "BUZZER")]
    [InlineData(4, "START BUTTON LAMP")]
    [InlineData(5, "RESET BUTTON LAMP")]
    [InlineData(6, "A/M BUTTON LAMP")]
    [InlineData(7, "STOP BUTTON LAMP")]
    [InlineData(9, "GUIDE LAMP")]
    [InlineData(10, "EMO")]
    public void Output_MatchesFieldWiringList(int index, string code)
    {
        Assert.Equal(code, IoPointMap.Output(index)?.Code);
    }

    // 미배선 접점에 의미를 부여하지 않는다 — null + "미배선" 라벨.
    [Theory]
    [InlineData(6)]
    [InlineData(15)]
    public void Input_Unwired_IsNull(int index)
    {
        Assert.Null(IoPointMap.Input(index));
        Assert.Equal("미배선", IoPointMap.InputLabel(index));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(15)]
    public void Output_Unwired_IsNull(int index)
    {
        Assert.Null(IoPointMap.Output(index));
        Assert.Equal("미배선", IoPointMap.OutputLabel(index));
    }

    // 인덱스 상수와 표가 어긋나면(예: 표만 수정) 여기서 잡힌다.
    [Fact]
    public void IndexConstants_PointAtTheirOwnSignal()
    {
        Assert.Equal("EMO BUTTON", IoPointMap.Input(IoPointMap.In.EmergencyStop)?.Code);
        Assert.Equal("RESET", IoPointMap.Input(IoPointMap.In.Reset)?.Code);
        Assert.Equal("START", IoPointMap.Input(IoPointMap.In.Start)?.Code);
        Assert.Equal("AUTO", IoPointMap.Input(IoPointMap.In.Auto)?.Code);
        Assert.Equal("MANUAL", IoPointMap.Input(IoPointMap.In.Manual)?.Code);
        Assert.Equal("STOP", IoPointMap.Input(IoPointMap.In.Stop)?.Code);

        Assert.Equal("TOWER LAMP RED", IoPointMap.Output(IoPointMap.Out.TowerLampRed)?.Code);
        Assert.Equal("TOWER LAMP YELLOW", IoPointMap.Output(IoPointMap.Out.TowerLampYellow)?.Code);
        Assert.Equal("TOWER LAMP GREEN", IoPointMap.Output(IoPointMap.Out.TowerLampGreen)?.Code);
        Assert.Equal("BUZZER", IoPointMap.Output(IoPointMap.Out.Buzzer)?.Code);
        Assert.Equal("START BUTTON LAMP", IoPointMap.Output(IoPointMap.Out.StartButtonLamp)?.Code);
        Assert.Equal("RESET BUTTON LAMP", IoPointMap.Output(IoPointMap.Out.ResetButtonLamp)?.Code);
        Assert.Equal("A/M BUTTON LAMP", IoPointMap.Output(IoPointMap.Out.AutoManualButtonLamp)?.Code);
        Assert.Equal("STOP BUTTON LAMP", IoPointMap.Output(IoPointMap.Out.StopButtonLamp)?.Code);
        Assert.Equal("GUIDE LAMP", IoPointMap.Output(IoPointMap.Out.GuideLamp)?.Code);
        Assert.Equal("EMO", IoPointMap.Output(IoPointMap.Out.EmergencyStop)?.Code);
    }

    // 맵 크기 = 모듈 점수(DC16A 16점 / TN16A 16점). 범위 밖은 예외 없이 null.
    [Fact]
    public void OutOfRange_IsNullNotThrow()
    {
        Assert.Null(IoPointMap.Input(-1));
        Assert.Null(IoPointMap.Input(16));
        Assert.Null(IoPointMap.Output(-1));
        Assert.Null(IoPointMap.Output(16));
    }
}
