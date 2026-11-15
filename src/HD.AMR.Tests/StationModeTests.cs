using System.Text.Json;
using System.Text.Json.Nodes;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>측면 정차 검사(docs/HD_AMR_측면정차검사_사양.md) — stationMode 계약·벽 방향·좌/우 검증·측면 리치 맵.</summary>
public class StationModeTests
{
    // 맵 규약(시험용): 선수 = +X, 좌현 = +Y. 좌현벽 PM 은 y=+3, 우현벽 SM 은 y=−3, 전벽 F 는 x=+10, 후벽 A 는 x=−10.
    private static VdaAction Action(string type, string id, string wall, double[] start, double[] end, string? mode)
    {
        var prm = new JsonObject
        {
            ["seamType"] = "LINE", ["anchorGroupId"] = "G1", ["seqInGroup"] = 1,
        };
        if (mode is not null) prm["stationMode"] = mode;
        var json = new JsonObject
        {
            ["actionType"] = type,
            ["actionId"] = id,
            ["actionParameters"] = new JsonArray(
                new JsonObject { ["key"] = "jobRef", ["value"] = $"JOB-{id}" },
                new JsonObject
                {
                    ["key"] = "position",
                    ["value"] = new JsonObject
                    {
                        ["seamStartW"] = new JsonArray(start[0], start[1], start[2]),
                        ["seamEndW"] = new JsonArray(end[0], end[1], end[2]),
                        ["drawingPos"] = new JsonObject { ["tank"] = "CT1", ["level"] = 2, ["wall_code"] = wall },
                    },
                },
                new JsonObject { ["key"] = "params", ["value"] = prm }),
        };
        return JsonSerializer.Deserialize<VdaAction>(json.ToJsonString())!;
    }

    private static OrderNode Node(double x, double y, double? theta, params VdaAction[] actions) => new()
    {
        NodeId = "N1",
        NodePosition = new NodePosition { X = x, Y = y, Theta = theta, MapId = "L2" },
        Actions = actions.ToList(),
    };

    // ── 해석 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, StationMode.Front)]
    [InlineData("", StationMode.Front)]
    [InlineData("FRONT", StationMode.Front)]
    [InlineData("SIDE_LEFT", StationMode.SideLeft)]
    [InlineData("SIDE_RIGHT", StationMode.SideRight)]
    public void TryParse_DefinedValues(string? raw, StationMode expected)
    {
        Assert.True(StationModes.TryParse(raw, out var mode, out var error), error);
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("SIDE")]
    [InlineData("side_left")]
    [InlineData("LEFT")]
    public void TryParse_UndefinedValue_Rejected(string raw)
    {
        Assert.False(StationModes.TryParse(raw, out _, out var error));
        Assert.Contains("미정의", error);
    }

    [Fact]
    public void Parser_ReadsStationMode_DefaultFront()
    {
        var withMode = Action("startWeldInspection", "a1", "SM", new[] { 0, -3.0, 1 }, new[] { 1, -3.0, 1 }, "SIDE_LEFT");
        var without = Action("startWeldInspection", "a2", "SM", new[] { 0, -3.0, 1 }, new[] { 1, -3.0, 1 }, null);

        Assert.True(WeldInspectionActionParser.TryParse(withMode, out var r1, out var e1), e1);
        Assert.Equal(StationMode.SideLeft, r1!.StationMode);
        Assert.True(WeldInspectionActionParser.TryParse(without, out var r2, out var e2), e2);
        Assert.Equal(StationMode.Front, r2!.StationMode);
    }

    [Fact]
    public void Parser_UndefinedStationMode_Rejected()
    {
        var bad = Action("startWeldInspection", "a1", "SM", new[] { 0, -3.0, 1 }, new[] { 1, -3.0, 1 }, "SIDEWAYS");
        Assert.False(WeldInspectionActionParser.TryParse(bad, out _, out var error));
        Assert.Contains("stationMode", error);
    }

    // ── 벽 방향 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, StationMode.Front, 0.0)]
    [InlineData(0.0, StationMode.SideLeft, Math.PI / 2)]
    [InlineData(0.0, StationMode.SideRight, -Math.PI / 2)]
    [InlineData(Math.PI, StationMode.SideLeft, -Math.PI / 2)]
    [InlineData(Math.PI / 2, StationMode.SideRight, 0.0)]
    public void WallFacing_RotatesByNinety(double theta, StationMode mode, double expected)
        => Assert.Equal(expected, StationModes.WallFacing(theta, mode), 9);

    // ── 좌/우 판정 — 사양 §1 의 8가지 운용 경우 ──────────────────────

