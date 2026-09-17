using System.Text.Json;
using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Inspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 검사 매핑 요약 뷰(레시피 관리 UI)용 읽기 모델 — 티칭 프로필 1건의 상태.
/// 실기 액션은 레시피에 지정된 프로필(<see cref="InspectionRecipe.InspectionProfileId"/>)의 경유점을 쓴다 —
/// <see cref="ProfileId"/> 로 레시피 지정과 대조한다. DrawingId/DrawingName 은 구 도면 기반 교시의 잔여 연결(없으면 0/"—").
/// </summary>
public record DrawingTeachingSummary(
    int DrawingId,
    string DrawingName,
    string FileName,
    bool HasProfile,
    string? ProfileName,
    string? SeamType,
    int WaypointCount,
    DateTime? TaughtAt,
    int ProfileId = 0);

/// <summary>
/// 검사 레시피(<see cref="InspectionRecipe"/>, 17종 카탈로그) CRUD + 기동 시드.
/// 시드는 upsert-if-missing — 기본값은 코드(git)로 버전 관리하되, 이미 존재하는 행(현장 조정값)은
/// 건드리지 않는다. LINE-* 5종만 Enabled=true (CROSS3-*/CROSS4-*/CORNER2/CORNER3 은 실행 게이트 OFF — N13/캡처 교시 대기).
/// </summary>
public class InspectionRecipeService
{
    private readonly HdAmrDbContext _db;
    private readonly ILogger<InspectionRecipeService> _logger;

    public InspectionRecipeService(HdAmrDbContext db, ILogger<InspectionRecipeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<List<InspectionRecipe>> ListAsync(CancellationToken ct = default) =>
        _db.InspectionRecipes.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);

    public Task<InspectionRecipe?> GetAsync(string id, CancellationToken ct = default) =>
        _db.InspectionRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>티칭 프로필 요약(검사 매핑 요약 뷰용) — 프로필 전체를 최신 갱신 순으로 1행씩.
    /// 어떤 레시피가 사용하는지는 호출측이 레시피 목록의 <see cref="InspectionRecipe.InspectionProfileId"/> 로 대조한다.
    /// 경유점 수는 <see cref="InspectionProfile.WaypointsJson"/> 배열 길이로 센다(파싱 실패 시 0).</summary>
    public async Task<List<DrawingTeachingSummary>> ListTeachingSummaryAsync(CancellationToken ct = default)
    {
        var profiles = await _db.InspectionProfiles.AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new
            {
                p.Id, p.DrawingId, p.Name, p.SeamType, p.WaypointsJson, p.UpdatedAt,
                DrawingName = p.Drawing != null ? p.Drawing.Name : null,
                DrawingFileName = p.Drawing != null ? p.Drawing.FileName : null,
            })
            .ToListAsync(ct);

