using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

public class WeldInspectionActionParserTests
{
    /// <summary>사양 §8.4 골든 예시 전문 — 계약 정본.</summary>
    private const string GoldenActionJson = """
    {
      "actionType": "startWeldInspection",
      "actionId": "8f3c19aa-0000-4000-8000-0000000000e2",
      "blockingType": "HARD",
      "actionParameters": [
        { "key": "jobRef", "value": "JOB-CT1-L2-W03-S07-2" },
        { "key": "position", "value": {
            "seamStartW": [12.510, 5.980, 1.420],
            "seamEndW":   [13.310, 5.980, 1.420],
            "drawingPos": { "tank": "CT1", "level": 2, "wall_code": "W03",
                            "u": 3.120, "v": 1.420,
                            "x": 3.120, "y": 0.0, "z": 1.420 } } },
        { "key": "params", "value": {
            "seamType": "LINE",
            "sectionDxfId": "DXF-CORR-T12",
            "inspectionProfileId": "INSPECT-STD-01",
            "standoffMm": 400,
            "workingDistanceMm": 400,
            "anchorGroupId": "CT1-L2-W03-ST04",
            "seqInGroup": 2 } }
      ]
    }
    """;

    private static VdaAction Deserialize(string json) =>
        JsonSerializer.Deserialize<VdaAction>(json)!;

    [Fact]
    public void Parse_GoldenExample_Succeeds()
    {
        var action = Deserialize(GoldenActionJson);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.NotNull(req);
        Assert.Equal("JOB-CT1-L2-W03-S07-2", req!.JobRef);
        Assert.Equal(new[] { 12.510, 5.980, 1.420 }, req.SeamStartW);
        Assert.Equal(new[] { 13.310, 5.980, 1.420 }, req.SeamEndW);
        Assert.Equal("CT1", req.DrawingPos.Tank);
        Assert.Equal(2, req.DrawingPos.Level);
        Assert.Equal("W03", req.DrawingPos.WallCode);
        Assert.Equal(3.120, req.DrawingPos.U);
        Assert.Equal(SeamTypeKind.Line, req.SeamType);
        Assert.Equal("DXF-CORR-T12", req.SectionDxfId);
        Assert.Equal("INSPECT-STD-01", req.InspectionProfileId);
        Assert.Equal(400, req.StandoffMm);
        Assert.Equal(400, req.WorkingDistanceMm);
        Assert.Equal("CT1-L2-W03-ST04", req.AnchorGroupId);
        Assert.Equal(2, req.SeqInGroup);
    }

    [Fact]
    public void Parse_StringEncodedObjects_Succeeds()
    {
        // §8.3/N7: value 가 JSON 문자열로 실려 와도 재파싱 수용.
        var action = Deserialize("""
        {
          "actionType": "startWeldInspection",
          "actionId": "a1",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-1" },
            { "key": "position", "value": "{\"seamStartW\":[1,2,3],\"seamEndW\":[4,5,6],\"drawingPos\":{\"tank\":\"CT1\",\"level\":1,\"wall_code\":\"B\",\"x\":0,\"y\":0,\"z\":0}}" },
            { "key": "params", "value": "{\"seamType\":\"LINE\",\"sectionDxfId\":\"D1\",\"inspectionProfileId\":\"P1\",\"standoffMm\":350,\"anchorGroupId\":\"G1\",\"seqInGroup\":1}" }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Equal("B", req!.DrawingPos.WallCode);
        Assert.Equal(350, req.StandoffMm);
        Assert.Null(req.WorkingDistanceMm);   // 선택 필드 부재 허용
    }

    [Fact]
    public void Parse_MissingUv_Succeeds()
    {
        // §8.2 각주: AMR 파서는 u,v 부재도 수용.
        var action = Deserialize("""
        {
          "actionType": "startWeldInspection",
          "actionId": "a2",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-2" },
            { "key": "position", "value": {
                "seamStartW": [1,2,3], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "SM", "x": 1, "y": 2, "z": 3 } } },
            { "key": "params", "value": {
                "seamType": "CROSS", "sectionDxfId": "D2", "inspectionProfileId": "P2",
                "standoffMm": 400, "anchorGroupId": "G2", "seqInGroup": 3 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Null(req!.DrawingPos.U);
        Assert.Null(req.DrawingPos.V);
        Assert.Equal(SeamTypeKind.Cross, req.SeamType);
    }

    [Fact]
    public void Parse_WithPointsArray_IgnoredAndSucceeds()
    {
        // §8.2: params.points 는 스키마에 존재하나 의미 미정의(N13) — 파서는 무시하고 통과해야 한다.
        var action = Deserialize("""
        {
          "actionType": "startWeldInspection",
          "actionId": "a3",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-3" },
            { "key": "position", "value": {
                "seamStartW": [1,2,3], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "SM", "x": 1, "y": 2, "z": 3 } } },
            { "key": "params", "value": {
                "seamType": "CROSS", "points": [[1,2,3],[4,5,6],[7,8,9],[10,11,12]],
                "sectionDxfId": "D3", "inspectionProfileId": "P3",
                "standoffMm": 400, "anchorGroupId": "G3", "seqInGroup": 1 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Equal(SeamTypeKind.Cross, req!.SeamType);
    }

    [Theory]
    [InlineData("POLYLINE")]   // §8.1: POLYLINE 은 거부
    [InlineData("ARC")]
    [InlineData("")]
    public void Parse_UndefinedSeamType_Fails(string seamType)
    {
        var action = Deserialize($$"""
        {
          "actionType": "startWeldInspection",
          "actionId": "a3",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-3" },
            { "key": "position", "value": {
                "seamStartW": [1,2,3], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "B", "x": 0, "y": 0, "z": 0 } } },
            { "key": "params", "value": {
                "seamType": "{{seamType}}", "sectionDxfId": "D3", "inspectionProfileId": "P3",
                "standoffMm": 400, "anchorGroupId": "G3", "seqInGroup": 1 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out _, out var error);

        Assert.False(ok);
        Assert.Contains("seamType", error);
    }

    [Fact]
    public void Parse_MissingParamsKey_Fails()
    {
        var action = Deserialize("""
        {
          "actionType": "startWeldInspection",
          "actionId": "a4",
          "actionParameters": [ { "key": "jobRef", "value": "JOB-4" } ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out _, out var error);

        Assert.False(ok);
        Assert.Contains("position", error);
    }

    [Fact]
    public void Parse_BadSeamVector_Fails()
    {
        var action = Deserialize("""
        {
          "actionType": "startWeldInspection",
          "actionId": "a5",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-5" },
            { "key": "position", "value": {
                "seamStartW": [1,2], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "B", "x": 0, "y": 0, "z": 0 } } },
            { "key": "params", "value": {
                "seamType": "LINE", "sectionDxfId": "D5", "inspectionProfileId": "P5",
                "standoffMm": 400, "anchorGroupId": "G5", "seqInGroup": 1 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out _, out var error);

        Assert.False(ok);
        Assert.Contains("seamStartW", error);
    }
}
