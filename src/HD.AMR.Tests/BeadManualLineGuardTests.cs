using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>수동 비드선 방향 검증과 센터링 잔차 실패 판정 — 2026-11-14 A 면 L4-R01C09-6 사례.</summary>
public class BeadManualLineGuardTests
{
    private static PixelPoint[] Pts(params (double X, double Y)[] p) => p.Select(q => new PixelPoint(q.X, q.Y)).ToArray();

    [Fact]
    public void FieldCase_VerticalLineOnHorizontalAxis_IsRejected()
    {
        // 현장에서 입력된 선(RGB 1280x720) — 거의 수직. 종전에는 Peak 위치까지 연장해 d=-5656px 가 됐다.
        var pts = Pts((347.7, 413.6), (343.0, 574.3));
        var why = ManualBeadGeometry.LineProblem(pts, horizontal: true, progressPos: 640, crossExtent: 720);
        Assert.NotNull(why);
        Assert.Contains("세로 방향", why);
        Assert.Contains("조그", why);
        Assert.True(ManualBeadGeometry.TiltDeg(pts, true) > 85);
    }

    [Fact]
    public void LineAlongProgressAxis_IsAccepted()
    {
        Assert.Null(ManualBeadGeometry.LineProblem(Pts((200, 300), (900, 310)), true, 640, 720));
        // 조금 기운 선(약 11°)·여러 점으로 꺾인 선도 받는다
        Assert.Null(ManualBeadGeometry.LineProblem(Pts((200, 300), (700, 400)), true, 640, 720));
        Assert.Null(ManualBeadGeometry.LineProblem(Pts((200, 300), (500, 320), (900, 305)), true, 640, 720));
        // 진행축이 세로면 세로선이 정상
        Assert.Null(ManualBeadGeometry.LineProblem(Pts((640, 100), (650, 600)), false, 360, 1280));
        Assert.NotNull(ManualBeadGeometry.LineProblem(Pts((200, 300), (900, 310)), false, 360, 1280));
    }

    [Fact]
    public void TiltLimit_Is30Degrees()
    {
        // 25° 통과, 35° 거부
        Assert.Null(ManualBeadGeometry.LineProblem(Pts((540, 300), (740, 300 + 200 * Math.Tan(25 * Math.PI / 180))), true, 640, 720));
        Assert.NotNull(ManualBeadGeometry.LineProblem(Pts((540, 300), (740, 300 + 200 * Math.Tan(35 * Math.PI / 180))), true, 640, 720));
    }

    [Fact]
    public void ShortSlantedLineFarFromPeak_ExtrapolatingOffScreen_IsRejected()
    {
        // 기울기는 허용 안(약 27°)이지만 Peak 에서 멀어 연장하면 화면 밖
        var why = ManualBeadGeometry.LineProblem(Pts((20, 300), (60, 320)), true, 1250, 720);
        Assert.NotNull(why);
        Assert.Contains("화면 밖", why);
    }

    [Fact]
    public void DegenerateInput_IsRejected()
    {
        Assert.NotNull(ManualBeadGeometry.LineProblem(Pts((300, 300)), true, 640, 720));
        Assert.NotNull(ManualBeadGeometry.LineProblem(Pts((300, 300), (300, 300)), true, 640, 720));
    }

    [Theory]
    // d, 1회 이동(±100) 뒤 잔차, 실패?
    [InlineData(-59.5, 0.0, false)]
    [InlineData(120.0, 20.0, false)]
    [InlineData(-130.0, -30.0, false)]
    [InlineData(-165.0, -65.0, true)]
    [InlineData(-3788.6, -3688.6, true)]
    public void ResidualAfterClamp_FailsOnlyBeyondPointCorrection(double d, double residual, bool fail)
    {
        Assert.Equal(residual, BeadCenteringStep.ResidualAfterClamp(d), 6);
        Assert.Equal(fail, BeadCenteringStep.IsUncorrectableResidual(BeadCenteringStep.ResidualAfterClamp(d)));
    }

    [Fact]
    public void NonFiniteResidual_IsUncorrectable()
        => Assert.True(BeadCenteringStep.IsUncorrectableResidual(double.NaN));
}
