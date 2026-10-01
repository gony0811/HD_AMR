using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// 층 전환 재측위의 <b>수렴 판정</b> 회귀. 이 판정이 느슨하면 다른 층 좌표를 들고 mapId 만 바뀐 state 가
/// ACS 로 나가고, 이후 /robot/go 가 엉뚱한 위치 기준으로 발행된다.
/// </summary>
public class FloorRelocalizationTests
{
    private static readonly FloorVerifyCriteria C = new()
    {
        MinMapMatchPercent = 30, MaxPosErrorM = 0.5, MaxAngleErrorDeg = 10,
    };

    private static RobotPose P(double x, double y, double deg)
        => new((float)x, (float)y, (float)(deg * Math.PI / 180));

    [Fact]
    public void Pass_WhenConvergedNearInitPose()
    {
        var e = FloorRelocalization.Evaluate(P(10.2, 5.1, 92), 75, new FloorInitPose(10, 5, 90), C);
        Assert.True(e.Pass);
        Assert.InRange(e.PosErrorM, 0.22, 0.23);
        Assert.InRange(e.AngleErrorDeg, 1.9, 2.1);
    }

    /// <summary>맵 일치율이 낮으면 위치가 initpose 와 같아도 실패 — PoseSearch 가 좌표만 옮기고 매칭은 못 한 상태.</summary>
    [Fact]
    public void Fail_WhenMapMatchLow_EvenIfPoseEqualsTarget()
    {
        var e = FloorRelocalization.Evaluate(P(10, 5, 90), 12, new FloorInitPose(10, 5, 90), C);
        Assert.False(e.Pass);
        Assert.False(e.MapOk);
        Assert.True(e.PosOk);
    }

    [Fact]
    public void Fail_WhenConvergedFarFromInitPose()
    {
        var e = FloorRelocalization.Evaluate(P(11, 5, 90), 80, new FloorInitPose(10, 5, 90), C);
        Assert.False(e.PosOk);
        Assert.False(e.Pass);
    }

    /// <summary>±180° 경계: 179° 와 -179° 는 2° 차이다(래핑 미처리 시 358° 로 오판).</summary>
    [Fact]
    public void AngleError_WrapsAcrossPi()
    {
        var e = FloorRelocalization.Evaluate(P(0, 0, -179), 80, new FloorInitPose(0, 0, 179), C);
        Assert.InRange(e.AngleErrorDeg, 1.9, 2.1);
        Assert.True(e.AngleOk);
    }

    [Fact]
    public void Fail_WhenHeadingReversed()
    {
        var e = FloorRelocalization.Evaluate(P(0, 0, 270), 80, new FloorInitPose(0, 0, 90), C);
        Assert.InRange(e.AngleErrorDeg, 179.9, 180.1);
        Assert.False(e.AngleOk);
    }

    [Fact]
    public void Keys_FollowConvention()
    {
        Assert.Equal("Floor.CT1-L2.InitPose.X", FloorRelocalization.KeyX("CT1-L2"));
        Assert.Equal("Floor.CT1-L2.InitPose.Y", FloorRelocalization.KeyY("CT1-L2"));
        Assert.Equal("Floor.CT1-L2.InitPose.ThetaDeg", FloorRelocalization.KeyThetaDeg("CT1-L2"));
    }
}
