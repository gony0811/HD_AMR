using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>cancelOrder 취소 방식(사양 §5.4) — mode=afterCurrentAction 이면 진행 중 검사를 마친 뒤 중단한다.</summary>
public class CancelPolicyTests
{
    [Theory]
    [InlineData("afterCurrentAction", true)]
    [InlineData("AFTERCURRENTACTION", true)]
    [InlineData(" afterCurrentAction ", true)]
    [InlineData("immediate", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void mode_파라미터_해석(string? mode, bool expected)
        => Assert.Equal(expected, CancelPolicy.IsAfterCurrent(mode));

    [Fact]
    public void 검사가_진행_중이면_끝날_때까지_미룬다()
        => Assert.True(CancelPolicy.ShouldDefer(true, missionRunning: true, paused: false, driving: false, actionRunning: true));

    [Fact]
    public void 요청이_없으면_종전처럼_즉시_취소한다()
        => Assert.False(CancelPolicy.ShouldDefer(false, missionRunning: true, paused: false, driving: false, actionRunning: true));

    [Theory]
    [InlineData(false, false, false, true)]   // 임무 없음
    [InlineData(true, true, false, true)]     // 일시정지 중 — 재개 전 확인 없이 검사를 이어 갈 수 없다
    [InlineData(true, false, true, false)]    // 주행 중 — 끝낼 검사가 없다
    [InlineData(true, false, false, false)]   // 진행 중 액션 없음(주행 전 준비 등)
    public void 끝낼_검사가_없거나_멈춰_있으면_즉시_취소한다(bool running, bool paused, bool driving, bool actionRunning)
        => Assert.False(CancelPolicy.ShouldDefer(true, running, paused, driving, actionRunning));

    [Fact]
    public void instantAction_의_mode_값을_읽는다()
    {
        var action = new VdaAction { ActionType = "cancelOrder", ActionId = "a1" };
        action.ActionParameters.Add(new ActionParameter { Key = "mode", Value = "afterCurrentAction" });
        Assert.True(CancelPolicy.IsAfterCurrent(Vda5050OrderExecutor.GetStringParam(action, "mode")));
        Assert.False(CancelPolicy.IsAfterCurrent(Vda5050OrderExecutor.GetStringParam(
            new VdaAction { ActionType = "cancelOrder", ActionId = "a2" }, "mode")));
    }
}
