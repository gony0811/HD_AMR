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

    // 정본 10코드(B/T/SM/PM/F/A/SL/PL/SU/PU) 외 값은 거부 — 방어적 처리.
    [Theory]
    [InlineData("W03")]   // 번호식 표기(구 골든 예시 오기) — ACS 실발행값 아님
    [InlineData("")]
    [InlineData("b")]     // 대소문자 구분 — 정본은 대문자
    [InlineData("XX")]
    public void Resolve_UndefinedWallCode_Fails(string wallCode)
    {
        var ok = InspectionRecipeResolver.TryResolve(MakeRequest(SeamTypeKind.Line, wallCode), out _, out var error);

        Assert.False(ok);
        Assert.Contains("wall_code", error);
    }

    // CORNER3 좌/우 거울 side — P*(좌현)→L, S*(우현)→R, 그 외(F/A/B/T)는 규칙 미확정으로 기본 L.
    [Theory]
    [InlineData("PM", "L")]
    [InlineData("PL", "L")]
    [InlineData("PU", "L")]
    [InlineData("SM", "R")]
    [InlineData("SL", "R")]
    [InlineData("SU", "R")]
    [InlineData("F", "L")]
    [InlineData("A", "L")]
    [InlineData("B", "L")]
    [InlineData("T", "L")]
    public void ResolveCornerSide_MatchesRule(string wallCode, string expected)
    {
        Assert.Equal(expected, InspectionRecipeResolver.ResolveCornerSide(wallCode, out _));
    }

    // 미확정 코드(F/A 등)는 note 에 사유가 남아야 한다 — N13 협의 전 폴백 가시화.
    [Fact]
    public void ResolveCornerSide_UndecidedCode_NotesFallback()
    {
        InspectionRecipeResolver.ResolveCornerSide("F", out var note);
        Assert.Contains("미확정", note);
    }

    [Fact]
    public void RecipeIds_CatalogHasElevenEntries()
    {
        Assert.Equal(11, RecipeIds.All.Count);
        Assert.Equal(RecipeIds.All.Count, RecipeIds.All.Distinct().Count());
    }
}
