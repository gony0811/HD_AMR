using System.Text.Json;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 검사 레시피 매핑(사양 §8.5.1, INSPECTION_TYPES.md §5) — 순수 함수 2단:
/// ① `wall_code` → 면 자세(유효성 확인용), ② `seamType` → 레시피 id 11종.
///
/// 정본 10코드(B/T/SM/PM/F/A/SL/PL/SU/PU, ACS 확정 2026-09-15) 외 wall_code 는 계약 위반으로
/// 실패를 반환한다 — 호출측이 액션 FAILED + orderValidationError 로 보고.
/// <b>면 자세는 레시피 선택에 쓰지 않는다</b> — LINE/CROSS4(좌·우)/CROSS3(회전4)/CORNER 모두 면 자세-독립 단일(또는 회전별)
/// 레시피다(INSPECTION_TYPES.md §9-8). 접근 자세·법선·방향은 런타임이 `wall_code`로 직접 계산하고
/// (<see cref="SeamBaseTransform"/>), 면별 경유점 차이는 레시피에 바인딩된 티칭 프로파일이 흡수한다.
/// CORNER2/CORNER3 은 삼면 코너 각도 전부 135°·90°·90° 균일(INSPECTION_TYPES.md §7). 거울 L/R 분리(§9-4)는 N13 확정 시 wall_code 판별 추가.
/// </summary>
public static class InspectionRecipeResolver
{
    /// <summary>wall_code(10코드 정본, <see cref="WallCodes"/>) → 면 자세. 미정의 코드는 null.</summary>
    public static SurfaceOrientation? ResolveOrientation(string wallCode) => WallCodes.Find(wallCode)?.Orientation;

    /// <summary>레시피 id 유도. 실패 시 error 에 사유(계약 위반 — orderValidationError 계열).</summary>
    public static bool TryResolve(WeldInspectionRequest request, out string? recipeId, out string? error,
        IReadOnlySet<string>? cross4RightWalls = null)
    {
        recipeId = ResolveRecipeId(request.SeamType, request.DrawingPos.WallCode, cross4RightWalls);
        error = recipeId is null
            ? $"미정의 wall_code '{request.DrawingPos.WallCode}' — 정본 10코드(B/T/SM/PM/F/A/SL/PL/SU/PU)만 수용 (§8.5.1)"
            : null;
        return recipeId is not null;
    }

    /// <summary>seamType → 레시피 id (§8.5.1 매핑표). wall_code 는 유효성(정본 10코드) 확인에만 쓰고
    /// 레시피 선택에는 관여하지 않는다 — 미정의 wall_code 는 null(계약 위반).
    /// UI 매핑 레퍼런스 등 request 없이 조회할 때 쓰는 경량 경로 — <see cref="TryResolve"/> 가 위임한다.
    /// <paramref name="cross4RightWalls"/> = 타입을 밝히지 않은 구 "CROSS4" 를 T2 로 실행할 면
    /// (<see cref="Cross4RightWalls"/>). 주지 않으면 전부 T1.</summary>
    public static string? ResolveRecipeId(SeamTypeKind seamType, string wallCode, IReadOnlySet<string>? cross4RightWalls = null)
    {
        // 면 자세는 레시피 선택에 쓰지 않는다 — 모든 seamType 이 면 자세-독립 단일(또는 회전별) 레시피다.
        // wall_code 는 여전히 정의 코드여야 한다(접근 자세·방향은 런타임이 wall_code 로 직접 계산).
        if (ResolveOrientation(wallCode) is null) return null;
        return seamType switch
        {
            SeamTypeKind.Line => RecipeIds.Line,
            SeamTypeKind.Cross4T1 => RecipeIds.Cross4T1,
            SeamTypeKind.Cross4T2 => RecipeIds.Cross4T2,
            SeamTypeKind.Cross4T3 => RecipeIds.Cross4T3,
            SeamTypeKind.Cross4T4 => RecipeIds.Cross4T4,
            // "CROSS4"(타입 미구분) — ACS 가 그 십자의 타입을 밝히지 않았으면 레시피에 설정한 면별 기본값을 쓴다.
            // 설정이 없는 면은 T1(구 L — 구 CROSS4 레시피의 설정을 양쪽에 그대로 옮겨 두었으므로 종전과 같은 동작).
            SeamTypeKind.Cross => cross4RightWalls?.Contains(wallCode) == true ? RecipeIds.Cross4T2 : RecipeIds.Cross4T1,
            SeamTypeKind.Cross3R0 => RecipeIds.Cross3R0,
            SeamTypeKind.Cross3R90 => RecipeIds.Cross3R90,
            SeamTypeKind.Cross3R180 => RecipeIds.Cross3R180,
            SeamTypeKind.Cross3R270 => RecipeIds.Cross3R270,
            SeamTypeKind.Corner2 => RecipeIds.Corner2,
            SeamTypeKind.Corner => RecipeIds.Corner3,
            _ => null,   // Cross3 tombstone 등 파서 미발행 값
        };
    }

