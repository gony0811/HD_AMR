using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Tests;

/// <summary>전역 실행 잠금 — 실행 주체 추적(다른 화면에서 ACS 실행을 보고 멈추기 위함).</summary>
public class SequenceRunGateTests
{
    [Fact]
    public async Task 실행_주체가_없으면_정지할_것이_없다()
    {
        var gate = new SequenceRunGate();
        Assert.False(gate.HasActiveSequence);
        Assert.Null(gate.ActiveAcsOrderId);
        Assert.False(await gate.StopActiveAsync());
    }

    [Fact]
    public void 조그처럼_시퀀스가_아닌_잠금은_실행_중_시퀀스로_보지_않는다()
    {
        var gate = new SequenceRunGate();
        var changed = 0;
        gate.Changed += () => changed++;
        Assert.True(gate.TryEnter());
        Assert.True(gate.IsBusy);
        Assert.False(gate.HasActiveSequence);
        gate.Exit();
        Assert.False(gate.IsBusy);
        Assert.Equal(2, changed);
    }
}
