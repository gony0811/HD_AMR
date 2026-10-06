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

        // 자세는 점1(검사위치)과 동일한 티칭 자세 — 위치만 끝점으로 옮긴다.
        var (target, _) = await CobotInspectionMoveStep.ComputeTargetPoseAsync(_cobot, inspection, ct);
        target = CobotInspectionMoveStep.NormalizeUvAnchor(target, _logger);

        // 위치는 용접선 끝점 접근점 — 끝점을 approachW 로, 시작점을 방향(접선)용으로 넘긴다.
        var (seamBase, _, blocker) = await _seam.ResolveAsync(
            context, context.SeamEndW, context.SeamStartW, context.Tool, "끝점", ct);
        if (blocker is not null)
            return StepResult.Fail(blocker);   // 리치 부족 — 폴백 없이 실패
        if (seamBase is null)
            return StepResult.Fail(
                "용접선 끝점을 코봇 BASE 로 환산할 수 없습니다 (AMR 측위·장착 보정 확인) — 드라이런은 폴백하지 않습니다.");

        target = new[] { seamBase[0], seamBase[1], seamBase[2], target[3], target[4], target[5] };

        // u/v 오프셋·J6 절대각을 ②와 동일하게 맞춰 MoveL — 점1과 같은 J6 라 직선 이동 중 손목이 돌지 않는다.
        double[] finalTarget;
        try
        {
            (finalTarget, _) = await CobotInspectionMoveStep.AlignTwistToJ6Async(
                _cobot, CobotInspectionMoveStep.ComposeUvTarget(target, context), context.Tool,
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

        return StepResult.Ok(
            $"용접선 끝점 접근점으로 직선 이동(MoveL) 완료 — 자세는 티칭 " +
            $"[0x{context.InspectionSurfaceId:X2} {inspection.Name}] 유지. 다음: 점2(X+방향) 기록.");
    }
}
