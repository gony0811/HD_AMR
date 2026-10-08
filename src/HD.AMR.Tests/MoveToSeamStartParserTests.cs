using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

/// <summary>`moveToSeamStart`(사양 §8.7, HD_ACS docs/HD_AMR_MOVE_TO_SEAM_START_SPEC.md) 파라미터 해석.</summary>
public class MoveToSeamStartParserTests
{
    // 가이드 §5 골든 메시지의 액션 — params 없음, jobRef/position 2쌍.
    private const string Golden = """
        {"actionType":"moveToSeamStart","actionId":"8f3c19aa-0000-4000-8000-0000000000e2","blockingType":"HARD",
         "actionParameters":[
           {"key":"jobRef","value":"TEST-SEAM-3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47"},
           {"key":"position","value":{
              "seamStartW":[12.510,5.980,1.420],
              "seamEndW":[13.310,5.980,1.420],
              "drawingPos":{"tank":"CT1","level":2,"wall_code":"SM","u":3.120,"v":1.420,"x":3.120,"y":0.0,"z":1.420}}}]}
        """;

    private static VdaAction Parse(string json) => JsonSerializer.Deserialize<VdaAction>(json)!;

    [Fact]
    public void GoldenMessage_Parses()
    {
        Assert.True(WeldInspectionActionParser.TryParseSeamStart(Parse(Golden), out var req, out var err), err);
        Assert.Equal("TEST-SEAM-3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47", req!.JobRef);
        Assert.Equal(new[] { 12.510, 5.980, 1.420 }, req.SeamStartW);
        Assert.Equal(new[] { 13.310, 5.980, 1.420 }, req.SeamEndW);
        Assert.Equal("SM", req.DrawingPos.WallCode);
        Assert.Equal(2, req.DrawingPos.Level);
    }

    // §8.3 문자열 폴백 — position 이 JSON 문자열로 실려 와도 수용.
    [Fact]
    public void PositionAsJsonString_Accepted()
    {
        var pos = """{"seamStartW":[1,2,3],"seamEndW":[2,2,3],"drawingPos":{"tank":"CT1","level":1,"wall_code":"PL","u":0,"v":0,"x":0,"y":0,"z":0}}""";
        var json = $$"""{"actionType":"moveToSeamStart","actionId":"a1","actionParameters":[{"key":"jobRef","value":"J"},{"key":"position","value":{{JsonSerializer.Serialize(pos)}}}]}""";
        Assert.True(WeldInspectionActionParser.TryParseSeamStart(Parse(json), out var req, out var err), err);
        Assert.Equal("PL", req!.DrawingPos.WallCode);
    }

    [Fact]
    public void MissingPosition_Rejected()
    {
        var json = """{"actionType":"moveToSeamStart","actionId":"a1","actionParameters":[{"key":"jobRef","value":"J"}]}""";
        Assert.False(WeldInspectionActionParser.TryParseSeamStart(Parse(json), out _, out var err));
        Assert.Contains("jobRef/position", err);
    }

    [Fact]
    public void SeamStartWithTwoNumbers_Rejected()
    {
        var json = Golden.Replace("[12.510,5.980,1.420]", "[12.510,5.980]");
        Assert.False(WeldInspectionActionParser.TryParseSeamStart(Parse(json), out _, out var err));
        Assert.Contains("seamStartW", err);
    }

    // 면 법선 자세를 wall_code 로 정하므로 정본 10코드 밖이면 거부.
    [Fact]
    public void UndefinedWallCode_Rejected()
    {
        var json = Golden.Replace("\"wall_code\":\"SM\"", "\"wall_code\":\"XX\"");
        Assert.False(WeldInspectionActionParser.TryParseSeamStart(Parse(json), out _, out var err));
        Assert.Contains("wall_code", err);
    }
}
