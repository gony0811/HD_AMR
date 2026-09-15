using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Sequence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// `startWeldInspection` 액션 실행 총괄 (singleton). 파이프라인:
///
///   파싱(<see cref="WeldInspectionActionParser"/>) → 레시피 매핑(<see cref="InspectionRecipeResolver"/>)
///   → scope 생성(scoped 서비스 경계 해소) → 레시피 로드(Enabled 게이트)
///   → sectionDxfId→Drawing→InspectionProfile 조회(사전 티칭 경유점)
///   → SequenceContext 구성(ACS 필드 주입) → SequenceService.RunSequenceAsync
///   → 결과 → FINISHED / FAILED + errorType(orderValidationError·equipmentError·inspectionFailed).
///
/// anchorGroupId 캐시(사양 §8.1): 같은 (orderId, anchorGroupId)의 연속 액션이고 사이에 주행이 없었으면
/// 정렬 스텝군을 생략하고 ⑱ 검사 수행만 실행한다. 무효화: 주행 발생(<see cref="InvalidateAnchor"/>),
/// 시퀀스 실패, 그룹 변경, 신규 order.
/// </summary>
public sealed class WeldInspectionOrchestrator : IWeldInspectionExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CobotService _cobot;
    private readonly VisionInterfaceService _vision;
    private readonly ILogger<WeldInspectionOrchestrator> _logger;

    private readonly object _sync = new();
    private (string OrderId, string AnchorGroupId)? _lastAnchor;   // 마지막 성공 정렬 키
    private CancellationTokenSource? _currentRunCts;

    /// <summary>anchor 캐시 적중 시 실행할 최소 스텝 — ⑱ 검사 수행만(작업물 좌표계 등록은 컨트롤러에 유지됨).</summary>
    private static readonly string[] AnchorHitStepKeys = { "inspectionRun" };

    public WeldInspectionOrchestrator(
        IServiceScopeFactory scopeFactory,
        CobotService cobot,
        VisionInterfaceService vision,
        ILogger<WeldInspectionOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _cobot = cobot;
        _vision = vision;
        _logger = logger;
    }

    public void InvalidateAnchor()
    {
        lock (_sync) _lastAnchor = null;
    }

    public async Task AbortAsync()
    {
        lock (_sync)
        {
            _currentRunCts?.Cancel();
            _lastAnchor = null;
        }
        try
        {
            await _cobot.StopMotionImmediateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "검사 중단 중 코봇 StopMotion 실패");
        }
    }

    public async Task<InspectionActionResult> ExecuteAsync(VdaAction action, string orderId, double? nodeThetaRad, CancellationToken ct)
    {
        try
        {
            return await ExecuteCoreAsync(action, orderId, nodeThetaRad, ct);
        }
        catch (OperationCanceledException)
        {
            throw;   // 임무 폐기/emergencyStop — 상위(Vda5050OrderExecutor)가 정리
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "startWeldInspection 실행 중 미처리 예외 (action={ActionId})", action.ActionId);
            InvalidateAnchor();
            return InspectionActionResult.Fail("inspectionFailed", $"내부 오류: {ex.Message}");
        }
    }

    private async Task<InspectionActionResult> ExecuteCoreAsync(VdaAction action, string orderId, double? nodeThetaRad, CancellationToken ct)
    {
        // 1) 파싱 — 실패는 계약 위반(orderValidationError).
        if (!WeldInspectionActionParser.TryParse(action, out var request, out var parseError))
            return InspectionActionResult.Fail("orderValidationError", $"파라미터 해석 실패: {parseError}");
        var req = request!;

        // 2) 레시피 id 유도 (seamType × wall_code, 사양 §8.5.1).
        if (!InspectionRecipeResolver.TryResolve(req, out var recipeId, out var resolveError))
            return InspectionActionResult.Fail("orderValidationError", $"레시피 매핑 실패: {resolveError}");

        _logger.LogInformation(
            "startWeldInspection 수리: jobRef={JobRef}, seamType={SeamType}, wall={Wall} → recipe={Recipe} " +
            "(dxf={Dxf}, profileId(촬영)={ProfId}, anchor={Anchor}#{Seq})",
            req.JobRef, req.SeamType, req.DrawingPos.WallCode, recipeId,
            req.SectionDxfId, req.InspectionProfileId, req.AnchorGroupId, req.SeqInGroup);

        // 3) 설비 선행 확인 — 코봇/비전 링크 불능이면 equipmentError(설비 자체 불능).
        if (!_cobot.IsConnected)
            return InspectionActionResult.Fail("equipmentError", "코봇 RPC 미연결 — 검사 실행 불가");
        if (!_vision.Client.IsConnected)
            return InspectionActionResult.Fail("equipmentError", "비전 S/W 링크 다운 — 검사 실행 불가");

        // 4) scope 생성 — scoped 서비스(레시피/도면/시퀀스) 사용.
        using var scope = _scopeFactory.CreateScope();
        var recipeService = scope.ServiceProvider.GetRequiredService<InspectionRecipeService>();
        var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
        var sequence = scope.ServiceProvider.GetRequiredService<SequenceService>();

        // 5) 레시피 로드 + Enabled 게이트 — 매핑은 됐지만 실행 미구현이면 inspectionFailed(실행 불가, §8.5.1 (4)).
        var recipe = await recipeService.GetAsync(recipeId!, ct);
        if (recipe is null)
            return InspectionActionResult.Fail("inspectionFailed",
                $"recipe {recipeId} 미등록 — 레시피 시드/DB 확인 필요");
        if (!recipe.Enabled)
            return InspectionActionResult.Fail("inspectionFailed",
                $"recipe {recipeId} resolved but not enabled (실행 미구현 — N13/2차 대기)");

        // 6) 사전 티칭 경유점 조회: sectionDxfId → Drawing(이름 매칭) → 최신 InspectionProfile.
        //    CORNER 는 도면 프로필을 쓰지 않는다(고정 티칭 슬롯 corner3.* 직접 순회) — 조회 생략.
        InspectionProfile? profile = null;
        if (recipe.SeamType != SeamTypeKind.Corner)
        {
            var (found, profileError) = await FindProfileAsync(db, req.SectionDxfId, ct);
            if (found is null)
                return InspectionActionResult.Fail("inspectionFailed", profileError!);
            profile = found;
        }

        // 7) anchor 캐시 판정 — 같은 (orderId, anchorGroupId) 연속이고 주행 없음 → ⑱만 실행.
        //    CORNER 는 정렬 스텝이 없어 캐시 비적용(항상 레시피 스텝 전체 실행, 캐시 갱신도 안 함).
        bool anchorHit;
        lock (_sync)
            anchorHit = recipe.SeamType != SeamTypeKind.Corner
                        && _lastAnchor == (orderId, req.AnchorGroupId);

        var stepKeys = anchorHit
            ? AnchorHitStepKeys
            : ParseStepKeys(recipe.StepKeysJson);

        // 8) 검사 방향 자동 유도(§4.4·§8.1) — seam 벡터를 노드 theta(벽 정면) 기준 벽면-로컬 투영.
        //    theta 미상(방어적)이면 현행 기본 Horizontal 폴백. 판정 근거는 로그로 남긴다.
        var direction = InspectionMoveDirection.Horizontal;
        if (nodeThetaRad is { } theta)
        {
            direction = SeamDirectionResolver.Resolve(req.SeamStartW, req.SeamEndW, theta, out var dirReason);
            _logger.LogInformation("검사 방향 유도: {Reason} (jobRef={JobRef})", dirReason, req.JobRef);
        }
        else
        {
            _logger.LogWarning("노드 theta 미상 — 검사 방향 Horizontal 폴백 (jobRef={JobRef})", req.JobRef);
        }

        // 8.5) CORNER3 좌/우 거울 side 판별 — 티칭 슬롯 접두사(corner3.L/R) 선택 키.
        string? cornerSide = null;
        if (recipe.SeamType == SeamTypeKind.Corner)
        {
            cornerSide = InspectionRecipeResolver.ResolveCornerSide(req.DrawingPos.WallCode, out var sideNote);
            _logger.LogInformation("CORNER3 side={Side} — {Note} (wall={Wall})",
                cornerSide, sideNote, req.DrawingPos.WallCode);
        }

        // 9) SequenceContext 구성 — 파라미터 우선순위: ① ACS action → ② 티칭 프로필 → ③ 레시피 → ④ 전역 기본.
        //    CORNER 는 profile 이 없다 — Tool/Velocity 는 SequenceContext 기본값(단독 실행과 동일).
        var context = new SequenceContext
        {
            InspectionDirection = direction,
            Tool = profile?.RunTool ?? 1,
            Velocity = profile?.RunVel ?? 20,
            InspectionDrawingId = profile?.DrawingId ?? 0,
            InspectionProfileId = profile?.Id ?? 0,
            CornerSide = cornerSide,
            InspectionSurfaceId = WallCodeToSurfaceId(req.DrawingPos.WallCode),
            CameraTargetDistanceMm = req.WorkingDistanceMm
                                     ?? recipe.CameraTargetDistanceMm
                                     ?? 400,
            AcsJobRef = req.JobRef,
            AcsOrderId = orderId,
            AcsActionId = action.ActionId,
            AnchorGroupId = req.AnchorGroupId,
            SeqInGroup = req.SeqInGroup,
            SurfaceOverride = recipe.SurfaceOverride,
            StandoffMmOverride = req.StandoffMm > 0 ? req.StandoffMm : recipe.DefaultStandoffMm,
            VisionFailRatioMax = recipe.VisionFailRatioMax < 1.0 ? recipe.VisionFailRatioMax : null,
        };

        // CROSS4: 레시피 PatternJson → 십자 4-arm 경유점 생성(wobj 프레임, 교차점=원점) → ⑱ 오버라이드 주입.
        // 정렬 체인은 LINE 과 동일하게 1회만 수행되고, anchor 캐시 적중 시에도 오버라이드가 다시 주입되므로
        // ⑱ 단독 재실행 경로가 그대로 동작한다.
        if (recipe.SeamType == SeamTypeKind.Cross)
        {
            if (string.IsNullOrWhiteSpace(recipe.PatternJson))
                return InspectionActionResult.Fail("inspectionFailed",
                    $"recipe {recipeId} 십자 패턴 미구성(PatternJson 없음) — 레시피 관리에서 설정 필요");

            CrossPatternParams? pattern;
            try
            {
                pattern = JsonSerializer.Deserialize<CrossPatternParams>(recipe.PatternJson);
            }
            catch (JsonException ex)
            {
                return InspectionActionResult.Fail("inspectionFailed",
                    $"recipe {recipeId} PatternJson 파싱 실패: {ex.Message}");
            }
            if (pattern is null)
                return InspectionActionResult.Fail("inspectionFailed",
                    $"recipe {recipeId} PatternJson 이 null 로 역직렬화됨");
            if (!CrossPatternGenerator.TryGenerate(pattern, out var crossWaypoints, out var patternError))
                return InspectionActionResult.Fail("inspectionFailed",
                    $"recipe {recipeId} 십자 패턴 생성 실패: {patternError}");

            context.WaypointsOverride = crossWaypoints;
            _logger.LogInformation(
                "CROSS4 십자 경유점 {N}점 생성 (arm={Arm}mm, spacing={Spacing}mm, perpRz={Rz}°)",
                crossWaypoints.Count, pattern.ArmMm, pattern.SpacingMm, pattern.PerpRzDeg);
        }

        _logger.LogInformation(
            "검사 시퀀스 시작: recipe={Recipe}, profile='{Profile}'(id={ProfileId}, drawing={DrawingId}), " +
            "anchor {AnchorState}, steps={Steps}",
            recipeId, profile?.Name ?? "(미사용 — CORNER 티칭 슬롯)", profile?.Id ?? 0, profile?.DrawingId ?? 0,
            anchorHit ? "적중(정렬 생략)" : "신규(풀시퀀스)",
            stepKeys is null ? "(전체)" : string.Join(",", stepKeys));

        // 10) 실행 — 취소 전파용 CTS 를 보관(AbortAsync 가 취소).
        CancellationTokenSource runCts;
        lock (_sync)
        {
            _currentRunCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runCts = _currentRunCts;
        }

        SequenceRunResult runResult;
        try
        {
            runResult = await sequence.RunSequenceAsync(context, stepKeys, runCts.Token);
        }
        finally
        {
            lock (_sync)
            {
                if (_currentRunCts == runCts) _currentRunCts = null;
                runCts.Dispose();
            }
        }

        // 11) 결과 매핑.
        switch (runResult.Outcome)
        {
            case SequenceRunOutcome.Busy:
                return InspectionActionResult.Fail("equipmentError",
                    "onboard sequence busy — UI 등 다른 실행이 장비 점유 중");

            case SequenceRunOutcome.Failed:
                InvalidateAnchor();   // 정렬 신뢰 불가 — 다음 액션은 풀시퀀스
                return InspectionActionResult.Fail("inspectionFailed",
                    $"recipe={recipeId} step={runResult.FailedStepKey ?? "?"} fail: {runResult.Message}");

            case SequenceRunOutcome.Completed:
            default:
                // CORNER 는 정렬을 수행하지 않으므로 anchor 캐시를 갱신하지 않는다(기존 정렬도 훼손 안 함 —
                // wobj 프레임은 컨트롤러에 유지).
                if (recipe.SeamType != SeamTypeKind.Corner)
                    lock (_sync) _lastAnchor = (orderId, req.AnchorGroupId);
                return InspectionActionResult.Ok(
                    $"recipe={recipeId} profile='{profile?.Name ?? $"corner3.{cornerSide}"}' anchor={req.AnchorGroupId}#{req.SeqInGroup}" +
                    (anchorHit ? " (정렬 공유)" : "") +
                    $" jobRef={req.JobRef}");
        }
    }

    /// <summary>sectionDxfId 로 로컬 Drawing 을 찾고(이름 정확 일치 → 파일명 매칭 순), 그 도면의
    /// 최신 티칭설정(InspectionProfile)을 반환. 없으면 (null, 사유).</summary>
    private static async Task<(InspectionProfile? Profile, string? Error)> FindProfileAsync(
        HdAmrDbContext db, string sectionDxfId, CancellationToken ct)
    {
        var drawing = await db.Drawings.AsNoTracking()
                          .FirstOrDefaultAsync(d => d.Name == sectionDxfId, ct)
                      ?? await db.Drawings.AsNoTracking()
                          .FirstOrDefaultAsync(d => d.FileName == sectionDxfId
                                                    || d.FileName == sectionDxfId + ".dxf"
                                                    || d.FileName == sectionDxfId + ".dwg", ct);
        if (drawing is null)
            return (null, $"no local drawing for sectionDxfId='{sectionDxfId}' — 도면 업로드/이름 정합 필요");

        var profile = await db.InspectionProfiles.AsNoTracking()
            .Where(p => p.DrawingId == drawing.Id)
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (profile is null)
            return (null, $"no taught profile for sectionDxfId='{sectionDxfId}' (drawing '{drawing.Name}') — 온보드 티칭 필요");

        return (profile, null);
    }

    /// <summary>StepKeysJson(문자열 배열) → 스텝 키 목록. null/빈/파싱 실패 → null(풀시퀀스).</summary>
    private string[]? ParseStepKeys(string? stepKeysJson)
    {
        if (string.IsNullOrWhiteSpace(stepKeysJson)) return null;
        try
        {
            var keys = JsonSerializer.Deserialize<string[]>(stepKeysJson);
            return keys is { Length: > 0 } ? keys : null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "레시피 StepKeysJson 파싱 실패 — 풀시퀀스로 실행: {Json}", stepKeysJson);
            return null;
        }
    }

    /// <summary>wall_code → 비전 Surface ID (vision_interface.md §5, 0x01~0x0A).
    /// S*=우현(Starboard), P*=좌현(Port). 미정의 코드는 resolver 가 먼저 거른다 — 방어적 기본 0x01.</summary>
    private static int WallCodeToSurfaceId(string wallCode) => wallCode switch
    {
        "B" => 0x01,    // 바닥 (Bottom)
        "T" => 0x02,    // 천장 (Top)
        "PM" => 0x03,   // 좌현벽 (Port)
        "SM" => 0x04,   // 우현벽 (Starboard)
        "F" => 0x05,    // 전벽 (Forward)
        "A" => 0x06,    // 후벽 (Aft)
        "PL" => 0x07,   // 하부 좌현 챔퍼
        "SL" => 0x08,   // 하부 우현 챔퍼
        "PU" => 0x09,   // 상부 좌현 챔퍼
        "SU" => 0x0A,   // 상부 우현 챔퍼
        _ => 0x01,
    };
}
