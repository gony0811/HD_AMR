using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

/// <summary>
/// 시퀀스 수행 전 코봇 도달·순회 판정. 하드웨어 없이 좌표 환산, 저장 맵 조회, 진행 여부만 본다.
/// 실시간 IK 는 <see cref="ReachMapService"/> 가 맡고, 여기서는 그 앞단(막을지 말지)을 고정한다.
/// </summary>
public class ReachPrecheckTests
{
    [Fact]
    public void 원점_정면이면_맵_좌표가_차체_좌표다()
    {
        var body = ReachStationFrame.WorldToBodyMm(new[] { 1.2, -0.4, 2.0 }, 0, 0, 0, zDatumOffsetMm: 0);
        Assert.Equal(1200, body[0], 6);
        Assert.Equal(-400, body[1], 6);
        Assert.Equal(2000, body[2], 6);
    }

    [Fact]
    public void 정차_위치만큼_평행이동하고_z기준을_뺀다()
    {
        var body = ReachStationFrame.WorldToBodyMm(new[] { 2.0, 3.0, 2.0 }, 2, 3, 0, zDatumOffsetMm: 1000);
        Assert.Equal(0, body[0], 6);
        Assert.Equal(0, body[1], 6);
        Assert.Equal(1000, body[2], 6);
    }

    [Fact]
    public void 요_90도면_맵_Y가_차체_전방이다()
    {
        var body = ReachStationFrame.WorldToBodyMm(new[] { 0.0, 1.0, 0.0 }, 0, 0, Math.PI / 2, 0);
        Assert.Equal(1000, body[0], 3);
        Assert.Equal(0, body[1], 3);
    }

    [Fact]
    public void 바닥은_높이_0으로_평가한다()
    {
        Assert.Equal(0, ReachStationFrame.CellZMm("B", 1500));
        Assert.Equal(1500, ReachStationFrame.CellZMm("SM", 1500));
    }

    [Theory]
    [InlineData(true, ReachStatus.Ok, null, true)]
    [InlineData(true, ReachStatus.Tight, null, true)]
    [InlineData(false, ReachStatus.FlangeShort, true, true)]
    [InlineData(false, ReachStatus.FlangeShort, false, false)]
    [InlineData(false, ReachStatus.FlangeShort, null, false)]
    [InlineData(false, ReachStatus.IkFailed, true, false)]
    [InlineData(false, ReachStatus.SelfCollision, true, false)]
    [InlineData(false, ReachStatus.PostureLimit, null, false)]
    public void 진행_여부는_정차점과_뻗음부족_후진만_본다(bool node, ReachStatus status, bool? backup, bool allow)
        => Assert.Equal(allow, ReachPrecheckPolicy.Allows(node, status, backup));

    [Fact]
    public void 저장한_CSV를_다시_읽어_가까운_셀을_고른다()
    {
        var mount = new[] { 0.0, 0, 1650, 0, 0, 0 };
        var spec = new ReachMapSpec(new[] { "SM" }, new[] { 500.0 }, new[] { 1200.0 }, new[] { 0.0 }, new[] { 1500.0 },
            ApproachStandoffMm: 400, AlignStandoffMm: 300, FocusStandoffMm: 0);
        var robot = new ReachMapRobot(mount, 375, PostureLimits.Default, ToolAxisDir.MinusZ);
        var cell = new ReachCell("SM", 500, 1200, 0, 1500, ReachStatus.IkFailed, "② IK 실패",
            null, 100, 800, null, null, null);
        var csv = new ReachMapResult(spec, robot, new[] { cell }, 1).ToCsv();

        var file = ReachMapCsv.Parse(csv);
        Assert.NotNull(file);
        var stamp = new ReachMapStamp(400, 300, 0, 375, PostureLimits.Default.MinFlangeReachMm,
            PostureLimits.Default.WristMarginDeg, mount);
        Assert.True(ReachMapCsv.StampMatches(file!.Stamp, stamp));

        var hit = ReachMapCsv.Find(file, "SM", 500, 1210, 10, 1490);
        Assert.NotNull(hit);
        Assert.Equal(ReachStatus.IkFailed, hit!.Status);
        Assert.False(hit.Usable);
        Assert.Contains("IK", hit.Reason);

        Assert.Null(ReachMapCsv.Find(file, "SM", 500, 1400, 0, 1500));
        Assert.False(ReachMapCsv.StampMatches(file.Stamp, stamp with { ToolMm = 200 }));
    }

