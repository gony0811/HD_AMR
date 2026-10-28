using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class HomeRouteTests
{
    private static TeachingPosition Taught(string key, params double[] j) => new()
    {
        Key = key, Name = key,
        X = 0, Y = 0, Z = 0, Rx = 0, Ry = 0, Rz = 0,
        J1 = j[0], J2 = j[1], J3 = j[2], J4 = j[3], J5 = j[4], J6 = j[5],
    };

    private static SequenceContext Ctx(string? wall, params TeachingPosition[] ps) => new()
    {
        WallCode = wall,
        Positions = ps.ToDictionary(p => p.Key, p => p),
    };

    private static readonly TeachingPosition Home = Taught("home", 0, -90, 90, 0, 90, 0);
    private static readonly TeachingPosition Ready = Taught("ready", 0, -80, 100, -20, 90, 0);
    private static readonly TeachingPosition Home15 = Taught("home15", 30, -90, 60, 0, 90, 0);
    private static readonly TeachingPosition Home2 = Taught("home2", 60, -100, 40, 30, 90, 0);

    // 천장·상부 챔퍼만 상부 경로 — 하부 챔퍼·수직벽·바닥·미지정은 종전 ready 경로.
    [Theory]
    [InlineData("T", true)]
    [InlineData("PU", true)]
    [InlineData("SU", true)]
    [InlineData("PL", false)]
    [InlineData("F", false)]
    [InlineData("B", false)]
    [InlineData(null, false)]
    public void IsUpperRoute_ByWallCode(string? wall, bool expected)
        => Assert.Equal(expected, SequenceEntry.IsUpperRoute(wall));

    [Fact]
    public void ValidateRoute_UpperWall_RequiresBothHomeSlots()
    {
        var r = SequenceEntry.ValidateRoute(Ctx("T", Home, Ready, Home15));
        Assert.False(r.IsValid);
        Assert.True(SequenceEntry.ValidateRoute(Ctx("T", Home, Home15, Home2)).IsValid);   // ready 없어도 OK
    }

    [Fact]
    public void ValidateRoute_NormalWall_RequiresReadyOnly()
    {
        Assert.False(SequenceEntry.ValidateRoute(Ctx("F", Home, Home15, Home2)).IsValid);
        Assert.True(SequenceEntry.ValidateRoute(Ctx("F", Home, Ready)).IsValid);
    }

    // 복귀 경로는 현재 자세로 — 홈 2 쪽에 가까우면 상부 경로, ready 쪽이면 종전 경로.
    [Fact]
    public void IsUpperSide_PicksNearerWaypoint()
    {
        var ctx = Ctx(null, Home, Ready, Home15, Home2);
        Assert.True(SequenceEntry.IsUpperSide(new double[] { 65, -95, 35, 40, 80, 10 }, ctx));
        Assert.False(SequenceEntry.IsUpperSide(new double[] { 5, -75, 105, -30, 85, 5 }, ctx));
        Assert.True(SequenceEntry.IsUpperSide(new double[] { 30, -90, 60, 0, 90, 0 }, ctx));   // 홈 1.5 위
    }

    // 홈 1.5·2 를 티칭하지 않았으면 언제나 종전 경로.
    [Fact]
    public void IsUpperSide_FalseWhenHomeSlotsNotTaught()
    {
        var ctx = Ctx(null, Home, Ready, Home2);
        Assert.False(SequenceEntry.IsUpperSide(new double[] { 60, -100, 40, 30, 90, 0 }, ctx));
    }

    [Fact]
    public void JointDistance_WrapsAround360()
        => Assert.Equal(1.0, SequenceEntry.JointDistance(
            new double[] { 0, 0, 0, 0, 0, 179.5 }, new double[] { 0, 0, 0, 0, 0, -179.5 }), 6);
}
