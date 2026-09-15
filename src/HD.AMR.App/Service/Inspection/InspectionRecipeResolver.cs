namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 검사 레시피 매핑(사양 §8.5.1, INSPECTION_TYPES.md §5) — 순수 함수 2단:
/// ① `wall_code` → 면 자세 5군, ② `(seamType, 면 자세)` → 레시피 id 16종.
///
/// 정본 10코드(B/T/SM/PM/F/A/SL/PL/SU/PU, ACS 확정 2026-09-15) 외 wall_code 는 계약 위반으로
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
        recipeId = ResolveRecipeId(request.SeamType, request.DrawingPos.WallCode);
        error = recipeId is null
            ? $"미정의 wall_code '{request.DrawingPos.WallCode}' — 정본 10코드(B/T/SM/PM/F/A/SL/PL/SU/PU)만 수용 (§8.5.1)"
            : null;
        return recipeId is not null;
    }

    /// <summary>(seamType × wall_code) → 레시피 id (§8.5.1 매핑표). 미정의 wall_code 는 null.
    /// UI 매핑 레퍼런스 등 request 없이 조회할 때 쓰는 경량 경로 — <see cref="TryResolve"/> 가 위임한다.</summary>
    public static string? ResolveRecipeId(SeamTypeKind seamType, string wallCode)
    {
        // CORNER: 면 자세 무관 단일 레시피(§8.5.1 (3)) — wall_code 는 여전히 정의 코드여야 한다.
        var orientation = ResolveOrientation(wallCode);
        if (orientation is null) return null;
        if (seamType == SeamTypeKind.Corner) return RecipeIds.Corner3;

        var prefix = seamType switch
        {
            SeamTypeKind.Line => "LINE",
            SeamTypeKind.Cross3 => "CROSS3",   // 3갈래 T자
            _ => "CROSS4",                       // Cross(4갈래)
        };
        return orientation switch
        {
            SurfaceOrientation.Floor => $"{prefix}-FLOOR",
            SurfaceOrientation.Ceiling => $"{prefix}-CEIL",
            SurfaceOrientation.Wall => $"{prefix}-WALL",
            SurfaceOrientation.ChamferLower => $"{prefix}-CHMR-LO",
            SurfaceOrientation.ChamferUpper => $"{prefix}-CHMR-UP",
            _ => null,
        };
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

/// <summary>레시피 id 상수 — INSPECTION_TYPES.md §5 카탈로그 16종과 동일 문자열.</summary>
public static class RecipeIds
{
    public const string LineFloor = "LINE-FLOOR";
    public const string LineCeil = "LINE-CEIL";
    public const string LineWall = "LINE-WALL";
    public const string LineChamferLower = "LINE-CHMR-LO";
    public const string LineChamferUpper = "LINE-CHMR-UP";
    public const string Cross3Floor = "CROSS3-FLOOR";
    public const string Cross3Ceil = "CROSS3-CEIL";
    public const string Cross3Wall = "CROSS3-WALL";
    public const string Cross3ChamferLower = "CROSS3-CHMR-LO";
    public const string Cross3ChamferUpper = "CROSS3-CHMR-UP";
    public const string Cross4Floor = "CROSS4-FLOOR";
    public const string Cross4Ceil = "CROSS4-CEIL";
    public const string Cross4Wall = "CROSS4-WALL";
    public const string Cross4ChamferLower = "CROSS4-CHMR-LO";
    public const string Cross4ChamferUpper = "CROSS4-CHMR-UP";
    public const string Corner3 = "CORNER3";

    public static readonly IReadOnlyList<string> All = new[]
    {
        LineFloor, LineCeil, LineWall, LineChamferLower, LineChamferUpper,
        Cross3Floor, Cross3Ceil, Cross3Wall, Cross3ChamferLower, Cross3ChamferUpper,
        Cross4Floor, Cross4Ceil, Cross4Wall, Cross4ChamferLower, Cross4ChamferUpper,
        Corner3,
    };
}
