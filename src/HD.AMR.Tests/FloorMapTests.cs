using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Enums;
using HD.AMR.App.Service;
using Xunit;

namespace HD.AMR.Tests;

/// <summary>층 맵 ID 규칙 — AMR 은 층 번호만 알고 선창 이름은 ACS 가 준 값을 되돌려 보고한다.</summary>
public class FloorMapTests
{
    [Theory]
    [InlineData("CT1-L4", 4)]
    [InlineData("HN8250-CT2-L12", 12)]
    [InlineData("L3", 3)]
    [InlineData("l2", 2)]
    [InlineData("1", 1)]
    [InlineData(" CT1-L2 ", 2)]
    [InlineData("CT1", null)]
    [InlineData("CT1-L0", null)]
    [InlineData("CT1-LX", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void LevelOf_ReadsTheFloorNumber(string? mapId, int? expected) =>
        Assert.Equal(expected, FloorMap.LevelOf(mapId));

    [Theory]
    [InlineData("CT1-L4", "CT1")]
    [InlineData("HN8250-CT2-L1", "HN8250-CT2")]
    [InlineData("L3", null)]
    [InlineData("4", null)]
    [InlineData("CT1", null)]
    public void TankOf_IsThePartBeforeTheFloor(string mapId, string? expected) =>
        Assert.Equal(expected, FloorMap.TankOf(mapId));

    [Fact]
    public void Compose_UsesTheTankNameFromAcs_OrFloorOnlyWhenUnknown()
    {
        Assert.Equal("HN8250-CT2-L3", FloorMap.Compose("HN8250-CT2", 3));
        Assert.Equal("L3", FloorMap.Compose(null, 3));
        Assert.Equal("L3", FloorMap.Compose(" ", 3));
        // 받은 맵 ID 를 쪼갰다 다시 붙이면 같은 값
        Assert.Equal("CT1-L2", FloorMap.Compose(FloorMap.TankOf("CT1-L2"), FloorMap.LevelOf("CT1-L2")!.Value));
    }

    [Fact]
    public void SameLevel_IgnoresTheTankName_ButNotTheFloor()
    {
        Assert.True(FloorMap.SameLevel("HN8250-CT2-L4", "CT1-L4"));   // 선창 ID 를 바꾼 직후의 지령
        Assert.True(FloorMap.SameLevel("CT1-L4", "L4"));
        Assert.False(FloorMap.SameLevel("CT1-L2", "CT1-L1"));          // 다른 층 지령은 여전히 거부
        Assert.False(FloorMap.SameLevel("CT1", "CT1"));                // 층을 읽을 수 없으면 같다고 보지 않는다
        Assert.False(FloorMap.SameLevel(null, "CT1-L1"));
    }

    [Fact]
    public void Levels_ComeFromSettings_ThenLegacyMapIds_ThenDefault()
    {
        Assert.Equal(new[] { 1, 3 }, FloorMap.Levels(new[] { 3, 1, 3, 0 }, new[] { "CT1-L2" }));
        Assert.Equal(new[] { 1, 2, 3, 4 }, FloorMap.Levels(null, new[] { "CT1-L1", "CT1-L2", "CT1-L3", "CT1-L4" }));
        Assert.Equal(new[] { 1, 2, 3, 4 }, FloorMap.Levels(Array.Empty<int>(), Array.Empty<string>()));
    }

    [Fact]
    public void TankNameStore_KeepsTheLastNameAcrossRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "hdamr-tank-" + Guid.NewGuid().ToString("N"), "acs-tank.txt");
        try
        {
            var store = new TankNameStore(path);
            Assert.Null(store.Load());                       // 받은 적 없음 → 설정의 옛 MapId 에서 뽑은 이름을 쓴다
            Assert.True(store.Save("HN8250-CT2"));
            Assert.Equal("HN8250-CT2", new TankNameStore(path).Load());
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void ResumePrecheck_ComparesFloorsOnly()
    {
        ResumePrecheck.Input Ok(string current, string order) => new(
            HasPausedMission: true, EmergencyStopActive: false, PowerMode: PowerMode.Normal,
            PositionInitialized: true, CurrentMapId: current, OrderMapId: order, OperatingMode: "AUTOMATIC");

        Assert.Null(ResumePrecheck.Evaluate(Ok("CT1-L1", "CT1-L1")));
        Assert.Null(ResumePrecheck.Evaluate(Ok("CT1-L1", "HN8250-CT2-L1")));               // 일시정지 사이에 선창 ID 가 바뀜
        Assert.Contains("mapId", ResumePrecheck.Evaluate(Ok("CT1-L2", "HN8250-CT2-L1")));  // 층이 다르면 거부
    }

    [Fact]
    public void OrderValidation_AcceptsAnotherTankName_ButRejectsAnotherFloor()
    {
        static Vda5050Order Order(string mapId) => new()
        {
            OrderId = "o1",
            Nodes = { new OrderNode { NodeId = "n1", NodePosition = new NodePosition { MapId = mapId } } },
        };

        Assert.Null(Vda5050OrderExecutor.Validate(Order("CT1-L4"), "CT1-L4"));
        Assert.Null(Vda5050OrderExecutor.Validate(Order("HN8250-CT2-L4"), "CT1-L4"));   // ACS 가 선창 ID 를 바꿨다 — 같은 층이면 수행
        Assert.Null(Vda5050OrderExecutor.Validate(Order("HN8250-CT2-L4"), "L4"));       // 선창 이름을 아직 받지 못한 AMR
        Assert.Contains("현재 층 불일치", Vda5050OrderExecutor.Validate(Order("HN8250-CT2-L3"), "CT1-L4"));
        Assert.Contains("현재 층 불일치", Vda5050OrderExecutor.Validate(Order("CT1-L1"), "CT1-L4"));
        Assert.Contains("층 번호를 읽을 수 없음", Vda5050OrderExecutor.Validate(Order("CT1"), "CT1-L4"));
        Assert.Contains("mapId 누락", Vda5050OrderExecutor.Validate(Order(""), "CT1-L4"));
    }
}