    [Theory]
    // 선수→선미(AMR 이 선미 −X 를 봄): SM 왼쪽, PM 오른쪽
    [InlineData(Math.PI, "SM", StationMode.SideLeft)]
    [InlineData(Math.PI, "PM", StationMode.SideRight)]
    // 선미→선수(+X): SM 오른쪽, PM 왼쪽
    [InlineData(0.0, "SM", StationMode.SideRight)]
    [InlineData(0.0, "PM", StationMode.SideLeft)]
    // 좌현→우현(−Y): F 왼쪽, A 오른쪽
    [InlineData(-Math.PI / 2, "F", StationMode.SideLeft)]
    [InlineData(-Math.PI / 2, "A", StationMode.SideRight)]
    // 우현→좌현(+Y): F 오른쪽, A 왼쪽
    [InlineData(Math.PI / 2, "F", StationMode.SideRight)]
    [InlineData(Math.PI / 2, "A", StationMode.SideLeft)]
    public void CheckLeftRight_EightOperatingCases(double theta, string wall, StationMode correct)
    {
        // AMR 은 탱크 중앙 근처(원점), 용접선은 해당 벽 위(벽과 나란한 짧은 선).
        var (start, end) = wall switch
        {
            "SM" => (new[] { -0.3, -3.0, 1.2 }, new[] { 0.3, -3.0, 1.2 }),
            "PM" => (new[] { -0.3, 3.0, 1.2 }, new[] { 0.3, 3.0, 1.2 }),
            "F" => (new[] { 10.0, -0.3, 1.2 }, new[] { 10.0, 0.3, 1.2 }),
            _ => (new[] { -10.0, -0.3, 1.2 }, new[] { -10.0, 0.3, 1.2 }),
        };
        // F/A 는 벽 1 m 앞(x=±9)에 선다 — 좌현↔우현으로 진행하므로 벽은 옆에 온다.
        var (x, y) = wall switch { "F" => (9.0, 0.0), "A" => (-9.0, 0.0), _ => (0.0, 0.0) };
        var wrong = correct == StationMode.SideLeft ? StationMode.SideRight : StationMode.SideLeft;

        Assert.Null(StationModes.CheckLeftRight(correct, x, y, theta, start, end, 200));
        Assert.NotNull(StationModes.CheckLeftRight(wrong, x, y, theta, start, end, 200));
    }

    [Fact]
    public void CheckLeftRight_SeamAheadNotBeside_Mismatch()
    {
        // 용접선이 차체 정면(옆 성분 0) — 측면이라 할 수 없다(최소 옆 거리 미달).
        var why = StationModes.CheckLeftRight(StationMode.SideLeft, 0, 0, 0,
            new[] { 1.5, 0.05, 1.0 }, new[] { 1.5, 0.1, 1.0 }, 200);
        Assert.NotNull(why);
    }

    // ── 수신 검증 V1~V5 ──────────────────────────────────────────────

    [Fact]
    public void ValidateNode_FrontOrMissing_Passes()
    {
        var node = Node(0, 0, 0, Action("startWeldInspection", "a1", "B", new[] { 1, 0.0, 0 }, new[] { 2, 0.0, 0 }, null));
        Assert.Null(StationModes.ValidateNode(node, 200, out var mode));
        Assert.Equal(StationMode.Front, mode);
    }

    [Fact]
    public void ValidateNode_SideLeftOnStarboardFacingAft_Passes()
    {
        var node = Node(0, -2, Math.PI,
            Action("startWeldInspection", "a1", "SM", new[] { 0.3, -3.0, 1.2 }, new[] { -0.3, -3.0, 1.2 }, "SIDE_LEFT"),
            Action("moveToSeamStart", "a2", "SM", new[] { 0.6, -3.0, 1.2 }, new[] { 0.0, -3.0, 1.2 }, "SIDE_LEFT"));
        Assert.Null(StationModes.ValidateNode(node, 200, out var mode));
        Assert.Equal(StationMode.SideLeft, mode);
    }

    [Fact]
    public void ValidateNode_UndefinedValue_V1()
    {
        var node = Node(0, -2, Math.PI,
            Action("startWeldInspection", "a1", "SM", new[] { 0.3, -3.0, 1.2 }, new[] { -0.3, -3.0, 1.2 }, "SIDE"));
        Assert.Contains("미정의", StationModes.ValidateNode(node, 200, out _));
    }

    [Fact]
    public void ValidateNode_Mixed_V2()
    {
        var node = Node(0, -2, Math.PI,
            Action("startWeldInspection", "a1", "SM", new[] { 0.3, -3.0, 1.2 }, new[] { -0.3, -3.0, 1.2 }, "SIDE_LEFT"),
            Action("startWeldInspection", "a2", "SM", new[] { 0.3, -3.0, 1.2 }, new[] { -0.3, -3.0, 1.2 }, null));
        Assert.Contains("혼재", StationModes.ValidateNode(node, 200, out _));
    }

