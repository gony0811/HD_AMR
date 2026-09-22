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
        { "key": "jobRef", "value": "JOB-CT1-L2-SM-S07-2" },
        { "key": "position", "value": {
            "seamStartW": [12.510, 5.980, 1.420],
            "seamEndW":   [13.310, 5.980, 1.420],
            "drawingPos": { "tank": "CT1", "level": 2, "wall_code": "SM",
                            "u": 3.120, "v": 1.420,
                            "x": 3.120, "y": 0.0, "z": 1.420 } } },
        { "key": "params", "value": {
            "seamType": "LINE",
            "sectionDxfId": "DXF-CORR-T12",
            "inspectionProfileId": "INSPECT-STD-01",
            "standoffMm": 400,
            "workingDistanceMm": 400,
            "anchorGroupId": "CT1-L2-SM-ST04",
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
        Assert.Equal("JOB-CT1-L2-SM-S07-2", req!.JobRef);
        Assert.Equal(new[] { 12.510, 5.980, 1.420 }, req.SeamStartW);
        Assert.Equal(new[] { 13.310, 5.980, 1.420 }, req.SeamEndW);
        Assert.Equal("CT1", req.DrawingPos.Tank);
        Assert.Equal(2, req.DrawingPos.Level);
        Assert.Equal("SM", req.DrawingPos.WallCode);
        Assert.Equal(3.120, req.DrawingPos.U);
        Assert.Equal(SeamTypeKind.Line, req.SeamType);
        Assert.Equal("DXF-CORR-T12", req.SectionDxfId);
        Assert.Equal("INSPECT-STD-01", req.InspectionProfileId);
        Assert.Equal(400, req.StandoffMm);
        Assert.Equal(400, req.WorkingDistanceMm);
        Assert.Equal("CT1-L2-SM-ST04", req.AnchorGroupId);
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

    // 계약 enum 확장(N13): LINE/CROSS3/CROSS4/CORNER2/CORNER3 + legacy CROSS→Cross·CORNER→Corner 수용.
    [Theory]
    [InlineData("LINE", SeamTypeKind.Line)]
    [InlineData("CROSS4", SeamTypeKind.Cross)]
    [InlineData("CROSS", SeamTypeKind.Cross)]      // legacy
    [InlineData("CROSS3", SeamTypeKind.Cross3)]
    [InlineData("CORNER3", SeamTypeKind.Corner)]
    [InlineData("CORNER", SeamTypeKind.Corner)]    // legacy
    [InlineData("CORNER2", SeamTypeKind.Corner2)]
    public void Parse_SeamTypeEnum_MapsToKind(string seamType, SeamTypeKind expected)
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

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Equal(expected, req!.SeamType);
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

    // ── taskId / attempt (ACS 추가분, 비전 CAPTURE_REQ v3.2 전달분) ──────────────

    private const string TaskIdGuid = "3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47";

    [Fact]
    public void Parse_TaskId_TopLevelParameter_Succeeds()
    {
        var action = Deserialize("""
        {
          "actionType": "startWeldInspection",
          "actionId": "t1",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-T1" },
            { "key": "taskId", "value": "3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47" },
            { "key": "attempt", "value": 2 },
            { "key": "position", "value": {
                "seamStartW": [1,2,3], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "SM", "x": 0, "y": 0, "z": 0 } } },
            { "key": "params", "value": {
                "seamType": "LINE", "sectionDxfId": "D1", "inspectionProfileId": "P1",
                "standoffMm": 400, "anchorGroupId": "G1", "seqInGroup": 1 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Equal(Guid.Parse(TaskIdGuid), req!.TaskId);
        Assert.Equal(TaskIdGuid, req.TaskIdRaw);
        Assert.Equal((byte)2, req.Attempt);
    }

    [Fact]
    public void Parse_TaskId_InsideParams_Succeeds()
    {
        // 계약상 위치가 확정되기 전이라 params 안에 실려 와도 같은 값으로 읽는다.
        var action = Deserialize("""
        {
          "actionType": "startWeldInspection",
          "actionId": "t2",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-T2" },
            { "key": "position", "value": {
                "seamStartW": [1,2,3], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "SM", "x": 0, "y": 0, "z": 0 } } },
            { "key": "params", "value": {
                "seamType": "LINE", "sectionDxfId": "D2", "inspectionProfileId": "P2",
                "standoffMm": 400, "anchorGroupId": "G2", "seqInGroup": 1,
                "taskId": "3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47", "attempt": 3 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Equal(Guid.Parse(TaskIdGuid), req!.TaskId);
        Assert.Equal((byte)3, req.Attempt);
    }

    [Theory]
    [InlineData("3a9f2c148e514d7ab2c91f6e0a5d3b47")]          // 하이픈 없음
    [InlineData("{3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47}")]    // 중괄호
    [InlineData("3A9F2C14-8E51-4D7A-B2C9-1F6E0A5D3B47")]      // 대문자
    public void Parse_TaskId_AlternateGuidFormats_Succeed(string raw)
    {
        var action = Deserialize($$"""
        {
          "actionType": "startWeldInspection",
          "actionId": "t3",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-T3" },
            { "key": "taskId", "value": "{{raw}}" },
            { "key": "position", "value": {
                "seamStartW": [1,2,3], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "SM", "x": 0, "y": 0, "z": 0 } } },
            { "key": "params", "value": {
                "seamType": "LINE", "sectionDxfId": "D3", "inspectionProfileId": "P3",
                "standoffMm": 400, "anchorGroupId": "G3", "seqInGroup": 1 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Equal(Guid.Parse(TaskIdGuid), req!.TaskId);
    }

    [Fact]
    public void Parse_TaskId_Absent_LeavesNull_AndSucceeds()
    {
        // 현행 ACS(미발행) 하위호환 — taskId 없이도 액션은 유효하며 스텝이 Guid.Empty/1 로 폴백한다.
        var action = Deserialize(GoldenActionJson);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Null(req!.TaskId);
        Assert.Null(req.TaskIdRaw);
        Assert.Null(req.Attempt);
    }

    [Fact]
    public void Parse_TaskId_NotAGuid_KeepsRaw_AndSucceeds()
    {
        // GUID 가 아니면 액션을 거부하지 않고 원문만 보존 — 호출측이 경고 후 Guid.Empty 로 전송한다.
        // attempt 는 1부터라 0 은 무시(폴백 1).
        var action = Deserialize("""
        {
          "actionType": "startWeldInspection",
          "actionId": "t4",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-T4" },
            { "key": "taskId", "value": "TASK-CT1-L2-SM-07" },
            { "key": "attempt", "value": 0 },
            { "key": "position", "value": {
                "seamStartW": [1,2,3], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "SM", "x": 0, "y": 0, "z": 0 } } },
            { "key": "params", "value": {
                "seamType": "LINE", "sectionDxfId": "D4", "inspectionProfileId": "P4",
                "standoffMm": 400, "anchorGroupId": "G4", "seqInGroup": 1 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);
        Assert.Null(req!.TaskId);
        Assert.Equal("TASK-CT1-L2-SM-07", req.TaskIdRaw);
        Assert.Null(req.Attempt);
        Assert.Equal("0", req.AttemptRaw);   // 원문은 진단용으로 보존
    }

    [Theory]
    [InlineData("256", null)]        // UInt8 범위 밖 → 폴백(1), 원문만 보존
    [InlineData("\"2\"", (byte)2)]   // 문자열 표기 — 수치면 수용
    public void Parse_Attempt_RangeAndStringForm(string attemptJson, byte? expected)
    {
        var action = Deserialize($$"""
        {
          "actionType": "startWeldInspection",
          "actionId": "t5",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-T5" },
            { "key": "taskId", "value": "3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47" },
            { "key": "attempt", "value": {{attemptJson}} },
            { "key": "position", "value": {
                "seamStartW": [1,2,3], "seamEndW": [4,5,6],
                "drawingPos": { "tank": "CT1", "level": 1, "wall_code": "SM", "x": 0, "y": 0, "z": 0 } } },
            { "key": "params", "value": {
                "seamType": "LINE", "sectionDxfId": "D5", "inspectionProfileId": "P5",
                "standoffMm": 400, "anchorGroupId": "G5", "seqInGroup": 1 } }
          ]
        }
        """);

        var ok = WeldInspectionActionParser.TryParse(action, out var req, out var error);

        Assert.True(ok, error);   // 범위 밖이어도 액션은 거부하지 않는다(전환 유예)
        Assert.Equal(expected, req!.Attempt);
        Assert.Equal(attemptJson.Trim('"'), req.AttemptRaw);
    }
}
