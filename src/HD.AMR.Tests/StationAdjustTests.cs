using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

/// <summary>정차 온보드 보정 params.stationAdjust (사양 §4.4.2, N23 초안) — 파싱·계획·토큰.</summary>
public class StationAdjustTests
{
    private static readonly StationAdjustLimits Limits = new(0, 400, 200, 5);

    // ── 계획 ────────────────────────────────────────────────────────

    [Fact]
    public void Plan_AllSeamsReachable_NoMove()
    {
        var plan = StationAdjustPlanner.Plan(Limits, new[] { Seam("A", 1300, 1400, 1120) });
        Assert.Equal(0, plan.BackwardMm);
        Assert.Empty(plan.Unfixable);
    }

    /// <summary>2026-09-28 현장 재현 — 법선거리 800, 필요 1120 → 부족 320 + 여유 20 = 340 후진.</summary>
    [Fact]
    public void Plan_VerticalWall_BacksOffShortfallPlusMargin()
    {
        var plan = StationAdjustPlanner.Plan(Limits, new[] { Seam("A", 800, 900, 1120) });
        Assert.Equal(340, plan.BackwardMm);
        Assert.Empty(plan.Unfixable);
    }

    /// <summary>법선이 기울어진 면(챔퍼)은 후진 1mm 당 법선거리가 덜 는다 — 그만큼 더 물러난다(올림 10mm).</summary>
    [Fact]
    public void Plan_TiltedWall_ScalesBySlope()
    {
        var plan = StationAdjustPlanner.Plan(Limits, new[] { Seam("A", 1000, 1050, 1120) });   // 기울기 0.5
        Assert.Equal(280, plan.BackwardMm);   // (120+20)/0.5
    }

    [Fact]
    public void Plan_TakesWorstFixableSeam_AndReportsUnfixable()
    {
        var plan = StationAdjustPlanner.Plan(Limits, new[]
        {
            Seam("near", 1000, 1100, 1120),    // 140
            Seam("worse", 900, 1000, 1120),    // 240
            Seam("hopeless", 500, 600, 1120),  // 640 > 400
        });
        Assert.Equal(240, plan.BackwardMm);
        var u = Assert.Single(plan.Unfixable);
        Assert.StartsWith("hopeless:", u);
    }

    /// <summary>바닥·천장은 수평 이동이 법선거리를 바꾸지 않는다 — 보정 불가로 분류하고 옮기지 않는다.</summary>
    [Fact]
    public void Plan_FlatSlope_IsUnfixable()
    {
        var plan = StationAdjustPlanner.Plan(Limits, new[] { Seam("floor", 900, 900, 1120) });
        Assert.Equal(0, plan.BackwardMm);
        Assert.Single(plan.Unfixable);
        Assert.Contains("보정 불가", plan.Summary);
    }

    [Fact]
    public void Plan_NeedBeyondLimit_DoesNotMove()
    {
        var plan = StationAdjustPlanner.Plan(Limits with { MaxBackwardMm = 100 }, new[] { Seam("A", 800, 900, 1120) });
        Assert.Equal(0, plan.BackwardMm);
        Assert.Contains("허락 100mm 초과", Assert.Single(plan.Unfixable));
    }

    [Fact]
    public void BackedOff_MovesOppositeTheta()
    {
        var (x, y) = StationAdjustPlanner.BackedOff(12.0, 5.0, Math.PI / 2, 340);
        Assert.Equal(12.0, x, 9);
        Assert.Equal(4.66, y, 9);
    }

    [Theory]
    [InlineData(-340, 0, 0, "stationAdjusted dF=-340 dL=0 dTh=0.0;")]
    [InlineData(20, -45, 1.5, "stationAdjusted dF=+20 dL=-45 dTh=+1.5;")]
    public void Token_MatchesSpecFormat(double f, double l, double t, string expected)
        => Assert.Equal(expected, StationAdjustPlanner.Token(f, l, t));

    // ── stationReachShortfall 수치 (N23 A안) ─────────────────────────

    [Fact]
    public void NeededBackward_EnoughReach_IsZero()
        => Assert.Equal(0, StationAdjustPlanner.NeededBackwardMm(Seam("A", 1300, 1400, 1120)));

    [Fact]
    public void NeededBackward_FlatSurface_IsNull()
        => Assert.Null(StationAdjustPlanner.NeededBackwardMm(Seam("floor", 900, 900, 1120)));

    [Fact]
    public void Shortfall_DescribeIsMachineReadable()
        => Assert.Equal("normalMm=800 requiredMm=1120 extraBackwardMm=340",
            new ReachShortfall(800.4, 1120, 340).Describe());