    [Fact]
    public void ValidateNode_NonVerticalWall_V3()
    {
        var node = Node(0, -2, Math.PI,
            Action("startWeldInspection", "a1", "SL", new[] { 0.3, -3.0, 0.3 }, new[] { -0.3, -3.0, 0.3 }, "SIDE_LEFT"));
        Assert.Contains("수직벽", StationModes.ValidateNode(node, 200, out _));
    }

    [Fact]
    public void ValidateNode_MissingTheta_V4()
    {
        var node = Node(0, -2, null,
            Action("startWeldInspection", "a1", "SM", new[] { 0.3, -3.0, 1.2 }, new[] { -0.3, -3.0, 1.2 }, "SIDE_LEFT"));
        Assert.Contains("theta", StationModes.ValidateNode(node, 200, out _));
    }

    [Fact]
    public void ValidateNode_WrongSide_V5_HasFixedPrefix()
    {
        var node = Node(0, -2, Math.PI,
            Action("startWeldInspection", "a1", "SM", new[] { 0.3, -3.0, 1.2 }, new[] { -0.3, -3.0, 1.2 }, "SIDE_RIGHT"));
        var why = StationModes.ValidateNode(node, 200, out _);
        Assert.NotNull(why);
        Assert.StartsWith("SIDE_LR_MISMATCH: ", why);
    }

    // ── 면 법선 ──────────────────────────────────────────────────────

    [Fact]
    public void SeamBaseTransform_SideLeft_NormalPointsToBodyLeft()
    {
        var target = SeamBaseTransform.Resolve(new SeamBaseInput(
            SeamStartW: new[] { 0.0, 1.0, 1.0 }, SeamEndW: new[] { 0.5, 1.0, 1.0 },
            AmrXm: 0, AmrYm: 0, AmrYawRad: 0,
            MountAtHome: new[] { 0.0, 0, 1000, 0, 0, 0 }, TelescopicStrokeMm: 0, ZDatumOffsetMm: 0,
            StandoffMm: 400, WallFacingThetaRad: StationModes.WallFacing(0.0, StationMode.SideLeft), WallCode: "SM"));

        Assert.Equal(0.0, target.SurfaceNormalMap[0], 6);
        Assert.Equal(1.0, target.SurfaceNormalMap[1], 6);
        // 접근점은 벽(y=1000)에서 법선 반대로 400mm — y=600.
        Assert.Equal(600.0, target.ApproachMapMm[1], 3);
        Assert.True(target.NormalDistanceMm > 0);
    }

    // ── 리치 맵 CSV 방식 표시 ────────────────────────────────────────

    [Fact]
    public void ReachMapCsv_ModeHeader_RoundTripsAndSeparatesMaps()
    {
        const string head = "# reach map  approachStandoff=400mm  alignStandoff=300mm  focusStandoff=0mm  tool=375mm  " +
                            "minFlange=350mm  wrist=10deg  mount=[0 0 1000 0 0 0]";
        var front = ReachMapCsv.Parse(head + "\nwall_code,stroke_mm\nSM,0,800,0,1000,Ok,1,20,\"도달\"\n");
        var side = ReachMapCsv.Parse(head + "  mode=SIDE_LEFT\nwall_code,stroke_mm\nSM,0,0,800,1000,Ok,1,20,\"도달\"\n");

        Assert.Equal(StationMode.Front, front!.Stamp.Mode);
        Assert.Equal(StationMode.SideLeft, side!.Stamp.Mode);
        Assert.False(ReachMapCsv.StampMatches(front.Stamp, side.Stamp));
        Assert.True(ReachMapCsv.StampMatches(side.Stamp, side.Stamp));
    }

    // ── ACS 전달 JSON ───────────────────────────────────────────────

