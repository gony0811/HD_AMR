using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using static HD.AMR.App.Service.Sequence.Steps.SequenceEntry;

namespace HD.AMR.Tests;

/// <summary>홈 위치_2 자세 모드 운영안(2026-10-29) — 자동 운전은 모드를 바꾸지 않고, 면과 모드가 맞지 않으면 멈춘다.</summary>
public class HomePoseModeTests
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
    private static readonly TeachingPosition Home2 = Taught("home2", 60, -100, -40, 30, 90, 0);
    private static readonly TeachingPosition SlotT = Taught(TeachingService.WallSlotKey("T"), 70, -110, -60, 40, 80, 0);
    private static readonly TeachingPosition SlotSU = Taught(TeachingService.WallSlotKey("SU"), 90, -100, -50, 40, 80, 0);

    // 천장·상부 챔퍼만 홈 위치_2 모드 면 — 하부 챔퍼·수직벽·바닥·미지정은 홈 모드.
    [Theory]
    [InlineData("T", true)]
    [InlineData("PU", true)]
    [InlineData("SU", true)]
    [InlineData("PL", false)]
    [InlineData("F", false)]
    [InlineData("B", false)]
    [InlineData(null, false)]
    public void IsUpperRoute_ByWallCode(string? wall, bool expected)
        => Assert.Equal(expected, IsUpperRoute(wall));

    // 모드는 가까운 쪽 — 홈 위치_2 쪽이면 Home2, ready 쪽이면 Home.
    [Fact]
    public void ModeOf_PicksNearerBase()
    {
        var ctx = Ctx(null, Home, Ready, Home2);
        Assert.Equal(PoseMode.Home2, ModeOf(new double[] { 65, -105, -45, 35, 85, 5 }, ctx));
        Assert.Equal(PoseMode.Home, ModeOf(new double[] { 5, -82, 95, -15, 88, 0 }, ctx));
        Assert.Equal(PoseMode.Home, ModeOf(new double[] { 0, -90, 90, 0, 90, 0 }, ctx));
    }

    // 홈 위치_2 미티칭이면 언제나 홈 모드(종전 동작).
    [Fact]
    public void ModeOf_HomeWhenHome2NotTaught()
        => Assert.Equal(PoseMode.Home, ModeOf(new double[] { 60, -100, -40, 30, 90, 0 }, Ctx(null, Home, Ready)));

    // 면과 모드가 어긋나면 실패 사유(수동 자세 전환 필요), 맞으면 null.
    [Theory]
    [InlineData(false, "T", true)]
    [InlineData(false, "SU", true)]
    [InlineData(true, "F", true)]
    [InlineData(true, "T", false)]
    [InlineData(false, "F", false)]
    public void ModeMismatch_BlocksCrossModeWork(bool atHome2, string wall, bool blocked)
    {
        var msg = ModeMismatch(atHome2 ? PoseMode.Home2 : PoseMode.Home, wall);
        Assert.Equal(blocked, msg is not null);
        if (blocked) Assert.Contains("수동 자세 전환 필요", msg);
    }

    [Fact]
    public void ValidateRoute_UpperNeedsHome2_NormalNeedsReady()
    {
        Assert.False(ValidateRoute(Ctx("T", Home, Ready)).IsValid);
        Assert.True(ValidateRoute(Ctx("T", Home, Home2)).IsValid);
        Assert.False(ValidateRoute(Ctx("F", Home, Home2)).IsValid);
        Assert.True(ValidateRoute(Ctx("F", Home, Ready)).IsValid);
    }

    // 복귀 경유 슬롯 — 작업 면 슬롯이 홈 위치_2 보다 가까울 때만, 면이 없으면 가장 가까운 상부 슬롯.
    [Fact]
    public void UpperViaSlot_UsesWallSlotWhenCloserThanHome2()
    {
        var nearT = new double[] { 72, -112, -62, 42, 78, 0 };
        Assert.Equal("T", UpperViaSlot(nearT, Ctx("T", Home, Home2, SlotT, SlotSU))?.Code);
        Assert.Equal("T", UpperViaSlot(nearT, Ctx(null, Home, Home2, SlotT, SlotSU))?.Code);   // 주행 전 복귀
        Assert.Null(UpperViaSlot(new double[] { 61, -100, -40, 30, 90, 0 }, Ctx("T", Home, Home2, SlotT)));
        Assert.Null(UpperViaSlot(nearT, Ctx("T", Home, Home2)));   // 검사 준비 미티칭 → 바로 홈 위치_2
    }

    [Fact]
    public void JointDistance_WrapsAround360()
        => Assert.Equal(1.0, JointDistance(
            new double[] { 0, 0, 0, 0, 0, 179.5 }, new double[] { 0, 0, 0, 0, 0, -179.5 }), 6);
}
