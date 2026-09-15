using System.Text.Json;
using HD.AMR.App.Data.Entities;

namespace HD.AMR.Tests;

public class InspectionWaypointCompatTests
{
    // 구버전 WaypointsJson(RzDeg/Y/Surface 필드 없음) 역직렬화 — 신 필드는 기본값으로 채워져야 한다.
    [Fact]
    public void Deserialize_LegacyJson_DefaultsNewFields()
    {
        const string legacy = """[{"X":10.5,"Z":-3.0,"Theta":12.0,"ThetaManual":true}]""";

        var list = JsonSerializer.Deserialize<List<InspectionWaypoint>>(legacy)!;

        var w = Assert.Single(list);
        Assert.Equal(10.5, w.X);
        Assert.Equal(-3.0, w.Z);
        Assert.Equal(12.0, w.Theta);
        Assert.True(w.ThetaManual);
        Assert.Equal(0, w.Surface);
        Assert.False(w.SurfaceManual);
        Assert.Equal(0, w.Y);
        Assert.Equal(0, w.RzDeg);
    }

    // RzDeg 포함 왕복 직렬화 보존.
    [Fact]
    public void Roundtrip_WithRzDeg_Preserved()
    {
        var src = new List<InspectionWaypoint>
        {
            new(X: 0, Z: -180, Theta: 0, ThetaManual: false, RzDeg: -90),
        };

        var json = JsonSerializer.Serialize(src);
        var back = JsonSerializer.Deserialize<List<InspectionWaypoint>>(json)!;

        Assert.Equal(-90, Assert.Single(back).RzDeg);
    }
}