    /// <summary>레시피 id → 티칭 프로필 SeamType 문자열(LINE/CROSS/CROSS3/CORNER2/CORNER3).
    /// 교시 화면이 레시피 선택으로부터 프로필 타입을 유도할 때 쓴다(CROSS4_T1~T4 는 모두 저장값 "CROSS"). 미정의 id 는 null.</summary>
    public static string? ProfileSeamTypeOf(string? recipeId)
    {
        if (recipeId is null || !RecipeIds.All.Contains(recipeId)) return null;
        if (recipeId == RecipeIds.Corner2) return "CORNER2";
        if (recipeId == RecipeIds.Corner3) return "CORNER3";
        if (recipeId == RecipeIds.Cross3R0) return "CROSS3_R0";
        if (recipeId == RecipeIds.Cross3R90) return "CROSS3_R90";
        if (recipeId == RecipeIds.Cross3R180) return "CROSS3_R180";
        if (recipeId == RecipeIds.Cross3R270) return "CROSS3_R270";
        if (recipeId == RecipeIds.Line) return "LINE";
        return "CROSS";   // CROSS4_T1 ~ T4
    }

    /// <summary>챔퍼 면(SL/PL/SU/PU)에서 수직(경사 방향)으로 진행하는지 — Peak pitch 254.6mm 판정
    /// (<see cref="Sequence.Steps.WeldSequenceSupport.IsChamferSlopeProgress"/>)과 같은 기준.</summary>
    public static bool IsChamferSlope(string? wallCode, InspectionMoveDirection direction)
        => direction == InspectionMoveDirection.Vertical
           && WallCodes.Find(wallCode)?.Orientation is SurfaceOrientation.ChamferLower or SurfaceOrientation.ChamferUpper;

    /// <summary>면 목록(wall_code 쉼표 구분) → 정본 10코드만 남긴 집합. null/빈 값은 빈 집합.</summary>
    public static HashSet<string> ParseWallList(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => WallCodes.Find(c) is not null).ToHashSet(StringComparer.Ordinal);

    /// <summary>면 집합 → 저장값(정본 표 순, 쉼표 구분). 비면 null.</summary>
    public static string? SerializeWallList(IEnumerable<string> walls)
    {
        var set = walls.ToHashSet(StringComparer.Ordinal);
        var ordered = WallCodes.All.Where(w => set.Contains(w.Code)).Select(w => w.Code).ToList();
        return ordered.Count == 0 ? null : string.Join(",", ordered);
    }

    /// <summary>타입을 밝히지 않은 구 "CROSS4" 를 T2 로 실행할 면 — CROSS4_T2 레시피의
    /// <see cref="InspectionRecipe.UnspecifiedSideWalls"/>. 레시피가 없거나 비었으면 빈 집합(전부 T1).</summary>
    public static IReadOnlySet<string> Cross4RightWalls(IEnumerable<InspectionRecipe> recipes) =>
        ParseWallList(recipes.FirstOrDefault(r => r.Id == RecipeIds.Cross4T2)?.UnspecifiedSideWalls);

    /// <summary>레시피 id 가 십자 4갈래(T1~T4)인지 — 면별 프로필 지정(<see cref="InspectionRecipe.WallProfilesJson"/>) 대상.</summary>
    public static bool IsCross4(string? recipeId) => recipeId is RecipeIds.Cross4T1 or RecipeIds.Cross4T2 or RecipeIds.Cross4T3 or RecipeIds.Cross4T4;

