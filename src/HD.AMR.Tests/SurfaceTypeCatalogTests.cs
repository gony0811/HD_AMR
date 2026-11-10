using System.Text.Json;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;

namespace HD.AMR.Tests;

public class SurfaceTypeCatalogTests
{
    // 비전 측 사양 표: No → 저장 폴더 Metadata(No+1), 0~1 자동 / 2~ 수동.
    [Theory]
    [InlineData(SurfaceType.Flat,                         0, "Metadata1", false)]
    [InlineData(SurfaceType.Corrugation,                  1, "Metadata2", false)]
    [InlineData(SurfaceType.Manual3Axis,                  2, "Metadata3", true)]
    [InlineData(SurfaceType.Manual4Axis,                  3, "Metadata4", true)]
    [InlineData(SurfaceType.Manual90Flat,                 4, "Metadata5", true)]
    [InlineData(SurfaceType.Manual90Corrugation,          5, "Metadata6", true)]
    [InlineData(SurfaceType.Manual135Flat,                6, "Metadata7", true)]
    [InlineData(SurfaceType.Manual135Corrugation,         7, "Metadata8", true)]
    [InlineData(SurfaceType.ManualEndMembraneFlat,        8, "Metadata9", true)]
    [InlineData(SurfaceType.ManualEndMembraneCorrugation, 9, "Metadata10", true)]
    public void 사양표_번호_폴더_수동여부(SurfaceType type, byte value, string dir, bool manual)
    {
        Assert.Equal(value, (byte)type);
        var info = SurfaceTypes.Find(value)!;
        Assert.Equal(type, info.Type);
        Assert.Equal(dir, info.MetadataDir);
        Assert.Equal(manual, info.IsManual);
        Assert.Equal(manual, type.IsManual());
    }

    [Fact]
    public void 표는_enum_전체를_순서대로_포함()
    {
        Assert.Equal(Enum.GetValues<SurfaceType>(), SurfaceTypes.All.Select(t => t.Type));
        // UI ComboBox 가 SelectedIndex == 값 으로 바인딩하므로 0부터 연속이어야 한다.
        Assert.Equal(Enumerable.Range(0, SurfaceTypes.All.Count), SurfaceTypes.All.Select(t => (int)t.Type));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(255)]
    [InlineData(-1)]
    public void 미정의_값은_Flat(int value)
    {
        Assert.False(SurfaceTypes.IsDefined(value));
        Assert.Equal(SurfaceType.Flat, SurfaceTypes.OrFlat(value));
    }

    [Theory]
    [InlineData(0, 0)]   // 구 Flat
    [InlineData(1, 0)]   // 구 Corner → Flat
    [InlineData(2, 1)]   // 구 Corrugation → Corrugation(1)
    public void 구번호_변환(byte legacy, byte expected) => Assert.Equal(expected, SurfaceTypes.FromLegacy(legacy));

    [Fact]
    public void 경유점JSON_구번호_변환()
    {
        var legacy = JsonSerializer.Serialize(new List<InspectionWaypoint>
        {
            new(1, 2, 0, false, Surface: 0, SurfaceManual: false),
            new(3, 4, 0, false, Surface: 1, SurfaceManual: true),
            new(5, 6, 30, true, Surface: 2, SurfaceManual: true, Y: 7, RzDeg: -90),
        });

        var converted = WaypointSurfaceMigration.ConvertLegacyJson(legacy);
        var list = JsonSerializer.Deserialize<List<InspectionWaypoint>>(converted)!;

        Assert.Equal(new byte[] { 0, 0, 1 }, list.Select(w => w.Surface));
        Assert.Equal(-90, list[2].RzDeg);   // 다른 필드 보존
        Assert.Equal(7, list[2].Y);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""[{"X":1,"Z":2,"Theta":0,"ThetaManual":false}]""")]   // Surface 필드 없음
    public void 경유점JSON_변환대상_없으면_원문유지(string json) =>
        Assert.Equal(json, WaypointSurfaceMigration.ConvertLegacyJson(json));

    [Fact]
    public void CSV_표식유무로_구번호_해석()
    {
        Assert.Equal(1, SurfaceTypes.ParseCsvCell("2", currentSchema: false));   // 구 Corrugation
        Assert.Equal(0, SurfaceTypes.ParseCsvCell("1", currentSchema: false));   // 구 Corner
        Assert.Equal(2, SurfaceTypes.ParseCsvCell("2", currentSchema: true));    // 신 Manual3Axis
        Assert.Equal(9, SurfaceTypes.ParseCsvCell(" 9 ", currentSchema: true));
        Assert.Equal(0, SurfaceTypes.ParseCsvCell("10", currentSchema: true));   // 미정의 → Flat
        Assert.True(SurfaceTypes.IsCurrentSchemaCsv($"# a\r\n{SurfaceTypes.CsvSchemaMarker}\r\n1,2,3"));
        Assert.False(SurfaceTypes.IsCurrentSchemaCsv("# Surface: 0=Flat, 1=Corner\r\n1,2,3"));
    }
}
