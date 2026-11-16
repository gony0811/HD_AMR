using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>ACS 이동 명령 정차 오프셋(<see cref="StationOffset"/>) — 맵 X(선수–선미)·Y(좌현–우현), mm.</summary>
public class StationOffsetTests
{
    [Fact]
    public void 밀리미터_오프셋을_미터_좌표에_더한다()
    {
        // 2026-11-16 R01C17 노드 (24.077, 2.56) 에서 벽 쪽(맵 −y)으로 100mm, 선미 쪽(맵 +x)으로 50mm.
        var (x, y) = StationOffset.Apply(24.077, 2.56, offsetXMm: 50, offsetYMm: -100);

        Assert.Equal(24.127, x, 6);
        Assert.Equal(2.46, y, 6);
    }

    [Fact]
    public void 오프셋_0이면_좌표_그대로()
    {
        var (x, y) = StationOffset.Apply(23.96, 2.634, 0, 0);
        Assert.Equal(23.96, x, 9);
        Assert.Equal(2.634, y, 9);
    }

    [Theory]
    [InlineData(null, 0.0)]
    [InlineData(150.0, 150.0)]
    [InlineData(-1000.0, -1000.0)]
    [InlineData(1000.1, 0.0)]       // 범위 밖 — 설정 실수로 보고 0
    [InlineData(-5000.0, 0.0)]
    [InlineData(double.NaN, 0.0)]
    public void 범위_밖이나_빈_값은_0(double? raw, double expected)
        => Assert.Equal(expected, StationOffset.Sanitize(raw));

    private const string PositionJson =
        """{"seamStartW":[24.268,1.287,2.2425],"seamEndW":[24.628,1.294,2.2425],"drawingPos":{"wall_code":"SM","x":1.0,"y":2.0,"z":3.0}}""";

    private static VdaAction Action(object value) => new()
    {
        ActionType = "startWeldInspection",
        ActionId = "a1",
        ActionParameters = [new ActionParameter { Key = "position", Value = value }],
    };

    private static JsonElement PositionOf(VdaAction a) => (JsonElement)a.ActionParameters[0].Value!;

    [Fact]
    public void 용접선_좌표도_같은_만큼_옮겨_TCP_가_따라간다()
    {
        var a = Action(JsonDocument.Parse(PositionJson).RootElement.Clone());

        Assert.True(StationOffset.ShiftSeamPosition(a, offsetXMm: -400, offsetYMm: 50));

        var p = PositionOf(a);
        var s = p.GetProperty("seamStartW");
        var e = p.GetProperty("seamEndW");
        Assert.Equal(23.868, s[0].GetDouble(), 6);
        Assert.Equal(1.337, s[1].GetDouble(), 6);
        Assert.Equal(2.2425, s[2].GetDouble(), 6);          // 높이는 그대로
        Assert.Equal(24.228, e[0].GetDouble(), 6);
        Assert.Equal(1.344, e[1].GetDouble(), 6);
        Assert.Equal(1.0, p.GetProperty("drawingPos").GetProperty("x").GetDouble(), 6);   // 도면 좌표는 그대로
    }

    [Fact]
    public void 문자열에_담긴_position_도_옮긴다()
    {
        var a = Action(JsonSerializer.SerializeToElement(PositionJson));   // JSON 을 문자열로 보낸 경우(§8.3 폴백)

        Assert.True(StationOffset.ShiftSeamPosition(a, -400, 0));
        Assert.Equal(23.868, PositionOf(a).GetProperty("seamStartW")[0].GetDouble(), 6);
    }

    [Fact]
    public void 노드와_용접선을_같이_옮기면_AMR_기준_상대_위치는_그대로()
    {
        var (nx, ny) = (22.276, 2.744);
        var a = Action(JsonDocument.Parse(PositionJson).RootElement.Clone());
        var before = PositionOf(a).GetProperty("seamStartW");
        var (relX0, relY0) = (before[0].GetDouble() - nx, before[1].GetDouble() - ny);

        var (sx, sy) = StationOffset.Apply(nx, ny, -400, 0);
        StationOffset.ShiftSeamPosition(a, -400, 0);
        var after = PositionOf(a).GetProperty("seamStartW");

        Assert.Equal(relX0, after[0].GetDouble() - sx, 9);
        Assert.Equal(relY0, after[1].GetDouble() - sy, 9);
    }

    [Fact]
    public void position_이_없으면_손대지_않는다()
    {
        var a = new VdaAction { ActionType = "x", ActionParameters = [new ActionParameter { Key = "params", Value = 1 }] };
        Assert.False(StationOffset.ShiftSeamPosition(a, -400, 0));
        Assert.Equal(1, a.ActionParameters[0].Value);
    }
}
