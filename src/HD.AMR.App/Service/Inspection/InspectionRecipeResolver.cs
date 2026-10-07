using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 검사 레시피 매핑(사양 §8.5.1, INSPECTION_TYPES.md §5) — 순수 함수 2단:
/// ① `wall_code` → 면 자세(유효성 확인용), ② `seamType` → 레시피 id 8종.
///
/// 정본 10코드(B/T/SM/PM/F/A/SL/PL/SU/PU, ACS 확정 2026-09-15) 외 wall_code 는 계약 위반으로
/// 실패를 반환한다 — 호출측이 액션 FAILED + orderValidationError 로 보고.
/// <b>면 자세는 레시피 선택에 쓰지 않는다</b> — LINE/CROSS4/CROSS3(회전4)/CORNER 모두 면 자세-독립 단일(또는 회전별)
/// 레시피다(INSPECTION_TYPES.md §9-8). 접근 자세·법선·방향은 런타임이 `wall_code`로 직접 계산하고
/// (<see cref="SeamBaseTransform"/>), 면별 경유점 차이는 레시피에 바인딩된 티칭 프로파일이 흡수한다.
/// CORNER2/CORNER3 은 삼면 코너 각도 전부 135°·90°·90° 균일(INSPECTION_TYPES.md §7). 거울 L/R 분리(§9-4)는 N13 확정 시 wall_code 판별 추가.
/// </summary>
public static class InspectionRecipeResolver
{
    /// <summary>wall_code(10코드 정본, <see cref="WallCodes"/>) → 면 자세. 미정의 코드는 null.</summary>
    public static SurfaceOrientation? ResolveOrientation(string wallCode) => WallCodes.Find(wallCode)?.Orientation;

    /// <summary>레시피 id 유도. 실패 시 error 에 사유(계약 위반 — orderValidationError 계열).</summary>
    public static bool TryResolve(WeldInspectionRequest request, out string? recipeId, out string? error)
    {
        recipeId = ResolveRecipeId(request.SeamType, request.DrawingPos.WallCode);
        error = recipeId is null
            ? $"미정의 wall_code '{request.DrawingPos.WallCode}' — 정본 10코드(B/T/SM/PM/F/A/SL/PL/SU/PU)만 수용 (§8.5.1)"
            : null;
        return recipeId is not null;
    }

    /// <summary>seamType → 레시피 id (§8.5.1 매핑표). wall_code 는 유효성(정본 10코드) 확인에만 쓰고
    /// 레시피 선택에는 관여하지 않는다 — 미정의 wall_code 는 null(계약 위반).
    /// UI 매핑 레퍼런스 등 request 없이 조회할 때 쓰는 경량 경로 — <see cref="TryResolve"/> 가 위임한다.</summary>
    public static string? ResolveRecipeId(SeamTypeKind seamType, string wallCode)
    {
        // 면 자세는 레시피 선택에 쓰지 않는다 — 모든 seamType 이 면 자세-독립 단일(또는 회전별) 레시피다.
        // wall_code 는 여전히 정의 코드여야 한다(접근 자세·방향은 런타임이 wall_code 로 직접 계산).
        if (ResolveOrientation(wallCode) is null) return null;
        return seamType switch
        {
            SeamTypeKind.Line => RecipeIds.Line,
            SeamTypeKind.Cross => RecipeIds.Cross4,
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
    /// 교시 화면이 레시피 선택으로부터 프로필 타입을 유도할 때 쓴다(CROSS4 는 저장값 "CROSS"). 미정의 id 는 null.</summary>
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
        return "CROSS";   // CROSS4
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

/// <summary>레시피 id 상수 — INSPECTION_TYPES.md §5 카탈로그 8종과 동일 문자열
/// (LINE + CROSS3 회전 4 + CROSS4 + CORNER2 + CORNER3). 면 자세별 변종은 통합되었다(§9-8).</summary>
public static class RecipeIds
{
    public const string Line = "LINE";                   // 직선 1갈래 — 면 자세-독립 단일
    // T자 3갈래 회전 4종 — 면 자세 무관(회전으로만 키잉).
    public const string Cross3R0 = "CROSS3-R0";
    public const string Cross3R90 = "CROSS3-R90";
    public const string Cross3R180 = "CROSS3-R180";
    public const string Cross3R270 = "CROSS3-R270";
    public const string Cross4 = "CROSS4";               // 십자 4갈래 — 면 자세-독립 단일
    public const string Corner2 = "CORNER2";
    public const string Corner3 = "CORNER3";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Line,
        Cross3R0, Cross3R90, Cross3R180, Cross3R270,
        Cross4,
        Corner2, Corner3,
    };
}
