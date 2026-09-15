using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Inspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 검사 레시피(<see cref="InspectionRecipe"/>, 11종 카탈로그) CRUD + 기동 시드.
/// 시드는 upsert-if-missing — 기본값은 코드(git)로 버전 관리하되, 이미 존재하는 행(현장 조정값)은
/// 건드리지 않는다. LINE-* 5종만 Enabled=true (CROSS4-*/CORNER3 은 실행 미구현 — Phase 2/3).
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
    /// 시드가 upsert-if-missing 이라 기본값 변경이 기존 DB 에 반영되지 않을 때 쓰는 명시적 경로(레시피 관리 UI).</summary>
    public async Task<bool> ResetToDefaultAsync(string id, CancellationToken ct = default)
    {
        var seed = BuildDefaults().FirstOrDefault(r => r.Id == id);
        if (seed is null) return false;
        await SaveAsync(seed, ct);
        _logger.LogInformation("검사 레시피 {Id} 기본값 재적용", id);
        return true;
    }

    /// <summary>기동 시드 — 카탈로그 11종 중 없는 행만 추가(현장 수정값 보존).</summary>
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

    /// <summary>카탈로그 11종 기본값 (INSPECTION_TYPES.md §5 / 사양 §8.5.1).</summary>
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
            (RecipeIds.Cross4Floor, "4점 십자 — 바닥(B)", SeamTypeKind.Cross, SurfaceOrientation.Floor, false),
            (RecipeIds.Cross4Ceil, "4점 십자 — 천장(T)", SeamTypeKind.Cross, SurfaceOrientation.Ceiling, false),
            (RecipeIds.Cross4Wall, "4점 십자 — 수직벽(SM/PM/F/A)", SeamTypeKind.Cross, SurfaceOrientation.Wall, false),
            (RecipeIds.Cross4ChamferLower, "4점 십자 — 하부챔퍼(SL/PL)", SeamTypeKind.Cross, SurfaceOrientation.ChamferLower, false),
            (RecipeIds.Cross4ChamferUpper, "4점 십자 — 상부챔퍼(SU/PU)", SeamTypeKind.Cross, SurfaceOrientation.ChamferUpper, false),
            (RecipeIds.Corner3, "3점 코너 (삼면, 135°·90°·90°)", SeamTypeKind.Corner, SurfaceOrientation.Any, false),
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
                // 그 외는 풀시퀀스(null) — 타입별 부분 구성은 현장 튜닝으로 조정.
                StepKeysJson = c.Seam == SeamTypeKind.Corner
                    ? """["amrMove","cornerInspectionRun","wObjReset","monitorClose"]"""
                    : null,
                ApproachTeachingKey = "",
                DefaultStandoffMm = 400,
                CameraTargetDistanceMm = null,     // 전역 기본(400mm) 사용
                SurfaceOverride = c.Id == RecipeIds.Corner3 ? (byte)1 : null,   // CORNER3 = Corner 고정
                AlignRetryCount = 0,
                VisionFailRatioMax = 1.0,          // 판정 안 함 — 정책 확정 시 하향
                // CROSS4: 십자 4-arm 경유점 생성 파라미터(CrossPatternParams) — 교차점 ±180mm, 30mm 간격,
                // 교차 arm 촬상 회전 −90°. 현장 튜닝은 레시피 관리 UI 에서.
                PatternJson = c.Seam == SeamTypeKind.Cross
                    ? """{"ArmMm":180,"SpacingMm":30,"PerpRzDeg":-90}"""
                    : null,
            };
        }
    }
}