    /// <summary>면별 프로필 지정(JSON object, 예 {"SM":12,"PM":13}) → 면 코드별 프로필 id. 정본 10코드가 아닌 키와
    /// 숫자가 아닌 값은 버린다. null/빈/깨진 값은 빈 표(= 전부 기본 프로필).</summary>
    public static Dictionary<string, int> ParseWallProfiles(string? json)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (WallCodes.Find(p.Name) is not null && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var id))
                    map[p.Name] = id;
        }
        catch (JsonException) { }
        return map;
    }

    /// <summary>면별 프로필 지정 → 저장값. 지정이 하나도 없으면 null. 면 순서는 정본 표(<see cref="WallCodes.All"/>) 순.</summary>
    public static string? SerializeWallProfiles(IReadOnlyDictionary<string, int> map)
    {
        var ordered = WallCodes.All.Where(w => map.ContainsKey(w.Code)).ToDictionary(w => w.Code, w => map[w.Code]);
        return ordered.Count == 0 ? null : JsonSerializer.Serialize(ordered);
    }

    /// <summary>레시피가 이 프로필을 기본·챔퍼 수직용·면별 지정 중 어느 하나로든 쓰는지.</summary>
    public static bool UsesProfile(InspectionRecipe recipe, int profileId) =>
        recipe.InspectionProfileId == profileId || recipe.ChamferSlopeProfileId == profileId
        || ParseWallProfiles(recipe.WallProfilesJson).ContainsValue(profileId);

    /// <summary>이 task 가 쓸 티칭 프로필 id. LINE 레시피의 챔퍼 수직 진행이면 <see cref="InspectionRecipe.ChamferSlopeProfileId"/>
    /// (경사 방향 254.6mm 경유점), 십자(CROSS4_T1~T4) 레시피에 이 면의 프로필이 따로 지정돼 있으면
    /// (<see cref="InspectionRecipe.WallProfilesJson"/>) 그것, 그 외는 <see cref="InspectionRecipe.InspectionProfileId"/>.
    /// 챔퍼 수직인데 미지정이면 null 을 돌려준다 — 기본 프로필로 대체하지 않는다(360mm 경유점은 경사면에서 엉뚱한 위치).</summary>
    public static (int? ProfileId, bool ChamferSlope) SelectProfileId(
        InspectionRecipe recipe, string? wallCode, InspectionMoveDirection direction)
    {
        var chamferSlope = recipe.Id == RecipeIds.Line && IsChamferSlope(wallCode, direction);
        if (chamferSlope) return (recipe.ChamferSlopeProfileId, true);
        if (IsCross4(recipe.Id) && wallCode is not null
            && ParseWallProfiles(recipe.WallProfilesJson).TryGetValue(wallCode, out var wallProfileId))
            return (wallProfileId, false);
        return (recipe.InspectionProfileId, false);
    }

    /// <summary>CORNER3 좌/우 거울 side 판별 — 코너 스텝의 티칭 슬롯 접두사(corner3.L/R) 선택 키.
    /// wall_code 는 코너에 접한 옆면 코드: P*(좌현) → "L", S*(우현) → "R".
    /// F/A(마구리)·B/T 는 side 규칙 미확정(N13 협의 대상) — 기본 "L" 로 두고 note 에 사유를 남긴다.</summary>
    public static string ResolveCornerSide(string wallCode, out string note)
    {
        if (wallCode.StartsWith('P'))
        {
            note = "좌현(P*) → L";
            return "L";
        }
        if (wallCode.StartsWith('S'))
        {
            note = "우현(S*) → R";
            return "R";
        }
        note = $"wall_code '{wallCode}' 의 side 규칙 미확정(N13) — 기본 L 적용";
        return "L";
    }
}

/// <summary>레시피 id 상수 — INSPECTION_TYPES.md §5 카탈로그와 동일 문자열
/// (LINE + CROSS3 회전 4 + CROSS4 T1~T4 4 + CORNER2 + CORNER3). 면 자세별 변종은 통합되었다(§9-8).</summary>
public static class RecipeIds
{
    public const string Line = "LINE";                   // 직선 1갈래 — 면 자세-독립 단일
    // T자 3갈래 회전 4종 — 면 자세 무관(회전으로만 키잉).
    public const string Cross3R0 = "CROSS3-R0";
    public const string Cross3R90 = "CROSS3-R90";
    public const string Cross3R180 = "CROSS3-R180";
    public const string Cross3R270 = "CROSS3-R270";
    // 십자 4갈래 — 접합 형상에 따른 4타입(ACS seamType CROSS4_T1~T4). 타입마다 티칭 프로필을 따로 지정하고 면별로도 지정할 수 있다.
    // ACS 가 타입 없이 CROSS4 만 보내면 CROSS4_T2 의 면별 기본값 설정으로 T1/T2 를 고른다. 구 CROSS4_L/R 은 T1/T2 로 이어진다.
    public const string Cross4T1 = "CROSS4_T1";
    public const string Cross4T2 = "CROSS4_T2";
    public const string Cross4T3 = "CROSS4_T3";
    public const string Cross4T4 = "CROSS4_T4";
    public const string Corner2 = "CORNER2";
    public const string Corner3 = "CORNER3";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Line,
        Cross3R0, Cross3R90, Cross3R180, Cross3R270,
        Cross4T1, Cross4T2, Cross4T3, Cross4T4,
        Corner2, Corner3,
    };
}
