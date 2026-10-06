using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// `startWeldInspection` 액션 실행 총괄 (singleton). 파이프라인:
///
///   파싱(<see cref="WeldInspectionActionParser"/>) → 레시피 매핑(<see cref="InspectionRecipeResolver"/>)
///   → scope 생성(scoped 서비스 경계 해소) → 레시피 로드(Enabled 게이트)
///   → 레시피에 지정된 InspectionProfile 조회(사전 티칭 경유점)
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
    private (string OrderId, string AnchorGroupId, InspectionMoveDirection Direction)? _lastAnchor;
    private double[]? _lastInternalWorkpieceFrame;                 // 같은 anchor에서 재사용할 BASE 기준 내부 프레임
    private CancellationTokenSource? _currentRunCts;

    /// <summary>
    /// <b>작업물 좌표계 교시 스텝군(⑤~⑯)</b> — anchor 적중(같은 노드의 2번째 이후 task) 시 건너뛴다.
    ///
    /// 첫 task 가 여기서 계산한 내부 작업물 좌표계(T_BW)를 오케스트레이터가 캐시하고 이후 task에 복사한다.
    /// 컨트롤러 작업물 좌표계에는 등록하지 않는다.
    ///
    /// <b>②③④는 건너뛰지 않는다</b> — task 마다 용접선(seamStartW)이 다르므로 ②가 그 위치로 다시
    /// 이동해야 하고, 위치가 바뀐 이상 ③ 카메라 거리·④ 평탄면 센터링도 그 자리에서 다시 잡아야 한다.
    /// </summary>
    private static readonly HashSet<string> AlignmentStepKeys = new(StringComparer.Ordinal)
    {
        "peak1Find", "peak1Center", "bead1Find", "bead1Center", "wobjPoint1",
        "peak2Approach", "peak2Find", "peak2Center", "bead2Find", "bead2Center", "wobjPoint2",
        "wobjRegister",
        // 드라이런 2점 교시의 끝점 이동 — anchor 적중 시 캐시 프레임 재사용으로 재교시 생략.
        "cobotSeamEnd",
    };

    /// <summary>코봇 홈 복귀 스텝 — 노드의 <b>마지막</b> 검사 액션에서만 실행한다.</summary>
    private const string HomeStepKey = "cobotHome";

    /// <summary>드라이런 레시피(<see cref="Data.Entities.InspectionRecipe.DryRun"/>=true)가 돌릴 스텝 집합.
    /// 비전 정렬(③~⑯) 없이 ACS 용접선 시작/끝점으로 2점 교시(wobjPoint1→cobotSeamEnd→wobjPoint2)만 해
    /// 내부 작업물 좌표계를 잡고(wobjRegister), 경유점을 이동만 순회(inspectionRunDry)한 뒤 반납·홈 복귀한다.
    /// 순서는 각 스텝 DefaultOrder 가 정하므로(SequenceService) 배열 순서는 무관하다. StepKeysJson 은 무시된다.</summary>
    private static readonly string[] DryRunStepKeys =
    {
        "cobotInspection", "wobjPoint1", "cobotSeamEnd", "wobjPoint2",
        "wobjRegister", "inspectionRunDry", "wobjReset", "cobotHome", "monitorClose",
    };

    /// <summary>드라이런에서만 의미 있는 스텝 — 일반 레시피가 풀시퀀스(StepKeysJson 미지정)로 돌 때 제외한다.
    /// cobotSeamEnd 가 섞이면 bead2Center 직후 끝점으로 이동해 wobjPoint2 가 비전 Bead2 대신 그 위치를 기록한다.</summary>
    private static readonly HashSet<string> DryRunOnlyStepKeys = new(StringComparer.Ordinal)
    {
        "cobotSeamEnd", "inspectionRunDry",
    };

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
        lock (_sync)
        {
            _lastAnchor = null;
            _lastInternalWorkpieceFrame = null;
        }
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

    public async Task<InspectionActionResult> ExecuteAsync(VdaAction action, string orderId, double? nodeThetaRad,
                                                           bool isLastInspection, CancellationToken ct)
    {
        try
        {
            return await ExecuteCoreAsync(action, orderId, nodeThetaRad, isLastInspection, ct);
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

    private async Task<InspectionActionResult> ExecuteCoreAsync(VdaAction action, string orderId, double? nodeThetaRad,
                                                                bool isLastInspection, CancellationToken ct)
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
            "(dxf={Dxf}, profileId(촬영)={ProfId}, anchor={Anchor}#{Seq}, taskId={TaskId}, attempt={Attempt})",
            req.JobRef, req.SeamType, req.DrawingPos.WallCode, recipeId,
            req.SectionDxfId, req.InspectionProfileId, req.AnchorGroupId, req.SeqInGroup,
            req.TaskId?.ToString() ?? "(미수신)", req.Attempt?.ToString() ?? "(미수신→1)");

        // taskId 미탑재는 계약상 허용(선택 필드)이지만 비전 측 이력 누적 키가 없다는 뜻이라 흔적을 남긴다.
        // 형식 오류는 파서가 이미 orderValidationError 로 거부했으므로 여기 도달하지 않는다.
        if (req.TaskId is null)
            _logger.LogWarning(
                "ACS 액션에 taskId 없음 — 비전에 Guid.Empty 로 전송됩니다(SAIGE 이력 누적 불가, jobRef={JobRef})",
                req.JobRef);

        // 3) 설비 선행 확인 — 코봇 링크 불능이면 equipmentError(설비 자체 불능).
        //    비전 링크는 레시피 로드 후 드라이런이 아닐 때만 본다(5단계 뒤).
        if (!_cobot.IsConnected)
            return InspectionActionResult.Fail("equipmentError", "코봇 RPC 미연결 — 검사 실행 불가");

        // 4) scope 생성 — scoped 서비스(레시피/도면/시퀀스) 사용.
        using var scope = _scopeFactory.CreateScope();
        var recipeService = scope.ServiceProvider.GetRequiredService<InspectionRecipeService>();
        var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
        var sequence = scope.ServiceProvider.GetRequiredService<SequenceService>();
        var param = scope.ServiceProvider.GetRequiredService<ParameterService>();

        // 5) 레시피 로드 + Enabled 게이트 — 매핑은 됐지만 실행 미구현이면 inspectionFailed(실행 불가, §8.5.1 (4)).
        var recipe = await recipeService.GetAsync(recipeId!, ct);
        if (recipe is null)
            return InspectionActionResult.Fail("inspectionFailed",
                $"recipe {recipeId} 미등록 — 레시피 시드/DB 확인 필요");
        if (!recipe.Enabled)
            return InspectionActionResult.Fail("inspectionFailed",
                $"recipe {recipeId} resolved but not enabled (실행 미구현 — N13/2차 대기)");
        if (recipe.SeamType == SeamTypeKind.Corner)
            return InspectionActionResult.Fail("inspectionFailed",
                "CORNER3 검사 시퀀스가 제거되어 실행할 수 없습니다.");

        // 비전 링크 — 드라이런 스텝 집합은 촬영(CAPTURE_REQ)을 하지 않으므로 링크 다운이어도 진행한다.
        if (!_vision.Client.IsConnected)
        {
            if (!recipe.DryRun)
                return InspectionActionResult.Fail("equipmentError", "비전 S/W 링크 다운 — 검사 실행 불가");
            _logger.LogInformation("비전 링크 확인 생략(드라이런) — 비전 S/W 링크 다운 상태로 진행 (recipe={Recipe})",
                recipeId);
        }

        // 6) 사전 티칭 경유점 조회: 레시피에 지정된 InspectionProfile(검사 레시피 페이지에서 지정).
        //    면 자세마다 경유점이 다르므로 레시피(LINE-FLOOR ≠ LINE-WALL)별로 명시 지정한다.
        //    CORNER 는 도면 프로필을 쓰지 않는다(고정 티칭 슬롯 corner3.* 직접 순회) — 조회 생략.
        InspectionProfile? profile = null;
        if (recipe.SeamType != SeamTypeKind.Corner)
        {
            var (found, profileError) = await FindProfileAsync(db, recipe, req.SectionDxfId, ct);
            if (found is null)
                return InspectionActionResult.Fail("inspectionFailed", profileError!);
            profile = found;
        }

        // 7) 검사 방향 자동 유도(§4.4·§8.1) — anchor 판정보다 먼저 결정해야 방향 변경 시 재교시 가능.
        //    seam 벡터를 노드 theta(벽 정면) 기준 벽면-로컬 투영.
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

        // 8) anchor 캐시 판정 — 같은 (orderId, anchorGroupId, direction) 연속이고 주행 없음 → ⑱만 실행.
        //    방향이 달라지면(수평→수직 등) 작업물 좌표계를 재교시해야 하므로 캐시 미적중 처리.
        //    CORNER 는 정렬 스텝이 없어 캐시 비적용(항상 레시피 스텝 전체 실행, 캐시 갱신도 안 함).
        bool anchorHit;
        double[]? cachedInternalFrame;
        lock (_sync)
        {
            anchorHit = recipe.SeamType != SeamTypeKind.Corner
                        && _lastAnchor == (orderId, req.AnchorGroupId, direction)
                        && _lastInternalWorkpieceFrame is { Length: 6 };
            if (!anchorHit && _lastAnchor is { } prev
                && prev.OrderId == orderId && prev.AnchorGroupId == req.AnchorGroupId
                && prev.Direction != direction)
            {
                _logger.LogInformation("anchor 방향 변경({Old}→{New}) — 작업물 좌표계 재교시 (anchorGroup={Group})",
                    prev.Direction, direction, req.AnchorGroupId);
            }
            cachedInternalFrame = anchorHit ? (double[])_lastInternalWorkpieceFrame!.Clone() : null;
        }

        // 레시피가 정한 스텝(미지정이면 등록된 전체)에서 출발해, 이 task 에 맞지 않는 것만 뺀다.
        //  · anchor 적중 → 정렬 스텝군 제거(첫 task 가 잡아 둔 작업물 좌표계 재사용)
        //  · 마지막 검사 액션이 아님 → 코봇 홈 복귀 제거(task 사이에 홈 왕복 금지)
        // 드라이런 레시피는 StepKeysJson 을 무시하고 고정 드라이런 집합을 쓴다 — 수기 StepKeysJson 누락으로
        // 교시 스텝이 빠져 ⑱이 "작업물 좌표계 없음"으로 실패하던 문제를 원천 차단한다.
        // 일반 레시피의 풀시퀀스(StepKeysJson 미지정)에서는 드라이런 전용 스텝을 뺀다.
        var stepKeys = SelectStepKeys(recipe.DryRun, ParseStepKeys(recipe.StepKeysJson),
            sequence.Steps.Select(st => st.Key), anchorHit, isLastInspection);

        if (stepKeys.Length == 0)
            return InspectionActionResult.Fail("inspectionFailed",
                $"recipe {recipeId} 에 실행할 스텝이 남지 않았습니다 — 레시피 실행 스텝 구성을 확인하세요.");

        // 8.5) CORNER3 좌/우 거울 side 판별 — 티칭 슬롯 접두사(corner3.L/R) 선택 키.
        string? cornerSide = null;
        if (recipe.SeamType == SeamTypeKind.Corner)
        {
            cornerSide = InspectionRecipeResolver.ResolveCornerSide(req.DrawingPos.WallCode, out var sideNote);
            _logger.LogInformation("CORNER3 side={Side} — {Note} (wall={Wall})",
                cornerSide, sideNote, req.DrawingPos.WallCode);
        }

        var cameraTargetMm = recipe.CameraTargetDistanceMm ?? 400;
        // params.standoffMm 은 런타임에서 쓰지 않는다 — 툴 면 이격은 AMR 이 정하는 값이고(§10 N18),
        // ②는 ③이 수렴시킬 카메라 목표거리만큼 물러난다. 수신값이 다르면 흔적만 남긴다.
        if (req.StandoffMm > 0 && Math.Abs(req.StandoffMm - cameraTargetMm) > 0.001)
            _logger.LogInformation(
                "ACS standoffMm={AcsStandoff} 미사용 — ② 면 이격은 레시피 {Recipe} 카메라 목표거리 {Target}mm 를 씁니다 (jobRef={JobRef})",
                req.StandoffMm, recipe.Id, cameraTargetMm, req.JobRef);
        if (req.WorkingDistanceMm is { } acsWd && Math.Abs(acsWd - cameraTargetMm) > 0.001)
            _logger.LogInformation(
                "ACS workingDistanceMm={AcsWd} 무시 — 레시피 {Recipe} 카메라 목표거리 {Target}mm 사용 (jobRef={JobRef})",
                acsWd, recipe.Id, cameraTargetMm, req.JobRef);

        // 9) SequenceContext 구성 — 파라미터 우선순위: ① ACS action → ② 티칭 프로필 → ③ 레시피 → ④ 전역 기본.
        //    CORNER 는 profile 이 없다 — Tool/Velocity 는 SequenceContext 기본값(단독 실행과 동일).
        //    공구는 실제 TCP 가 설정된 #1 이어야 한다 — 프로필 RunTool 이 0(미설정/플랜지)이면 1 로 보정.
        if (profile is { RunTool: <= 0 })
            _logger.LogWarning("티칭 프로필 '{Profile}' RunTool={RunTool} — 공구 #1 로 보정해 실행 (프로필 저장값 확인 필요)",
                profile.Name, profile.RunTool);
        // 장비 튜닝값 — ACS 가 보내는 값이 아니라 현장에서 시퀀스 페이지로 맞춰 저장한 상수다.
        // 예전에는 페이지만 읽고 이 경로는 기본값(0/0/−65)으로 돌아, 맞춰 놓은 값이 실제 검사에는
        // 적용되지 않는 조용한 불일치가 있었다. 같은 키를 여기서도 읽어 해소한다.
        var offsetU = await param.GetDoubleAsync(WeldSequenceSupport.InspectionOffsetUKey) ?? 0.0;
        var offsetV = await param.GetDoubleAsync(WeldSequenceSupport.InspectionOffsetVKey) ?? 0.0;
        var camToLaserShiftY = await param.GetDoubleAsync(WeldSequenceSupport.CameraToLaserShiftYKey) ?? -65.0;
        // z 기준 보정(N17) — ACS 의 z 는 도면 전역(선창 바닥) 기준이라 L2 이상은 층 바닥 높이를 빼야 한다.
        var zDatumOffset = await param.GetDoubleAsync(WeldSequenceSupport.ZDatumOffsetKey) ?? 0.0;

        var context = new SequenceContext
        {
            InspectionDirection = direction,
            Tool = profile is { RunTool: > 0 } ? profile.RunTool : 1,
            // 이동 속도: 레시피 MoveVelPercent 우선, 없으면 종전대로 프로필 RunVel(미지정 20).
            // 검사(스캔) 속도: 레시피 InspectVelPercent — null 이면 ⑱이 이동 속도로 폴백.
            Velocity = recipe.MoveVelPercent ?? profile?.RunVel ?? 20,
            InspectVelocity = recipe.InspectVelPercent,
            InspectionDrawingId = profile?.DrawingId ?? 0,
            InspectionProfileId = profile?.Id ?? 0,
            CornerSide = cornerSide,
            InspectionSurfaceId = WallCodeToSurfaceId(req.DrawingPos.WallCode),
            // ③ 카메라 거리 정렬 목표 — 레시피 값(빈 값=전역 400). ACS workingDistanceMm 은 산출 근거가 없어
            // 사용하지 않는다(2026-09-18 결정, 스키마 항목은 유지 — 수신·로그만).
            CameraTargetDistanceMm = cameraTargetMm,
            InspectionOffsetU = offsetU,
            InspectionOffsetV = offsetV,
            CameraToLaserShiftYmm = camToLaserShiftY,
            AcsJobRef = req.JobRef,
            AcsOrderId = orderId,
            AcsActionId = action.ActionId,
            // ACS 발급 식별자 → 이 액션의 모든 CAPTURE_REQ(v3.2 [15-30]/[31])에 그대로 실린다(N14).
            // null 이면 실행 스텝이 Guid.Empty/1 로 폴백.
            AcsTaskId = req.TaskId,
            AcsAttempt = req.Attempt,
            AnchorGroupId = req.AnchorGroupId,
            SeqInGroup = req.SeqInGroup,
            StandoffMmOverride = req.StandoffMm > 0 ? req.StandoffMm : null,
            // ② 검사위치 이동의 목표 좌표 — task 마다 달라지는 유일한 위치 정보(§8.1).
            SeamStartW = req.SeamStartW,
            SeamEndW = req.SeamEndW,
            WallCode = req.DrawingPos.WallCode,
            WallFacingThetaRad = nodeThetaRad,
            ZDatumOffsetMm = zDatumOffset,
            VisionFailRatioMax = recipe.VisionFailRatioMax < 1.0 ? recipe.VisionFailRatioMax : null,
        };
        if (cachedInternalFrame is not null)
            context.Bag[InternalWorkpieceFrame.BagKey] = cachedInternalFrame;

        // CROSS3/CROSS4 는 LINE 과 동일하게 티칭 프로필 경유점을 실행한다 — 코로게이션 격자 교차부는
        // 평탄하지 않아 수식 생성으로 법선·깊이를 담을 수 없으므로 6-DOF 캡처 교시(/inspection-points)로
        // 단일화했다. (구 PatternJson 런타임 생성 경로는 폐기 — CrossPatternGenerator 는 교시 시작 템플릿 전용.)

        _logger.LogInformation(
            "검사 시퀀스 시작: recipe={Recipe}{DryRun}, profile='{Profile}'(id={ProfileId}, drawing={DrawingId}), " +
            "anchor {AnchorState}, steps={Steps}",
            recipeId, recipe.DryRun ? "[드라이런]" : "", profile?.Name ?? "(미사용 — CORNER 티칭 슬롯)",
            profile?.Id ?? 0, profile?.DrawingId ?? 0,
            anchorHit ? "적중(정렬 생략)" : "신규(정렬 수행)",
            string.Join(",", stepKeys));

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
                // 활성 작업물 좌표계 0·공구 복귀(best-effort)는 SequenceService.RunSequenceAsync 가 실패 종료
                // 시(취소 제외) 수행한다 — UI 풀오토와 이 경로가 같은 반납을 공유한다.
                return InspectionActionResult.Fail("inspectionFailed",
                    $"recipe={recipeId} step={runResult.FailedStepKey ?? "?"} fail: {runResult.Message}");

            case SequenceRunOutcome.Completed:
            default:
                // CORNER 는 정렬을 수행하지 않으므로 기존 내부 프레임 캐시를 훼손하지 않는다.
                if (recipe.SeamType != SeamTypeKind.Corner)
                {
                    if (!context.Bag.TryGetValue(InternalWorkpieceFrame.BagKey, out var value)
                        || value is not double[] { Length: 6 } internalFrame)
                    {
                        InvalidateAnchor();
                        return InspectionActionResult.Fail("inspectionFailed",
                            "검사는 완료됐지만 내부 작업물 좌표계가 없어 anchor를 보존할 수 없습니다.");
                    }
                    lock (_sync)
                    {
                        _lastAnchor = (orderId, req.AnchorGroupId, direction);
                        _lastInternalWorkpieceFrame = (double[])internalFrame.Clone();
                    }
                }
                return InspectionActionResult.Ok(
                    $"recipe={recipeId} profile='{profile?.Name ?? $"corner3.{cornerSide}"}' anchor={req.AnchorGroupId}#{req.SeqInGroup}" +
                    (anchorHit ? " (정렬 공유)" : "") +
                    $" jobRef={req.JobRef} taskId={req.TaskId?.ToString() ?? "—"}");
        }
    }

    /// <summary>레시피에 지정된 티칭설정(InspectionProfile)을 반환. 미지정/없음/타입 불일치면 (null, 사유).
    /// 도면·최신 저장 기준 자동 선택은 폐기 — 레시피 페이지의 명시 지정만 사용한다(테스트 저장이 실행 대상을 바꾸지 않도록).
    /// sectionDxfId 는 로그용으로만 받는다.</summary>
    private static async Task<(InspectionProfile? Profile, string? Error)> FindProfileAsync(
        HdAmrDbContext db, InspectionRecipe recipe, string sectionDxfId, CancellationToken ct)
    {
        if (recipe.InspectionProfileId is not { } profileId)
            return (null, $"recipe {recipe.Id} 에 티칭 프로필 미지정 (sectionDxfId='{sectionDxfId}') — " +
                          "검사 레시피 페이지에서 프로필 지정 필요");

        var profile = await db.InspectionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct);
        if (profile is null)
            return (null, $"recipe {recipe.Id} 지정 티칭 프로필(id={profileId}) 없음 — 삭제됨, 레시피 재지정 필요");

        var want = InspectionRecipeResolver.ProfileSeamTypeOf(recipe.Id);
        if (!InspectionRecipeService.IsSeamMatch(profile, want))
            return (null, $"recipe {recipe.Id} 지정 프로필 '{profile.Name}' 타입({profile.SeamType}) ≠ {want} — 레시피 재지정 필요");

        return (profile, null);
    }

    /// <summary>StepKeysJson(문자열 배열) → 스텝 키 목록. null/빈/파싱 실패 → null(풀시퀀스).</summary>
    /// <summary>이 task 가 실행할 스텝 키 — 드라이런은 고정 집합, 일반은 레시피 지정 키(미지정이면 드라이런 전용을
    /// 뺀 전체)에서 anchor 적중 시 정렬 스텝군, 마지막 검사가 아니면 홈 복귀를 제외한다.</summary>
    internal static string[] SelectStepKeys(bool dryRun, string[]? recipeKeys, IEnumerable<string> allKeys,
                                            bool anchorHit, bool isLastInspection)
    {
        var baseSteps = dryRun
            ? DryRunStepKeys
            : recipeKeys ?? allKeys.Where(k => !DryRunOnlyStepKeys.Contains(k)).ToArray();
        return baseSteps
            .Where(k => !(anchorHit && AlignmentStepKeys.Contains(k)))
            .Where(k => isLastInspection || k != HomeStepKey)
            .ToArray();
    }

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

    /// <summary>wall_code → 비전 Surface ID (vision_interface.md §5, 0x01~0x0A) — 정본 표 <see cref="WallCodes"/>.
    /// 미정의 코드는 resolver 가 먼저 거른다 — 방어적 기본 0x01.</summary>
    private static int WallCodeToSurfaceId(string wallCode) => WallCodes.Find(wallCode)?.SurfaceId ?? 0x01;
}
