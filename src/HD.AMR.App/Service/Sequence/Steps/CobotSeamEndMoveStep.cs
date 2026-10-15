using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 드라이런 2점 교시용 — 코봇을 ACS 용접선 <b>끝점</b>(seamEndW) 접근점으로 이동시킨다.
/// 바로 뒤의 <see cref="WObjPointStep"/>(점2) 이 그 자리에서 현재 TCP 를 기록해, 점1(시작점)→점2(끝점)
/// 벡터가 작업물 X축(용접 진행 방향)이 된다.
///
/// 비전 정렬(③~⑯) 없이 좌표 환산만으로 2점을 잡는 드라이런 경로 전용이다. 자세는 ②(검사위치 이동)와
/// <b>같은 티칭 자세 + 같은 u/v 오프셋</b>을 써야 X축(점2−점1)이 그 상수 오프셋에 영향받지 않는다
/// (<see cref="WObjPointStep"/> 참조). 위치만 ②의 시작점 접근점에서 끝점 접근점으로 옮긴다.
///
/// ②가 아니라 면을 따라가는 짧은 횡이동이므로 <b>MoveL</b> 로 간다(③~⑱과 동일 규약 — 자세 불변, 면
/// 이격 일정 유지). 진입 준비(홈 복귀)는 ②가 이미 했으므로 하지 않는다.
///
/// <b>seamEndW 가 없으면 즉시 Fail</b> — 끝점이 없으면 점2가 점1과 같은 자리가 되어 프레임 X가 0이 된다.
/// ②와 달리 티칭 위치 폴백을 쓰지 않는다(폴백하면 두 점이 같아짐).
///
/// <b>위치는 점1 기준 평행 이동</b>이다 — 점1(<see cref="WObjPointStep"/> 기록 포즈)에서 ACS 시작점→끝점 벡터의
/// 면 방향 성분만큼 옮기고, 광축(면 법선) 성분은 버린다. 그래서 ③ 카메라 거리 정렬 등으로 점1에서 맞춘
/// 면 거리가 점2에 그대로 유지된다. 예전엔 끝점을 ACS 좌표로 새로 환산해 면 거리를 다시 정했는데, ACS 면
/// 높이가 실제와 다르면 점1(카메라 실측)과 점2(ACS)가 어긋났다(실기 2026-10-15 천장 T: ACS 기준 400mm 가
/// 실측 927mm — 점2가 527mm 낮아 작업물 좌표계가 37° 기울어 검사 수행 IK 112).
/// 점1 기록이 없으면(단독 실행 등) 종전처럼 끝점을 ACS 좌표로 환산한 접근점으로 간다.
/// </summary>
public class CobotSeamEndMoveStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly SeamApproachResolver _seam;
    private readonly ILogger<CobotSeamEndMoveStep> _logger;

    private const double MoveAcc = 100.0;
    private const double MoveOvl = 100.0;

    public CobotSeamEndMoveStep(CobotService cobot, SeamApproachResolver seam,
        ILogger<CobotSeamEndMoveStep> logger)
    {
        _cobot = cobot;
        _seam = seam;
        _logger = logger;
    }

    public string Key => "cobotSeamEnd";
    public string DisplayName => "Cobot 용접선 끝점 이동";
    public int DefaultOrder => 1155;   // wobjPoint1(760) 이후, wobjPoint2(1160) 이전

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");
        if (context.InspectionSurfaceId is <= 0x00 or > 0xFF)
            return StepValidation.Fail("검사 Wall ID 미설정 (0x01~0xFF).");
        var pos = CobotInspectionMoveStep.FindBySurfaceId(context);
        if (pos is null)
            return StepValidation.Fail(
                $"Wall 0x{context.InspectionSurfaceId:X2}에 해당하는 티칭 위치가 없습니다.");
        if (!pos.IsTaught)
            return StepValidation.Fail($"Wall 0x{context.InspectionSurfaceId:X2} '{pos.Name}' 미티칭.");
        if (context.SeamEndW is not { Length: 3 })
            return StepValidation.Fail(
                "용접선 끝점(seamEndW)이 없습니다 — 드라이런 2점 교시는 ACS seamEndW 가 필요합니다.");
        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (context.SeamEndW is not { Length: 3 })
            return StepResult.Fail(
                "용접선 끝점(seamEndW)이 없습니다 — 드라이런 2점 교시는 ACS seamEndW 가 필요합니다.");

        var inspection = CobotInspectionMoveStep.FindBySurfaceId(context)
            ?? throw new InvalidOperationException($"Wall 0x{context.InspectionSurfaceId:X2} 티칭 위치 없음");

        // 자세 선택은 점1(②)과 동일 규칙 — 위치만 끝점으로 옮긴다. 평면 벽에서는 시작점·끝점의 면 법선이
        // 같으므로 하이브리드 토글이 켜져도 점1↔점2 자세가 일치한다(작업물 X축 = 끝−시작 가 상수 자세에 불변).
        var (taught, _) = await CobotInspectionMoveStep.ComputeTargetPoseAsync(_cobot, inspection, ct);

        // 위치는 용접선 끝점 접근점 — 끝점을 approachW 로, 시작점을 방향(접선)용으로 넘긴다.
        // 면 이격은 점1(②)과 같은 고정값이어야 한다 — 예전엔 ③ 카메라 목표거리(레시피 300)를 써서 점1(400)과
        // 100mm 달랐고, 점1→점2 가 벽에 비스듬해져 작업물 좌표계·경유점 전체가 틀어졌다(실기 2026-10-15 F-A0002).
        var seam = await _seam.ResolveAsync(
            context, context.SeamEndW, context.SeamStartW, context.Tool, "끝점", ct,
            CobotInspectionMoveStep.ApproachStandoffMm);
        if (seam.Blocker is not null)
            return StepResult.Fail(seam.Blocker);   // 리치 부족 — 폴백 없이 실패
        if (seam.Base is null)
            return StepResult.Fail(
                "용접선 끝점을 코봇 BASE 로 환산할 수 없습니다 (AMR 측위·장착 보정 확인) — 드라이런은 폴백하지 않습니다.");

        var (target, usedComputed) = CobotInspectionMoveStep.BuildApproachAnchor(
            taught, seam.Base, seam.ComputedPose, seam.UseComputedOrientation);
        target = CobotInspectionMoveStep.NormalizeUvAnchor(target, _logger);

        // 점1 기준 평행 이동 — 시작점 접근점도 같은 면 이격으로 환산해 두 접근점의 차이(면 방향 성분)만 쓴다.
        var positionNote = "ACS 끝점 환산 위치";
        var fromPoint1 = false;
        if (context.Bag.TryGetValue(WObjPointStep.PointPoseBagKey(1), out var p1v) && p1v is double[] { Length: 6 } p1)
        {
            var start = await _seam.ResolveAsync(
                context, context.SeamStartW, context.SeamEndW, context.Tool, "시작점(평행 이동 기준)", ct,
                CobotInspectionMoveStep.ApproachStandoffMm);
            if (start.Base is { } sb)
            {
                var (pos, normalMm) = ParallelFromPoint1(p1, sb, seam.Base, seam.OpticalAxis);
                target = new[] { pos[0], pos[1], pos[2], target[3], target[4], target[5] };
                fromPoint1 = true;
                positionNote = $"점1 기준 면 방향 평행 이동(ACS 시작→끝 법선 성분 {normalMm:+0.0;-0.0}mm 제외, 면 거리 점1 유지)";
                _logger.LogInformation("[Cobot 용접선 끝점 이동] {Note} — 점1 [{P1}] → 목표 [{T}]",
                    positionNote,
                    string.Join(", ", p1.Take(3).Select(v => v.ToString("0.0"))),
                    string.Join(", ", pos.Select(v => v.ToString("0.0"))));
            }
            else
            {
                _logger.LogWarning("[Cobot 용접선 끝점 이동] 시작점 환산 불가({Note}) — ACS 끝점 환산 위치로 이동", start.Note);
            }
        }

        // u/v 오프셋·J6 절대각을 ②와 동일하게 맞춰 MoveL — 점1과 같은 J6 라 직선 이동 중 손목이 돌지 않는다.
        double[] finalTarget;
        try
        {
            // 점1 기준이면 u/v 오프셋은 이미 점1 위치에 들어 있다 — 다시 더하지 않는다.
            (finalTarget, _) = await CobotInspectionMoveStep.AlignTwistToJ6Async(
                _cobot, fromPoint1 ? target : CobotInspectionMoveStep.ComposeUvTarget(target, context), context.Tool,
                CobotInspectionMoveStep.TargetJ6Deg(context), ct);
        }
        catch (InvalidOperationException ex)
        {
            return StepResult.Fail(ex.Message);
        }
        var rc = await _cobot.Rpc.MoveLAsync(finalTarget, tool: context.Tool, user: 0,
            vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"용접선 끝점 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        var attitudeNote = usedComputed
            ? "자세는 계산된 면 법선"
            : $"자세는 티칭 [0x{context.InspectionSurfaceId:X2} {inspection.Name}] 유지";
        return StepResult.Ok(
            $"용접선 끝점 접근점으로 직선 이동(MoveL) 완료 — {positionNote}, {attitudeNote}. 다음: 점2(X+방향) 기록.");
    }

    /// <summary>
    /// 점1 위치에서 ACS 시작→끝 접근점 벡터의 면 방향 성분만큼 옮긴 위치와, 버린 법선 성분 [mm].
    /// 법선 = 점1 포즈의 광축 툴축(<paramref name="opticalAxis"/>) — 부호는 성분 제거에 무관하다.
    /// </summary>
    internal static (double[] Position, double NormalMm) ParallelFromPoint1(
        double[] point1Pose, double[] startBase, double[] endBase, ToolAxisDir opticalAxis)
    {
        var m = FrameMath.PoseToMatrix(point1Pose);
        var col = (int)opticalAxis / 2;
        double[] n = { m[0, col], m[1, col], m[2, col] };
        double[] d = { endBase[0] - startBase[0], endBase[1] - startBase[1], endBase[2] - startBase[2] };
        var dn = d[0] * n[0] + d[1] * n[1] + d[2] * n[2];
        var pos = new double[3];
        for (var i = 0; i < 3; i++)
            pos[i] = point1Pose[i] + d[i] - dn * n[i];
        return (pos, dn);
    }
}
