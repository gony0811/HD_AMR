using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using static HD.AMR.App.Service.Sequence.Steps.SequenceEntry;

namespace HD.AMR.Tests;

/// <summary>홈 위치_3 자세 모드(2026-11-17 리프트 탈거 후) — 바닥·하부 챔퍼(B/PL/SL)는 홈 위치_3 에서 출발·복귀한다.
/// 홈 위치_2 와 같이 자동 운전은 모드를 바꾸지 않는다.</summary>
public class Home3PoseModeTests
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
    private static readonly TeachingPosition Home3 = Taught("home3", -3, -21, 102, -81, 88, -180);
    private static readonly TeachingPosition SlotB = Taught(TeachingService.WallSlotKey("B"), -10, -30, 110, -80, 88, -180);

    [Theory]
    [InlineData("B", true)]
    [InlineData("PL", true)]
    [InlineData("SL", true)]
    [InlineData("F", false)]
    [InlineData("T", false)]
    [InlineData("PU", false)]
    [InlineData(null, false)]
    public void IsLowerRoute_ByWallCode(string? wall, bool expected)
        => Assert.Equal(expected, IsLowerRoute(wall));

    [Theory]
    [InlineData("B", true, "Home3")]
    [InlineData("SL", true, "Home3")]
    [InlineData("B", false, "Home")]     // 홈 위치_3 미티칭이면 종전 홈 모드
    [InlineData("F", true, "Home")]
    [InlineData("T", true, "Home2")]
    public void RequiredMode_ByWallAndTeaching(string wall, bool home3Taught, string expected)
        => Assert.Equal(Enum.Parse<PoseMode>(expected), RequiredMode(wall, home3Taught));

    [Theory]
    [InlineData("Home", "B", true)]
    [InlineData("Home2", "PL", true)]
    [InlineData("Home3", "F", true)]
    [InlineData("Home3", "T", true)]
    [InlineData("Home3", "B", false)]
    [InlineData("Home3", "SL", false)]
    public void ModeMismatch_Home3(string mode, string wall, bool blocked)
    {
        var msg = ModeMismatch(Enum.Parse<PoseMode>(mode), wall, home3Taught: true);
        Assert.Equal(blocked, msg is not null);
        if (blocked) Assert.Contains("수동 자세 전환 필요", msg);
    }

    [Fact]
    public void 홈3_미티칭이면_바닥도_종전처럼_홈_모드()
        => Assert.Null(ModeMismatch(PoseMode.Home, "B", home3Taught: false));

    [Fact]
    public void ModeOf_홈3_가까우면_Home3()
    {
        var ctx = Ctx(null, Home, Ready, Home2, Home3);
        Assert.Equal(PoseMode.Home3, ModeOf(new double[] { -4, -22, 100, -80, 88, -179 }, ctx));
        Assert.Equal(PoseMode.Home, ModeOf(new double[] { 0, -89, 91, 0, 90, 0 }, ctx));
        Assert.Equal(PoseMode.Home2, ModeOf(new double[] { 61, -100, -40, 30, 90, 0 }, ctx));
    }

    [Fact]
    public void ValidateRoute_하부면은_홈3만_있으면_된다()
    {
        Assert.True(ValidateRoute(Ctx("B", Home, Home3)).IsValid);
        Assert.False(ValidateRoute(Ctx("B", Home)).IsValid);        // 홈 3·ready 모두 없음
        Assert.True(ValidateRoute(Ctx("PL", Home, Ready)).IsValid);  // 홈 3 미티칭 → 종전 ready
    }

    [Fact]
    public void 기준_자세_위치_판정()
    {
        var ctx = Ctx("B", Home, Ready, Home3);
        Assert.True(IsAtBaseHome(new double[] { -3, -21, 102, -81, 88, -180 }, ctx, out var key, out var name));
        Assert.Equal("home3", key);
        Assert.Equal("홈 위치_3", name);
    }

    [Fact]
    public void 복귀_경유는_하부_면_검사_준비_슬롯()
    {
        var nearB = new double[] { -9, -29, 109, -80, 88, -180 };
        Assert.Equal("B", ModeViaSlot(nearB, Ctx("B", Home, Home3, SlotB), PoseMode.Home3)?.Code);
        Assert.Null(ModeViaSlot(nearB, Ctx("SL", Home, Home3, SlotB), PoseMode.Home3));   // 다른 면 슬롯은 빌리지 않는다
        Assert.Null(ModeViaSlot(nearB, Ctx("B", Home, Home3), PoseMode.Home3));           // 슬롯 미티칭 → 바로 홈 3
    }
}
