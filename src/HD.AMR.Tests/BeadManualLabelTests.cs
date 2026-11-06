using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Tests;

public class BeadManualLabelTests
{
    [Theory]
    [InlineData("off", BeadManualTrigger.Off)]
    [InlineData("failure", BeadManualTrigger.OnFailure)]
    [InlineData("LowConfidence", BeadManualTrigger.OnLowConfidence)]
    [InlineData(" always ", BeadManualTrigger.Always)]
    [InlineData("매번", BeadManualTrigger.Always)]
    public void ParseTrigger_AcceptsParamValues(string value, BeadManualTrigger expected)
        => Assert.Equal(expected, BeadManualSettings.ParseTrigger(value));

    [Fact]
    public void ParseTrigger_UnknownIsNull() => Assert.Null(BeadManualSettings.ParseTrigger("maybe"));

    [Fact]
    public void ToParam_RoundTrips()
    {
        foreach (var t in Enum.GetValues<BeadManualTrigger>())
            Assert.Equal(t, BeadManualSettings.ParseTrigger(BeadManualSettings.ToParam(t)));
    }

    [Theory]
    // trigger, autoOk, confidence → ask?
    [InlineData(BeadManualTrigger.Off, false, 0.0, false)]
    [InlineData(BeadManualTrigger.OnFailure, false, 0.9, true)]
    [InlineData(BeadManualTrigger.OnFailure, true, 0.1, false)]
    [InlineData(BeadManualTrigger.OnLowConfidence, true, 0.3, true)]
    [InlineData(BeadManualTrigger.OnLowConfidence, true, 0.8, false)]
    [InlineData(BeadManualTrigger.OnLowConfidence, false, 0.9, true)]
    [InlineData(BeadManualTrigger.Always, true, 1.0, true)]
    public void ShouldAsk_FollowsTrigger(BeadManualTrigger t, bool autoOk, double conf, bool expected)
        => Assert.Equal(expected, BeadManualSettings.ShouldAsk(t, autoOk, conf, 0.5));

    [Fact]
    public void CrossAt_HorizontalLine_InterpolatesAtPeak()
    {
        // 가로 진행: y = 0.1·x + 200 → x=320 에서 y=232.
        var pts = new[] { new PixelPoint(100, 210), new PixelPoint(600, 260) };
        Assert.Equal(232, ManualBeadGeometry.CrossAt(pts, horizontal: true, progressPos: 320)!.Value, 6);
    }

    [Fact]
    public void CrossAt_VerticalProgress_UsesYAsProgress()
    {
        // 세로 진행: x = 300 고정.
        var pts = new[] { new PixelPoint(300, 50), new PixelPoint(300, 400), new PixelPoint(300, 200) };
        Assert.Equal(300, ManualBeadGeometry.CrossAt(pts, horizontal: false, progressPos: 240)!.Value, 6);
    }

    [Fact]
    public void CrossAt_LeastSquaresOverManyPoints()
    {
        var pts = new[] { new PixelPoint(0, 99), new PixelPoint(100, 101), new PixelPoint(200, 99), new PixelPoint(300, 101) };
        Assert.Equal(100, ManualBeadGeometry.CrossAt(pts, true, 150)!.Value, 1);
    }

    [Fact]
    public void CrossAt_RejectsDegenerateLine()
    {
        Assert.Null(ManualBeadGeometry.CrossAt(new[] { new PixelPoint(10, 10) }, true, 0));
        // 진행축과 수직으로 그린 선(가로 진행인데 x 가 같음) → 교점 불가.
        Assert.Null(ManualBeadGeometry.CrossAt(new[] { new PixelPoint(320, 10), new PixelPoint(320.5, 400) }, true, 320));
    }

    private static ManualBeadRequest Req() => new()
    {
        PeakId = 1,
        IrGuide = new ManualBeadGuide("ir", 640, 480, true, 320, 240, true, null, null, null, null, null),
    };

    [Fact]
    public async Task Hub_SubmitCompletesAndClearsPending()
    {
        var hub = new BeadManualLabelHub();
        var req = Req();
        var task = hub.AskAsync(req, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Same(req, hub.Pending);

        var answer = new ManualBeadAnswer(ManualBeadDecision.Manual, "ir", new[] { new PixelPoint(0, 0), new PixelPoint(10, 1) });
        Assert.True(hub.Submit(req.Id, answer));
        Assert.Same(answer, await task);
        Assert.Null(hub.Pending);
        Assert.False(hub.Submit(req.Id, answer));   // 이미 끝난 요청
    }

    [Fact]
    public async Task Hub_TimesOut()
    {
        var hub = new BeadManualLabelHub();
        var a = await hub.AskAsync(Req(), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Equal(ManualBeadDecision.Timeout, a.Decision);
        Assert.Null(hub.Pending);
    }

    [Fact]
    public async Task Hub_CancellationThrowsAndClears()
    {
        var hub = new BeadManualLabelHub();
        using var cts = new CancellationTokenSource();
        var task = hub.AskAsync(Req(), TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Null(hub.Pending);
    }

    [Fact]
    public async Task Hub_WrongRequestIdIgnored()
    {
        var hub = new BeadManualLabelHub();
        var task = hub.AskAsync(Req(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.False(hub.Submit(Guid.NewGuid(), ManualBeadAnswer.Of(ManualBeadDecision.Reject)));
        Assert.Equal(ManualBeadDecision.Timeout, (await task).Decision);
    }
}