        return profiles.Select(p => new DrawingTeachingSummary(
                p.DrawingId ?? 0,
                p.DrawingName ?? "—",
                p.DrawingFileName ?? "—",
                HasProfile: true,
                ProfileName: p.Name,
                SeamType: p.SeamType,
                WaypointCount: CountWaypoints(p.WaypointsJson),
                TaughtAt: p.UpdatedAt,
                ProfileId: p.Id))
            .ToList();
    }

    /// <summary>WaypointsJson(배열) 원소 수. null/빈/비배열/파싱 실패는 0.</summary>
    private static int CountWaypoints(string? waypointsJson)
    {
        if (string.IsNullOrWhiteSpace(waypointsJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(waypointsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public async Task SaveAsync(InspectionRecipe recipe, CancellationToken ct = default)
    {
        var existing = await _db.InspectionRecipes.FirstOrDefaultAsync(r => r.Id == recipe.Id, ct);
        if (existing is null)
        {
            recipe.CreatedAt = DateTime.UtcNow;
            recipe.UpdatedAt = recipe.CreatedAt;
            _db.InspectionRecipes.Add(recipe);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(recipe);
            existing.CreatedAt = existing.CreatedAt == default ? DateTime.UtcNow : existing.CreatedAt;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>레시피 1건을 코드 기본값(<see cref="BuildDefaults"/>)으로 재적용 — 기존 행이 있어도 덮어쓴다.
    /// 시드가 upsert-if-missing 이라 기본값 변경이 기존 DB 에 반영되지 않을 때 쓰는 명시적 경로(레시피 관리 UI).
    /// 티칭 프로필 지정(<see cref="InspectionRecipe.InspectionProfileId"/>)은 코드 기본값이 아닌 현장 데이터라 보존한다.</summary>
    public async Task<bool> ResetToDefaultAsync(string id, CancellationToken ct = default)
    {
        var seed = BuildDefaults().FirstOrDefault(r => r.Id == id);
        if (seed is null) return false;
        seed.InspectionProfileId = await _db.InspectionRecipes.AsNoTracking()
            .Where(r => r.Id == id).Select(r => r.InspectionProfileId).FirstOrDefaultAsync(ct);
        await SaveAsync(seed, ct);
        _logger.LogInformation("검사 레시피 {Id} 기본값 재적용", id);
        return true;
    }

    /// <summary>레시피에 지정할 티칭 프로필 검증 — 저장 전 UI 가 호출. 문제 없으면 null, 있으면 사유.
    /// 규칙: CORNER3 은 지정 불가(고정 슬롯 사용), 프로필 존재, 프로필 SeamType = 레시피 타입, 경유점 2점 이상.</summary>
    public async Task<string?> ValidateProfileAssignmentAsync(InspectionRecipe recipe, CancellationToken ct = default)
    {
        if (recipe.InspectionProfileId is not { } profileId) return null;
        if (recipe.Id == RecipeIds.Corner3)
            return "CORNER3 는 고정 티칭 슬롯(corner3.*)을 사용하므로 티칭 프로필을 지정할 수 없습니다.";

        var profile = await _db.InspectionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct);
        if (profile is null)
            return $"티칭 프로필(id={profileId})을 찾을 수 없습니다.";

        var want = InspectionRecipeResolver.ProfileSeamTypeOf(recipe.Id);
        if (!IsSeamMatch(profile, want))
            return $"프로필 '{profile.Name}' 타입({profile.SeamType})이 레시피 {recipe.Id} 타입({want})과 다릅니다.";
        if (CountWaypoints(profile.WaypointsJson) < 2)
            return $"프로필 '{profile.Name}' 경유점이 2점 미만입니다 — 실행 시 실패합니다.";
        return null;
    }

    /// <summary>프로필 SeamType 이 레시피 요구 타입과 같은지(대소문자 무시, 빈 값은 LINE 취급).</summary>
    public static bool IsSeamMatch(InspectionProfile profile, string? wantSeamType)
    {
        var seam = string.IsNullOrWhiteSpace(profile.SeamType) ? "LINE" : profile.SeamType;
        return wantSeamType is not null && string.Equals(seam, wantSeamType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>기동 시드 — 카탈로그 17종 중 없는 행만 추가(현장 수정값 보존).</summary>
    public async Task SeedDefaultsAsync(CancellationToken ct = default)
    {
        var existingIds = await _db.InspectionRecipes.Select(r => r.Id).ToListAsync(ct);
        var added = 0;

        foreach (var seed in BuildDefaults())
        {
            if (existingIds.Contains(seed.Id)) continue;
            seed.CreatedAt = DateTime.UtcNow;
            seed.UpdatedAt = seed.CreatedAt;
            _db.InspectionRecipes.Add(seed);
            added++;
        }

        if (added > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("검사 레시피 시드: {Added}종 추가 (총 {Total}종 카탈로그)", added, RecipeIds.All.Count);
        }
    }

    /// <summary>카탈로그 17종 기본값 (INSPECTION_TYPES.md §5 / 사양 §8.5.1).</summary>
    private static IEnumerable<InspectionRecipe> BuildDefaults()
    {
        // (id, 표시명, seamType, 면자세, 실행 가능 여부)
        var catalog = new (string Id, string Name, SeamTypeKind Seam, SurfaceOrientation Orient, bool Enabled)[]
        {
            (RecipeIds.LineFloor, "직선 seam — 바닥(B)", SeamTypeKind.Line, SurfaceOrientation.Floor, true),
            (RecipeIds.LineCeil, "직선 seam — 천장(T)", SeamTypeKind.Line, SurfaceOrientation.Ceiling, true),
            (RecipeIds.LineWall, "직선 seam — 수직벽(SM/PM/F/A)", SeamTypeKind.Line, SurfaceOrientation.Wall, true),
            (RecipeIds.LineChamferLower, "직선 seam — 하부챔퍼(SL/PL)", SeamTypeKind.Line, SurfaceOrientation.ChamferLower, true),
            (RecipeIds.LineChamferUpper, "직선 seam — 상부챔퍼(SU/PU)", SeamTypeKind.Line, SurfaceOrientation.ChamferUpper, true),
            (RecipeIds.Cross3Floor, "T자 3갈래 — 바닥(B)", SeamTypeKind.Cross3, SurfaceOrientation.Floor, false),
            (RecipeIds.Cross3Ceil, "T자 3갈래 — 천장(T)", SeamTypeKind.Cross3, SurfaceOrientation.Ceiling, false),
            (RecipeIds.Cross3Wall, "T자 3갈래 — 수직벽(SM/PM/F/A)", SeamTypeKind.Cross3, SurfaceOrientation.Wall, false),
            (RecipeIds.Cross3ChamferLower, "T자 3갈래 — 하부챔퍼(SL/PL)", SeamTypeKind.Cross3, SurfaceOrientation.ChamferLower, false),
            (RecipeIds.Cross3ChamferUpper, "T자 3갈래 — 상부챔퍼(SU/PU)", SeamTypeKind.Cross3, SurfaceOrientation.ChamferUpper, false),
            (RecipeIds.Cross4Floor, "4점 십자 — 바닥(B)", SeamTypeKind.Cross, SurfaceOrientation.Floor, false),
            (RecipeIds.Cross4Ceil, "4점 십자 — 천장(T)", SeamTypeKind.Cross, SurfaceOrientation.Ceiling, false),
            (RecipeIds.Cross4Wall, "4점 십자 — 수직벽(SM/PM/F/A)", SeamTypeKind.Cross, SurfaceOrientation.Wall, false),
            (RecipeIds.Cross4ChamferLower, "4점 십자 — 하부챔퍼(SL/PL)", SeamTypeKind.Cross, SurfaceOrientation.ChamferLower, false),
            (RecipeIds.Cross4ChamferUpper, "4점 십자 — 상부챔퍼(SU/PU)", SeamTypeKind.Cross, SurfaceOrientation.ChamferUpper, false),
            (RecipeIds.Corner2, "2면 코너 (이면, 브릿지 플레이트)", SeamTypeKind.Corner2, SurfaceOrientation.Any, false),
            (RecipeIds.Corner3, "3면 코너 (삼면, 135°·90°·90°, 브릿지 플레이트)", SeamTypeKind.Corner, SurfaceOrientation.Any, false),
        };

        foreach (var c in catalog)
        {
            yield return new InspectionRecipe
            {
                Id = c.Id,
                DisplayName = c.Name,
                SeamType = c.Seam,
                Orientation = c.Orient,
                Enabled = c.Enabled,
                // CORNER3 은 평탄면 정렬 체인 미적용 — 티칭 슬롯(corner3.*) 직접 순회 스텝만 실행.
                // CORNER2 는 실행 스텝 미구현(Enabled=false 로 게이트) — 후속에서 corner2 슬롯/캡처 스텝 배선.
                // 그 외는 풀시퀀스(null) — 타입별 부분 구성은 현장 튜닝으로 조정.
                StepKeysJson = c.Seam == SeamTypeKind.Corner
                    ? """["amrMove","cornerInspectionRun","wobjReset","monitorClose"]"""
                    : null,
                CameraTargetDistanceMm = null,     // 전역 기본(400mm) 사용
                VisionFailRatioMax = 1.0,          // 판정 안 함 — 정책 확정 시 하향
                // (PatternJson 제거) CROSS3/CROSS4 는 /inspection-points 6-DOF 캡처 프로필 경유점을 실행 —
                // 십자 패턴 런타임 생성은 폐기(캡처 교시 단일화).
            };
        }
    }
}