    [Fact]
    public void Shortfall_UnfixableReportsMinusOne()
        => Assert.Equal("normalMm=900 requiredMm=1120 extraBackwardMm=-1",
            new ReachShortfall(900, 1120, StationAdjustPlanner.NeededBackwardMm(Seam("floor", 900, 900, 1120)) ?? -1).Describe());

    // ── 실제 환산과의 정합 — 표본 2점 기울기 가정 ────────────────────

    private static readonly double[] Mount = { 300, 120, 1650, 0, 0, 0 };

    private static double NormalAt(double x, double y, double theta, double[] seam, string wall)
        => SeamBaseTransform.Resolve(new SeamBaseInput(seam, null, x, y, theta, Mount, 0, 0, 400, theta, wall))
            .NormalDistanceMm;

    /// <summary>수직 벽은 후진 1mm = 법선거리 1mm, 바닥은 0 — 계획기의 선형·기울기 판정이 실제 환산과 맞는다.</summary>
    [Theory]
    [InlineData("SM", 1.0)]
    [InlineData("B", 0.0)]
    public void SeamBaseTransform_SlopeMatchesSurface(string wall, double expectedSlope)
    {
        const double theta = Math.PI / 2;   // +y 벽을 바라봄
        var seam = wall == "B" ? new[] { 12.0, 6.0, 0.0 } : new[] { 12.3, 6.2, 1.4 };
        var d0 = NormalAt(12.0, 5.0, theta, seam, wall);
        var (px, py) = StationAdjustPlanner.BackedOff(12.0, 5.0, theta, StationAdjustPlanner.ProbeMm);
        var d1 = NormalAt(px, py, theta, seam, wall);
        Assert.Equal(expectedSlope, (d1 - d0) / StationAdjustPlanner.ProbeMm, 3);

        // 두 배 후진해도 같은 기울기 — 선형 가정.
        var (qx, qy) = StationAdjustPlanner.BackedOff(12.0, 5.0, theta, 2 * StationAdjustPlanner.ProbeMm);
        Assert.Equal(d0 + 2 * (d1 - d0), NormalAt(qx, qy, theta, seam, wall), 6);
    }

    // ── 파싱 ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Absent_ReturnsNullWithoutError()
    {
        Assert.Null(WeldInspectionActionParser.TryParseStationAdjust(Action(null), out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Parse_FourKeys()
    {
        var l = WeldInspectionActionParser.TryParseStationAdjust(
            Action("""{"maxForwardMm":0,"maxBackwardMm":400,"maxLateralMm":200,"maxThetaDeg":5}"""), out var error);
        Assert.Null(error);
        Assert.Equal(Limits, l);
    }

    [Theory]
    [InlineData("""{"maxNormalMm":300,"maxLateralMm":200,"maxThetaDeg":5}""")]       // 구 3필드 초안
    [InlineData("""{"maxForwardMm":0,"maxBackwardMm":-1,"maxLateralMm":200,"maxThetaDeg":5}""")]
    [InlineData("\"400\"")]
    public void Parse_Malformed_ReturnsNullWithError(string json)
    {
        Assert.Null(WeldInspectionActionParser.TryParseStationAdjust(Action(json), out var error));
        Assert.NotNull(error);
    }

    /// <summary>새 필드가 실려도 본검사 파싱은 그대로 통과한다 — 하위 호환.</summary>
    [Fact]
    public void Parse_InspectionStillParsesWithStationAdjust()
    {
        var a = Action("""{"maxForwardMm":0,"maxBackwardMm":400,"maxLateralMm":200,"maxThetaDeg":5}""");
        Assert.True(WeldInspectionActionParser.TryParse(a, out var req, out var error), error);
        Assert.Equal("JOB-1", req!.JobRef);
    }

    private static StationAdjustSeam Seam(string label, double atNode, double atProbe, double required)
        => new(label, atNode, atProbe, required);

    private static VdaAction Action(string? stationAdjustJson)
    {
        var adjust = stationAdjustJson is null ? "" : $",\"stationAdjust\": {stationAdjustJson}";
        var json = $$"""
        {
          "actionType": "startWeldInspection", "actionId": "a1", "blockingType": "HARD",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-1" },
            { "key": "position", "value": {
                "seamStartW": [12.3, 6.2, 1.4], "seamEndW": [13.0, 6.2, 1.4],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "SM", "u": 1, "v": 1.4, "x": 1, "y": 0, "z": 1.4 } } },
            { "key": "params", "value": {
                "seamType": "LINE", "sectionDxfId": "", "inspectionProfileId": "P", "standoffMm": 400,
                "anchorGroupId": "G", "seqInGroup": 1 {{adjust}} } }
          ]
        }
        """;
        return JsonSerializer.Deserialize<VdaAction>(json)!;
    }
}
