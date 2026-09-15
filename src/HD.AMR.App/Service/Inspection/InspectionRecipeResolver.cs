namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 검사 레시피 매핑(사양 §8.5.1, INSPECTION_TYPES.md §5) — 순수 함수 2단:
/// ① `wall_code` → 면 자세 5군, ② `(seamType, 면 자세)` → 레시피 id 11종.
///
/// 미정의 wall_code(예: 골든 예시의 "W03" — §8.4↔§8.5.1 불일치, ACS 협의 대기)는 계약 위반으로
/// 실패를 반환한다 — 호출측이 액션 FAILED + orderValidationError 로 보고.
/// CORNER 는 면 자세 무관 단일 `CORNER3`(삼면 코너 각도 전부 135°·90°·90° 균일 — INSPECTION_TYPES.md §7).
/// 거울 L/R 분리(§9-4)는 N13 확정 시 wall_code 판별 추가.
/// </summary>
public static class InspectionRecipeResolver
{
    /// <summary>wall_code(10코드 정본: B/T/SM/PM/F/A/SL/PL/SU/PU) → 면 자세. 미정의 코드는 null.</summary>
    public static SurfaceOrientation? ResolveOrientation(string wallCode) => wallCode switch
    {
        "B" => SurfaceOrientation.Floor,
        "T" => SurfaceOrientation.Ceiling,
        "SM" or "PM" or "F" or "A" => SurfaceOrientation.Wall,
        "SL" or "PL" => SurfaceOrientation.ChamferLower,
        "SU" or "PU" => SurfaceOrientation.ChamferUpper,
        _ => null,
    };

    /// <summary>레시피 id 유도. 실패 시 error 에 사유(계약 위반 — orderValidationError 계열).</summary>
    public static bool TryResolve(WeldInspectionRequest request, out string? recipeId, out string? error)
    {
        recipeId = null;
        error = null;

        // CORNER: 면 자세 무관 단일 레시피(§8.5.1 (3)) — wall_code 는 여전히 정의 코드여야 한다.
        var orientation = ResolveOrientation(request.DrawingPos.WallCode);
        if (orientation is null)
        {
            error = $"미정의 wall_code '{request.DrawingPos.WallCode}' — 정본 10코드(B/T/SM/PM/F/A/SL/PL/SU/PU)만 수용 (§8.5.1)";
            return false;
        }

        if (request.SeamType == SeamTypeKind.Corner)
        {
            recipeId = RecipeIds.Corner3;
            return true;
        }

        var prefix = request.SeamType == SeamTypeKind.Line ? "LINE" : "CROSS4";
        recipeId = orientation switch
        {
            SurfaceOrientation.Floor => $"{prefix}-FLOOR",
            SurfaceOrientation.Ceiling => $"{prefix}-CEIL",
            SurfaceOrientation.Wall => $"{prefix}-WALL",
            SurfaceOrientation.ChamferLower => $"{prefix}-CHMR-LO",
            SurfaceOrientation.ChamferUpper => $"{prefix}-CHMR-UP",
            _ => null,
        };
        return recipeId is not null;
    }
}

/// <summary>레시피 id 상수 — INSPECTION_TYPES.md §5 카탈로그 11종과 동일 문자열.</summary>
public static class RecipeIds
{
    public const string LineFloor = "LINE-FLOOR";
    public const string LineCeil = "LINE-CEIL";
    public const string LineWall = "LINE-WALL";
    public const string LineChamferLower = "LINE-CHMR-LO";
    public const string LineChamferUpper = "LINE-CHMR-UP";
    public const string Cross4Floor = "CROSS4-FLOOR";
    public const string Cross4Ceil = "CROSS4-CEIL";
    public const string Cross4Wall = "CROSS4-WALL";
    public const string Cross4ChamferLower = "CROSS4-CHMR-LO";
    public const string Cross4ChamferUpper = "CROSS4-CHMR-UP";
    public const string Corner3 = "CORNER3";

    public static readonly IReadOnlyList<string> All = new[]
    {
        LineFloor, LineCeil, LineWall, LineChamferLower, LineChamferUpper,
        Cross4Floor, Cross4Ceil, Cross4Wall, Cross4ChamferLower, Cross4ChamferUpper,
        Corner3,
    };
}