    [Fact]
    public void 최신_파일_중_스탬프가_맞는_셀만_쓴다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reach-precheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mount = new[] { 1.0, 2, 3, 0, 0, 4 };
            var stamp = new ReachMapStamp(400, 300, 0, 370, 350, 10, mount);
            var other = stamp with { ToolMm = 10 };
            File.WriteAllText(Path.Combine(dir, "reachmap_old.csv"), Csv(stamp, "SM", ReachStatus.Ok, usable: true));
            File.SetLastWriteTimeUtc(Path.Combine(dir, "reachmap_old.csv"), DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(Path.Combine(dir, "reachmap_new.csv"), Csv(other, "SM", ReachStatus.IkFailed, usable: false));

            var hit = ReachMapCsv.FindLatest(dir, stamp, "SM", 0, 1000, 0, 1500);
            Assert.NotNull(hit);
            Assert.Equal(ReachStatus.Ok, hit!.Status);
            Assert.True(hit.Usable);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 시험이동은_시작점만_자동시퀀스는_시작과_끝()
    {
        var test = JsonSerializer.Deserialize<VdaAction>("""
            {"actionType":"moveToSeamStart","actionId":"t","actionParameters":[
              {"key":"jobRef","value":"T1"},
              {"key":"position","value":{"seamStartW":[1,2,3],"seamEndW":[4,2,3],
                "drawingPos":{"tank":"CT1","level":1,"wall_code":"SM","x":0,"y":0,"z":1}}},
              {"key":"params","value":{"liftStrokeMm":1000}}]}
            """)!;
        var inspect = JsonSerializer.Deserialize<VdaAction>("""
            {"actionType":"startWeldInspection","actionId":"i","actionParameters":[
              {"key":"jobRef","value":"J1"},
              {"key":"position","value":{"seamStartW":[1,2,3],"seamEndW":[4,2,3],
                "drawingPos":{"tank":"CT1","level":1,"wall_code":"PM","x":0,"y":0,"z":1}}},
              {"key":"params","value":{"seamType":"LINE","anchorGroupId":"G","seqInGroup":1,"liftStrokeMm":400}}]}
            """)!;

        var points = CobotReachPrecheck.FromActions(new[] { test, inspect }, allowInspectionBackup: true);
        Assert.Equal(3, points.Count);
        Assert.Equal("T1 시험이동", points[0].Label);
        Assert.True(points[0].TestMove);
        Assert.False(points[0].AllowBackup);
        Assert.Equal(1000, points[0].StrokeMm);
        Assert.Equal(new[] { 1.0, 2, 3 }, points[0].SeamW);

        Assert.Equal("J1 시작", points[1].Label);
        Assert.Equal("J1 끝", points[2].Label);
        Assert.All(points.Skip(1), p => Assert.False(p.TestMove));
        Assert.All(points.Skip(1), p => Assert.True(p.AllowBackup));
        Assert.Equal("PM", points[2].WallCode);
        Assert.Equal(400, points[2].StrokeMm);
    }

    [Fact]
    public void 배터리_교체만_있으면_점검할_점이_없다()
    {
        var action = new VdaAction { ActionType = "batterySwapMove", ActionId = "b" };
        Assert.Empty(CobotReachPrecheck.FromActions(new[] { action }, allowInspectionBackup: true));
    }

    private static string Csv(ReachMapStamp stamp, string wall, ReachStatus status, bool usable)
    {
        var spec = new ReachMapSpec(new[] { wall }, new[] { 0.0 }, new[] { 1000.0 }, new[] { 0.0 }, new[] { 1500.0 },
            stamp.ApproachMm, stamp.AlignMm, stamp.FocusMm);
        var limits = PostureLimits.Default with { MinFlangeReachMm = stamp.MinFlangeMm, WristMarginDeg = stamp.WristDeg };
        var robot = new ReachMapRobot(stamp.Mount, stamp.ToolMm, limits);
        var cell = new ReachCell(wall, 0, 1000, 0, 1500, status, status.ToString(), null, 0, 0, null, null, null);
        // usable 플래그는 Status 가 Ok/Tight 일 때만 참이 된다. 테스트가 넘긴 값과 어긋나면 헤더 왕복만 확인한다.
        _ = usable;
        return new ReachMapResult(spec, robot, new[] { cell }, 0).ToCsv();
    }
}
