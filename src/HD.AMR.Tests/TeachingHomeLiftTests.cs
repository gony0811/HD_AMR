using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>Teaching '이동' — 홈/홈 2 도착 후 리프트 0mm 하강 대상 판정과 결과 문구.</summary>
public class TeachingHomeLiftTests
{
    private static TeachingPosition Pos(string key, string name) => new() { Key = key, Name = name };

    [Theory]
    [InlineData("home", true)]
    [InlineData("home2", true)]
    [InlineData("home15", false)]
    [InlineData("home16", false)]
    [InlineData("ready", false)]
    [InlineData("wall.F", false)]
    public void 홈과_홈2만_리프트를_내린다(string key, bool expected)
        => Assert.Equal(expected, TeachingMove.LowersLiftOnArrival(Pos(key, key)));

    [Fact]
    public void 리프트_하강_실패는_전체_실패로_보고한다()
    {
        var home = Pos("home", "홈 위치");
        var r = new TeachingMove.RouteMoveResult(0, new([home]),
            Lift: TelescopicMoveResult.Fail("미연결"));
        Assert.True(r.CobotOk);
        Assert.False(r.Ok);
        Assert.Contains("하강 실패", TeachingMove.Describe(r, home));
    }

    [Fact]
    public void 리프트_하강_성공은_완료_문구에_붙는다()
    {
        var home2 = Pos("home2", "홈 위치_2");
        var r = new TeachingMove.RouteMoveResult(0, new([home2]),
            Lift: new TelescopicMoveResult(true, 1, Moved: true, "ok"));
        Assert.True(r.Ok);
        Assert.Contains("하강 완료", TeachingMove.Describe(r, home2));
    }
}
