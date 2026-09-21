using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Models;

namespace HD.AMR.Tests;

public class WallCodesTests
{
    // 정본 10코드 — Surface ID 는 vision_interface.md §5 순서(0x01~0x0A). 표가 바뀌면 이 테스트로 드러난다.
    [Theory]
    [InlineData("B", 0x01, SurfaceOrientation.Floor)]
    [InlineData("T", 0x02, SurfaceOrientation.Ceiling)]
    [InlineData("PM", 0x03, SurfaceOrientation.Wall)]
    [InlineData("SM", 0x04, SurfaceOrientation.Wall)]
    [InlineData("F", 0x05, SurfaceOrientation.Wall)]
    [InlineData("A", 0x06, SurfaceOrientation.Wall)]
    [InlineData("PL", 0x07, SurfaceOrientation.ChamferLower)]
    [InlineData("SL", 0x08, SurfaceOrientation.ChamferLower)]
    [InlineData("PU", 0x09, SurfaceOrientation.ChamferUpper)]
    [InlineData("SU", 0x0A, SurfaceOrientation.ChamferUpper)]
    public void Find_ReturnsCanonicalDefinition(string code, int surfaceId, SurfaceOrientation orientation)
    {
        var w = WallCodes.Find(code);
        Assert.NotNull(w);
        Assert.Equal(surfaceId, w!.SurfaceId);
        Assert.Equal(orientation, w.Orientation);
        Assert.Same(w, WallCodes.FindBySurfaceId(surfaceId));
    }

    [Fact]
    public void All_HasTenUniqueCodesAndSurfaceIds()
    {
        Assert.Equal(10, WallCodes.All.Count);
        Assert.Equal(10, WallCodes.All.Select(w => w.Code).Distinct().Count());
        Assert.Equal(Enumerable.Range(0x01, 10), WallCodes.All.Select(w => w.SurfaceId).OrderBy(x => x));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("b")]
    [InlineData("X")]
    public void Find_UnknownCode_ReturnsNull(string? code) => Assert.Null(WallCodes.Find(code));

    // 레시피 매핑과 Teaching 슬롯이 같은 표를 쓰는지 — 두 경로가 어긋나면 엉뚱한 면 자세로 이동한다.
    [Fact]
    public void RecipeResolverAndTeachingSlots_UseSameTable()
    {
        foreach (var w in WallCodes.All)
        {
            Assert.Equal(w.Orientation, InspectionRecipeResolver.ResolveOrientation(w.Code));
            var slot = TeachingService.Slots.Single(s => s.Key == TeachingService.WallSlotKey(w.Code));
            Assert.Equal(w.SurfaceId, slot.SurfaceId);
            Assert.True(TeachingService.IsReservedSurfaceId(w.SurfaceId));
        }
        Assert.False(TeachingService.IsReservedSurfaceId(0x00));
        Assert.False(TeachingService.IsReservedSurfaceId(0x0B));
    }

    // 비전 Surface ID 카탈로그는 정본 표에서 생성 — 번호·이름·축이 같고, 사양 시트 5 표기가 유지되는지.
    [Theory]
    [InlineData(0x01, "바닥 (Bottom)", "U: 선수→선미, V: 좌현→우현")]
    [InlineData(0x05, "전벽 (Forward)", "U: 좌현→우현, V: 바닥→천장")]
    [InlineData(0x0A, "상부 우현 챔퍼", "U: 선수→선미, V: 천장→우현벽")]
    public void VisionSurfaceCatalog_IsBuiltFromWallCodes(int id, string name, string axes)
    {
        Assert.Equal(WallCodes.All.Select(w => w.SurfaceId), SurfaceCatalog.All.Select(s => (int)s.Id));
        var s = SurfaceCatalog.All.Single(x => x.Id == id);
        Assert.Equal(name, s.Name);
        Assert.Equal(axes, s.Axes);
        Assert.Equal(name, SurfaceCatalog.NameOf((ushort)id));
    }
}