    [Fact]
    public void SideReachExport_PicksDistanceWithWideAlongWindow()
    {
        var cells = new List<ExportCell>();
        foreach (var stroke in new[] { 0.0, 500.0 })
        foreach (var x in new[] { -400.0, -200, 0, 200, 400 })
        {
            // 상대 높이 1000 (z = 1000 + stroke). 옆 800mm 은 벽 따라 전부 도달, 1000mm 은 x=0 한 칸만.
            cells.Add(new ExportCell("SM", stroke, x, 800, 1000 + stroke, Usable: true));
            cells.Add(new ExportCell("SM", stroke, x, 1000, 1000 + stroke, Usable: x == 0));
            // 오른쪽(−Y) 셀은 SIDE_LEFT 판정에 쓰지 않는다.
            cells.Add(new ExportCell("SM", stroke, x, -800, 1000 + stroke, Usable: true));
        }

        var json = SideReachExport.Build(new[] { (StationMode.SideLeft, (IReadOnlyList<ExportCell>)cells) },
            version: 3, DateTimeOffset.Parse("2026-11-15T10:00:00+09:00"), (0, 1000));
        var root = JsonNode.Parse(json)!;

        Assert.Equal(3, root["version"]!.GetValue<int>());
        var band = root["default"]!["SM"]!["SIDE_LEFT"]![0]!;
        var lateral = band["lateralMm"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
        Assert.Equal(new[] { 800.0, 800, 800 }, lateral);
        Assert.Null(band["minStrokeMm"]);
        Assert.Null(root["default"]!["SM"]!["SIDE_RIGHT"]);
    }

    [Fact]
    public void SideReachExport_SameLeftRight_MarkedSame()
    {
        var cells = new List<ExportCell>();
        foreach (var x in new[] { -400.0, -200, 0, 200, 400 })
        {
            cells.Add(new ExportCell("PM", 0, x, 900, 800, true));
            cells.Add(new ExportCell("PM", 0, x, -900, 800, true));
        }
        var maps = new[]
        {
            (StationMode.SideLeft, (IReadOnlyList<ExportCell>)cells),
            (StationMode.SideRight, (IReadOnlyList<ExportCell>)cells),
        };
        var root = JsonNode.Parse(SideReachExport.Build(maps, 1, DateTimeOffset.Now, (0, 1000)))!;
        Assert.Equal("SAME_AS_SIDE_LEFT", root["default"]!["PM"]!["SIDE_RIGHT"]!.GetValue<string>());
    }

    // ── 측면 검사 준비 슬롯 ──────────────────────────────────────────

    private static TeachingPosition Taught(string key, params double[] j) => new()
    {
        Key = key, Name = key, X = 0, Y = 0, Z = 0, Rx = 0, Ry = 0, Rz = 0,
        J1 = j[0], J2 = j[1], J3 = j[2], J4 = j[3], J5 = j[4], J6 = j[5],
    };

    [Fact]
    public void TeachingSlots_IncludeSideReadyPair()
    {
        Assert.Contains(TeachingService.Slots, s => s.Key == TeachingService.SideReadyLeftKey);
        Assert.Contains(TeachingService.Slots, s => s.Key == TeachingService.SideReadyRightKey);
        Assert.Equal(TeachingService.SideReadyLeftKey, TeachingService.SideReadyKey(StationMode.SideLeft));
        Assert.Null(TeachingService.SideReadyKey(StationMode.Front));
    }

    [Fact]
    public void CheckSideReadyJ1_WarnsUnlessNinety()
    {
        var ready = new[] { 0.0, -80, 100, -20, 90, 0 };
        Assert.Null(TeachingService.CheckSideReadyJ1(new[] { 90.0, -80, 100, -20, 90, 0 }, ready));
        Assert.Null(TeachingService.CheckSideReadyJ1(new[] { -88.0, -80, 100, -20, 90, 0 }, ready));
        Assert.NotNull(TeachingService.CheckSideReadyJ1(new[] { 45.0, -80, 100, -20, 90, 0 }, ready));
    }

    [Fact]
    public void SideViaSlot_ReturnsSideSlotOnlyWhenCloserThanReady()
    {
        SequenceEntry.ForgetSide();
        var ctx = new SequenceContext
        {
            Positions = new[]
            {
                Taught("ready", 0, -80, 100, -20, 90, 0),
                Taught(TeachingService.SideReadyLeftKey, 90, -80, 100, -20, 90, 0),
                Taught(TeachingService.SideReadyRightKey, -90, -80, 100, -20, 90, 0),
            }.ToDictionary(p => p.Key, p => p),
        };

        // 왼쪽 벽 앞에 뻗은 자세(J1≈85) — 측면 좌 슬롯을 거쳐 돌아간다(방식 기억이 없어도 가까운 쪽).
        Assert.Equal(TeachingService.SideReadyLeftKey,
            SequenceEntry.SideViaSlot(new[] { 85.0, -60, 80, -10, 90, 0 }, ctx, lastSide: null));
        // ready 근처면 경유하지 않는다.
        Assert.Null(SequenceEntry.SideViaSlot(new[] { 5.0, -80, 100, -20, 90, 0 }, ctx, lastSide: null));
        // 방식이 정해져 있으면 그 쪽 슬롯만 본다.
        ctx.StationMode = StationMode.SideRight;
        Assert.Null(SequenceEntry.SideViaSlot(new[] { 85.0, -60, 80, -10, 90, 0 }, ctx, lastSide: null));
    }
}
