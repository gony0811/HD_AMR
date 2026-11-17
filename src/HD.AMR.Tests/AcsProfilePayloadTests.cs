using System.Text.Json;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using Xunit;

namespace HD.AMR.Tests;

/// <summary>ACS 로 올려 보내는 검사 프로필 본문 — 경유점 값·필드 이름·주소 결정.</summary>
public class AcsProfilePayloadTests
{
    private static InspectionProfile Profile(int id, string name, string json, string seamType = "LINE") => new()
    {
        Id = id, Name = name, SeamType = seamType, PoseAbsolute = true, WaypointsJson = json,
        UpdatedAt = new DateTime(2026, 11, 9, 6, 26, 3),
    };

    [Fact]
    public void 저장된_경유점을_그대로_싣는다()
    {
        // DB 에 저장된 모양 그대로(CROSS-4-V1 의 세로 팔 한 점 포함)
        const string json = """
            [{"X":0,"Z":33.8,"Theta":0,"ThetaManual":true,"Surface":0,"SurfaceManual":true,"Y":0,"RzDeg":0,"RxDeg":0},
             {"X":205,"Z":28.37,"Theta":-10,"ThetaManual":true,"Surface":1,"SurfaceManual":true,"Y":-189.09,"RzDeg":-90,"RxDeg":0}]
            """;
        var body = AcsProfilePayload.Build("AMR-01", new[] { Profile(9, " CROSS-4-V1 ", json, "CROSS") }, out var skipped);

        Assert.Empty(skipped);
        Assert.True(body.Full);
        var p = Assert.Single(body.Profiles);
        Assert.Equal((9, "CROSS-4-V1", "CROSS", true), (p.Id, p.Name, p.SeamType, p.PoseAbsolute));
        Assert.Equal(TimeSpan.Zero, p.UpdatedAt.Offset);   // UTC
        Assert.Equal(new AcsProfilePayload.Waypoint(205, -189.09, 28.37, -10, -90, 0, 1), p.Waypoints[1]);
    }

    [Fact]
    public void 구버전_경유점은_빠진_값을_0으로_채운다()
    {
        var body = AcsProfilePayload.Build("AMR-01",
            new[] { Profile(3, "OLD", """[{"X":10,"Z":33.8,"Theta":5,"ThetaManual":false}]""") }, out _);
        Assert.Equal(new AcsProfilePayload.Waypoint(10, 0, 33.8, 5, 0, 0, 0), body.Profiles[0].Waypoints[0]);
    }

    [Fact]
    public void 읽지_못한_프로필이_있으면_빼고_전체_목록이_아니라고_보낸다()
    {
        var body = AcsProfilePayload.Build("AMR-01", new[]
        {
            Profile(1, "OK", "[]"), Profile(2, "BROKEN", "{깨진 JSON"), Profile(3, " ", "[]"),
        }, out var skipped);

        Assert.False(body.Full);   // ACS 가 뺀 프로필을 지워진 것으로 보지 않게
        Assert.Equal(new[] { "OK" }, body.Profiles.Select(p => p.Name));
        Assert.Equal(new[] { "BROKEN", "#3" }, skipped);
    }

