using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

public class InspectionRecipeResolverTests
{
    private static WeldInspectionRequest MakeRequest(SeamTypeKind seamType, string wallCode) => new(
        JobRef: "JOB-T",
        SeamStartW: new[] { 0.0, 0.0, 0.0 },
        SeamEndW: new[] { 1.0, 0.0, 0.0 },
        DrawingPos: new WeldDrawingPos("CT1", 1, wallCode, null, null, 0, 0, 0),
        SeamType: seamType,
        SectionDxfId: "D",
        InspectionProfileId: "P",
        StandoffMm: 400,
        WorkingDistanceMm: null,
        AnchorGroupId: "G",
        SeqInGroup: 1);

    // 사양 §8.5.1 (3) 매핑표 전 조합 — LINE/CROSS × 면자세 5군.
    [Theory]
    [InlineData(SeamTypeKind.Line, "B", "LINE-FLOOR")]
    [InlineData(SeamTypeKind.Line, "T", "LINE-CEIL")]
    [InlineData(SeamTypeKind.Line, "SM", "LINE-WALL")]
    [InlineData(SeamTypeKind.Line, "PM", "LINE-WALL")]
    [InlineData(SeamTypeKind.Line, "F", "LINE-WALL")]
    [InlineData(SeamTypeKind.Line, "A", "LINE-WALL")]
    [InlineData(SeamTypeKind.Line, "SL", "LINE-CHMR-LO")]
    [InlineData(SeamTypeKind.Line, "PL", "LINE-CHMR-LO")]
    [InlineData(SeamTypeKind.Line, "SU", "LINE-CHMR-UP")]
    [InlineData(SeamTypeKind.Line, "PU", "LINE-CHMR-UP")]
    [InlineData(SeamTypeKind.Cross, "B", "CROSS4-FLOOR")]
    [InlineData(SeamTypeKind.Cross, "T", "CROSS4-CEIL")]
    [InlineData(SeamTypeKind.Cross, "SM", "CROSS4-WALL")]
    [InlineData(SeamTypeKind.Cross, "PM", "CROSS4-WALL")]
    [InlineData(SeamTypeKind.Cross, "F", "CROSS4-WALL")]
    [InlineData(SeamTypeKind.Cross, "A", "CROSS4-WALL")]
    [InlineData(SeamTypeKind.Cross, "SL", "CROSS4-CHMR-LO")]
    [InlineData(SeamTypeKind.Cross, "PL", "CROSS4-CHMR-LO")]
    [InlineData(SeamTypeKind.Cross, "SU", "CROSS4-CHMR-UP")]
    [InlineData(SeamTypeKind.Cross, "PU", "CROSS4-CHMR-UP")]
    public void Resolve_LineAndCross_MatchesSpecTable(SeamTypeKind seamType, string wallCode, string expected)
    {
        var ok = InspectionRecipeResolver.TryResolve(MakeRequest(seamType, wallCode), out var recipeId, out var error);

        Assert.True(ok, error);
        Assert.Equal(expected, recipeId);
    }

    // CORNER: 면 자세 무관 단일 CORNER3 (§8.5.1 (3)).
    [Theory]
    [InlineData("B")]
    [InlineData("T")]
    [InlineData("SM")]
    [InlineData("SL")]
    [InlineData("PU")]
    public void Resolve_Corner_AlwaysCorner3(string wallCode)
    {
        var ok = InspectionRecipeResolver.TryResolve(MakeRequest(SeamTypeKind.Corner, wallCode), out var recipeId, out _);

        Assert.True(ok);
        Assert.Equal("CORNER3", recipeId);
    }

    // 미정의 wall_code — 골든 예시의 "W03" 포함(§8.4↔§8.5.1 불일치 방어).
    [Theory]
    [InlineData("W03")]
    [InlineData("")]
    [InlineData("b")]     // 대소문자 구분 — 정본은 대문자
    [InlineData("XX")]
    public void Resolve_UndefinedWallCode_Fails(string wallCode)
    {
        var ok = InspectionRecipeResolver.TryResolve(MakeRequest(SeamTypeKind.Line, wallCode), out _, out var error);

        Assert.False(ok);
        Assert.Contains("wall_code", error);
    }

    [Fact]
    public void RecipeIds_CatalogHasElevenEntries()
    {
        Assert.Equal(11, RecipeIds.All.Count);
        Assert.Equal(RecipeIds.All.Count, RecipeIds.All.Distinct().Count());
    }
}
