using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;

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

    // §8.5.1 / §9-8 — LINE/CROSS4 는 면 자세-독립 단일 레시피. 정본 10코드 전부가 같은 단일 id 로 수렴한다.
    [Theory]
    [InlineData(SeamTypeKind.Line, "B", "LINE")]
    [InlineData(SeamTypeKind.Line, "T", "LINE")]
    [InlineData(SeamTypeKind.Line, "SM", "LINE")]
    [InlineData(SeamTypeKind.Line, "PM", "LINE")]
    [InlineData(SeamTypeKind.Line, "F", "LINE")]
    [InlineData(SeamTypeKind.Line, "A", "LINE")]
    [InlineData(SeamTypeKind.Line, "SL", "LINE")]
    [InlineData(SeamTypeKind.Line, "PL", "LINE")]
    [InlineData(SeamTypeKind.Line, "SU", "LINE")]
    [InlineData(SeamTypeKind.Line, "PU", "LINE")]
    [InlineData(SeamTypeKind.Cross, "B", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "T", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "SM", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "PM", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "F", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "A", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "SL", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "PL", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "SU", "CROSS4")]
    [InlineData(SeamTypeKind.Cross, "PU", "CROSS4")]
    public void Resolve_LineAndCross_AreSurfaceIndependent(SeamTypeKind seamType, string wallCode, string expected)
    {
        var ok = InspectionRecipeResolver.TryResolve(MakeRequest(seamType, wallCode), out var recipeId, out var error);

        Assert.True(ok, error);
        Assert.Equal(expected, recipeId);
    }

    // 경량 헬퍼 ResolveRecipeId(seam, wallCode) — request 없이 매핑(§8.5.1). UI 매핑 레퍼런스가 사용.
    [Theory]
    [InlineData(SeamTypeKind.Line, "B", "LINE")]
    [InlineData(SeamTypeKind.Line, "F", "LINE")]           // 마구리도 동일 단일 레시피
    [InlineData(SeamTypeKind.Cross, "T", "CROSS4")]
    [InlineData(SeamTypeKind.Cross3R90, "SM", "CROSS3-R90")]
    [InlineData(SeamTypeKind.Corner, "PM", "CORNER3")]
    [InlineData(SeamTypeKind.Corner2, "PM", "CORNER2")]
    [InlineData(SeamTypeKind.Corner2, "SM", "CORNER2")]
    public void ResolveRecipeId_MatchesTryResolve(SeamTypeKind seamType, string wallCode, string expected)
    {
        Assert.Equal(expected, InspectionRecipeResolver.ResolveRecipeId(seamType, wallCode));
    }

    [Fact]
    public void ResolveRecipeId_UndefinedWallCode_ReturnsNull()
    {
        Assert.Null(InspectionRecipeResolver.ResolveRecipeId(SeamTypeKind.Line, "W03"));
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

    // CROSS3 회전 4종: 면 자세 무관 — 회전별 단일 레시피(CORNER 와 동일한 short-circuit).
    [Theory]
    [InlineData(SeamTypeKind.Cross3R0, "CROSS3-R0")]
    [InlineData(SeamTypeKind.Cross3R90, "CROSS3-R90")]
    [InlineData(SeamTypeKind.Cross3R180, "CROSS3-R180")]
    [InlineData(SeamTypeKind.Cross3R270, "CROSS3-R270")]
    public void Resolve_Cross3Rotation_SurfaceIndependent(SeamTypeKind seamType, string expected)
    {
        foreach (var wallCode in new[] { "B", "T", "SM", "SL", "PU" })
        {
            var ok = InspectionRecipeResolver.TryResolve(MakeRequest(seamType, wallCode), out var recipeId, out var error);
            Assert.True(ok, error);
            Assert.Equal(expected, recipeId);
        }
    }

    // 회전 CROSS3 라도 wall_code 는 정본 코드여야 한다(면 자세 무관 ≠ wall_code 무검증) — CORNER 와 동일.
    [Fact]
    public void Resolve_Cross3Rotation_UndefinedWallCode_Fails()
    {
        var ok = InspectionRecipeResolver.TryResolve(MakeRequest(SeamTypeKind.Cross3R90, "XX"), out var recipeId, out var error);
        Assert.False(ok);
        Assert.Null(recipeId);
        Assert.Contains("wall_code", error);
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
    public void RecipeIds_CatalogHasEightEntries()
    {
        // LINE 1 + CROSS3 회전 4 + CROSS4 1 + CORNER2 1 + CORNER3 1 = 8 (면 자세-독립 통합, §9-8).
        Assert.Equal(8, RecipeIds.All.Count);
        Assert.Equal(RecipeIds.All.Count, RecipeIds.All.Distinct().Count());
    }

    // 교시 화면: 레시피 id → 프로필 SeamType 문자열(ACS 실행 선택은 레시피 id, 타입은 파생 표기).
    [Theory]
    [InlineData("LINE", "LINE")]
    [InlineData("CROSS3-R0", "CROSS3_R0")]
    [InlineData("CROSS3-R90", "CROSS3_R90")]
    [InlineData("CROSS3-R180", "CROSS3_R180")]
    [InlineData("CROSS3-R270", "CROSS3_R270")]
    [InlineData("CROSS4", "CROSS")]
    [InlineData("CORNER2", "CORNER2")]
    [InlineData("CORNER3", "CORNER3")]
    public void ProfileSeamTypeOf_MapsRecipeToProfileSeam(string recipeId, string expected)
    {
        Assert.Equal(expected, InspectionRecipeResolver.ProfileSeamTypeOf(recipeId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LINE-WALL")]    // 통합으로 폐기된 구 변종 id — 더 이상 카탈로그에 없음
    [InlineData("CROSS4-XYZ")]
    public void ProfileSeamTypeOf_UnknownRecipe_ReturnsNull(string? recipeId)
    {
        Assert.Null(InspectionRecipeResolver.ProfileSeamTypeOf(recipeId));
    }

    // 모든 카탈로그 레시피는 프로필 타입으로 유도 가능해야 한다(교시 화면 저장 전제).
    [Fact]
    public void ProfileSeamTypeOf_CoversWholeCatalog()
    {
        Assert.All(RecipeIds.All, id => Assert.NotNull(InspectionRecipeResolver.ProfileSeamTypeOf(id)));
    }

    private static InspectionRecipe LineRecipe(int? chamferSlopeProfileId = 20) => new()
    {
        Id = RecipeIds.Line,
        InspectionProfileId = 8,
        ChamferSlopeProfileId = chamferSlopeProfileId,
    };

    // 챔퍼 면(SL/PL/SU/PU) 수직 = 경사 방향(피치 254.6mm) → 챔퍼 수직용 프로필.
    [Theory]
    [InlineData("SL")]
    [InlineData("PL")]
    [InlineData("SU")]
    [InlineData("PU")]
    public void SelectProfileId_ChamferVertical_UsesChamferSlopeProfile(string wall)
    {
        var (id, slope) = InspectionRecipeResolver.SelectProfileId(LineRecipe(), wall, InspectionMoveDirection.Vertical);
        Assert.True(slope);
        Assert.Equal(20, id);
    }

    // 챔퍼 수평(선체 길이 방향 360mm)과 일반 면은 방향과 무관하게 기본 프로필.
    [Theory]
    [InlineData("SU", InspectionMoveDirection.Horizontal)]
    [InlineData("PL", InspectionMoveDirection.Horizontal)]
    [InlineData("SM", InspectionMoveDirection.Vertical)]
    [InlineData("B", InspectionMoveDirection.Vertical)]
    [InlineData("T", InspectionMoveDirection.Horizontal)]
    public void SelectProfileId_OtherCases_UseDefaultProfile(string wall, InspectionMoveDirection direction)
    {
        var (id, slope) = InspectionRecipeResolver.SelectProfileId(LineRecipe(), wall, direction);
        Assert.False(slope);
        Assert.Equal(8, id);
    }

    // 챔퍼 수직인데 전용 프로필 미지정 → null(기본 프로필로 대체하지 않음 — 호출측이 FAILED 보고).
    [Fact]
    public void SelectProfileId_ChamferVerticalUnassigned_ReturnsNullWithoutFallback()
    {
        var (id, slope) = InspectionRecipeResolver.SelectProfileId(LineRecipe(null), "SU", InspectionMoveDirection.Vertical);
        Assert.True(slope);
        Assert.Null(id);
    }

    // 챔퍼 수직용 프로필은 LINE 전용 — CROSS4 는 챔퍼 수직이어도 기본 프로필.
    [Fact]
    public void SelectProfileId_NonLineRecipe_IgnoresChamferSlopeProfile()
    {
        var recipe = new InspectionRecipe { Id = RecipeIds.Cross4, InspectionProfileId = 9, ChamferSlopeProfileId = 20 };
        var (id, slope) = InspectionRecipeResolver.SelectProfileId(recipe, "SU", InspectionMoveDirection.Vertical);
        Assert.False(slope);
        Assert.Equal(9, id);
    }
}