    [Fact]
    public void 본문은_ACS_가_읽는_필드_이름으로_직렬화된다()
    {
        var body = AcsProfilePayload.Build("AMR-01",
            new[] { Profile(18, "LINE-1", """[{"X":4.91,"Z":33.37,"Theta":10,"ThetaManual":true}]""") }, out _);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body, AcsProfilePayload.Json));
        var root = doc.RootElement;

        Assert.Equal("AMR-01", root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("full").GetBoolean());
        var p = root.GetProperty("profiles")[0];
        Assert.Equal(18, p.GetProperty("id").GetInt32());
        Assert.Equal("LINE", p.GetProperty("seamType").GetString());
        Assert.True(p.GetProperty("poseAbsolute").GetBoolean());
        var w = p.GetProperty("waypoints")[0];
        Assert.Equal(new[] { "x", "y", "z", "theta", "rzDeg", "rxDeg", "surface" }, w.EnumerateObject().Select(o => o.Name));
        Assert.Equal(33.37, w.GetProperty("z").GetDouble());
    }

    private static InspectionRecipe Recipe(string id, int? profile, int? chamfer = null) =>
        new() { Id = id, InspectionProfileId = profile, ChamferSlopeProfileId = chamfer, Enabled = true };

    [Fact]
    public void 지정표는_실행_때와_같은_규칙으로_유형_면_방향마다_프로필을_싣는다()
    {
        // 현장 지정 그대로: LINE 18(경사면 세로만 17) · CROSS4_L 9 · CROSS3-R0 13 · 코너는 프로필 없음
        var table = AcsProfilePayload.BuildAssignments(new[]
        {
            Recipe(RecipeIds.Line, 18, 17), Recipe(RecipeIds.Cross4L, 9), Recipe(RecipeIds.Cross3R0, 13),
            Recipe(RecipeIds.Cross3R90, null), Recipe(RecipeIds.Corner3, null),
        });
        int? Of(string type, string wall, string dir) =>
            table.SingleOrDefault(a => a.SeamType == type && a.WallCode == wall && a.Direction == dir)?.ProfileId;

        Assert.Equal(18, Of("LINE", "T", "H"));
        Assert.Equal(18, Of("LINE", "T", "V"));     // 천장은 세로도 기본 프로필
        Assert.Equal(18, Of("LINE", "PU", "H"));
        Assert.Equal(17, Of("LINE", "PU", "V"));    // 경사면을 따라 오르내리는 용접선은 전용 프로필
        Assert.Equal(17, Of("LINE", "SL", "V"));
        Assert.Equal(18, Of("LINE", "F", "V"));     // 수직벽은 기본 프로필
        Assert.Equal(9, Of("CROSS4_L", "F", "H"));
        Assert.Equal(9, Of("CROSS4", "F", "H"));    // 좌/우 미지정은 면별 기본값(설정 없으면 좌측)
        Assert.Null(Of("CROSS4_R", "F", "H"));      // 우측 레시피는 아직 프로필 없음
        Assert.Equal(13, Of("CROSS3_R0", "SM", "V"));
        Assert.Null(Of("CROSS3_R90", "SM", "H"));   // 프로필을 지정하지 않은 레시피
        Assert.DoesNotContain(table, a => a.SeamType.StartsWith("CORNER"));
        Assert.Equal(20 + 20 + 20 + 20, table.Count);    // LINE·CROSS4_L·CROSS4(좌/우 미지정)·CROSS3_R0 × 10면 × 2방향
    }

    [Fact]
    public void 경사면_세로_전용_프로필이_없으면_그_조합은_싣지_않는다()
    {
        var table = AcsProfilePayload.BuildAssignments(new[] { Recipe(RecipeIds.Line, 18) });
        Assert.DoesNotContain(table, a => a.WallCode == "PU" && a.Direction == "V");   // 기본 프로필로 대신하지 않는다(실행 때도 실패)
        Assert.Contains(table, a => a.WallCode == "PU" && a.Direction == "H" && a.ProfileId == 18);
    }

    [Fact]
    public void 본문에_지정표가_함께_실린다()
    {
        var body = AcsProfilePayload.Build("AMR-01", new[] { Profile(18, "LINE-1", "[]") }, out _, new[] { Recipe(RecipeIds.Line, 18, 17) });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body, AcsProfilePayload.Json));
        var first = doc.RootElement.GetProperty("assignments")[0];
        Assert.Equal(new[] { "seamType", "wallCode", "direction", "profileId" }, first.EnumerateObject().Select(o => o.Name));
        Assert.Empty(AcsProfilePayload.Build("AMR-01", new[] { Profile(18, "LINE-1", "[]") }, out _).Assignments);   // 레시피를 안 주면 빈 표
    }

    [Theory]
    [InlineData("http://10.0.0.5:5199", "192.168.100.98", "http://10.0.0.5:5199/")]   // 설정한 주소 우선
    [InlineData("10.0.0.5:6000/", "192.168.100.98", "http://10.0.0.5:6000/")]          // http:// 생략
    [InlineData("", "192.168.100.98", "http://192.168.100.98:5199/")]                  // 비우면 브로커 호스트 + 포트
    [InlineData(null, " 192.168.100.98 ", "http://192.168.100.98:5199/")]
    [InlineData("", "", null)]                                                          // 둘 다 없으면 보내지 않는다
    [InlineData("ftp://10.0.0.5", "192.168.100.98", null)]
    public void ACS_주소_결정(string? baseUrl, string? brokerHost, string? expected)
    {
        Assert.Equal(expected, AcsProfilePayload.ResolveBaseUri(baseUrl, brokerHost, 5199)?.ToString());
    }

    [Fact]
    public void 코봇_모델_값을_함께_보내고_ACS_가_알려_준_차이를_읽는다()
    {
        var robot = AcsProfilePayload.CurrentRobotModel();
        Assert.Equal(HD.AMR.App.Service.Motion.Fr5Kinematics.Fr5Wml.LinksMm, robot.LinksMm);
        Assert.Equal(HD.AMR.App.Service.Motion.PostureLimits.DefaultWristMarginDeg, robot.WristMarginDeg);
        Assert.Equal(375, robot.ToolLengthMm);

        var body = AcsProfilePayload.Build("AMR-01", new[] { Profile(18, "LINE-1", "[]") }, out _) with { Robot = robot };
        var json = System.Text.Json.JsonSerializer.Serialize(body, AcsProfilePayload.Json);
        Assert.Contains("\"robot\":{\"linksMm\":[180,", json);
        Assert.Contains("\"wristMarginDeg\":10", json);
        Assert.Contains("\"toolLengthMm\":375", json);

        Assert.Equal(new[] { "d6 다름" }, AcsProfilePayload.RobotModelMismatch("{\"added\":0,\"robotModelMismatch\":[\"d6 다름\"]}"));
        Assert.Empty(AcsProfilePayload.RobotModelMismatch("{\"added\":0,\"robotModelMismatch\":null}"));
        Assert.Empty(AcsProfilePayload.RobotModelMismatch("{\"added\":0}"));   // 구버전 ACS
        Assert.Empty(AcsProfilePayload.RobotModelMismatch("not json"));
    }
}
