using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ② Cobot 검사위치 이동 — 티칭된 검사 준비 위치에 툴프레임 u/v 오프셋을 합성한 목표로 MoveL 직선 이동.
/// </summary>
public class CobotInspectionMoveStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ILogger<CobotInspectionMoveStep> _logger;

    public CobotInspectionMoveStep(CobotService cobot, ILogger<CobotInspectionMoveStep> logger)
    {
        _cobot = cobot;
        _logger = logger;
    }

    public string Key => "cobotInspection";
    public string DisplayName => "Cobot 검사위치 이동";
    public int DefaultOrder => 200;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        if (!context.Positions.TryGetValue("inspectionReady", out var pos) || !pos.IsTaught)
            return StepValidation.Fail("검사 준비 위치 미티칭 — Teaching에서 먼저 저장하세요.");

        if (Math.Abs(context.InspectionOffsetU) > 500 || Math.Abs(context.InspectionOffsetV) > 500)
            return StepValidation.Fail("검사 오프셋 u/v 범위 초과 (±500 mm 이내).");

        return StepValidation.Ok();
    }

    /// <summary>티칭된 검사 준비 위치의 베이스 목표 포즈 계산 (일반/작업물 추종 공용).
    /// ③ 단계의 도달 확인(<see cref="CameraAlignStep"/>)에서도 같은 목표를 재계산하는 데 쓴다.</summary>
    internal static async Task<(double[] Target, string Where)> ComputeTargetPoseAsync(
        CobotService cobot, Data.Entities.TeachingPosition inspection, CancellationToken ct)
    {
        if (inspection.UserFrame is int n && n > 0 && inspection.RelX.HasValue)
        {
            // 작업물 추종: 현재(재등록된) 프레임 T_N에 저장된 상대 pose를 적용해 베이스 목표 계산.
            // 최종 이동은 계산된 베이스 목표로 user:0 → rc=74(좌표계 불일치) 회피.
            var tN = await cobot.Rpc.GetWObjCoordAsync(n, ct);
            var rel = new[]
            {
                inspection.RelX!.Value, inspection.RelY!.Value, inspection.RelZ!.Value,
                inspection.RelRx!.Value, inspection.RelRy!.Value, inspection.RelRz!.Value,
            };
            return (FrameMath.FromFrame(rel, tN), $"검사 준비 위치(작업물 #{n} 추종)로");
        }

        var target = new[]
        {
            inspection.X!.Value, inspection.Y!.Value, inspection.Z!.Value,
            inspection.Rx!.Value, inspection.Ry!.Value, inspection.Rz!.Value,
        };
        return (target, "검사 준비 위치로");
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var inspection = context.Positions["inspectionReady"];
        var (target, where) = await ComputeTargetPoseAsync(_cobot, inspection, ct);

        // 툴프레임 오프셋: offset[0]=v(툴 X, 상+/하−), offset[1]=u(툴 Y, 좌+/우−).
        // 수직 검사 방향이면 툴 RZ −90° 회전을 합성 (병진 u/v는 회전 전 대기자세 축 기준이라 의미 불변).
        var rz = context.InspectionDirection == InspectionMoveDirection.Vertical ? -90.0 : 0.0;
        var offset = new[] { context.InspectionOffsetV, context.InspectionOffsetU, 0.0, 0.0, 0.0, rz };
        var hasOffset = context.InspectionOffsetU != 0 || context.InspectionOffsetV != 0;

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(target, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);

        var offsetNote = hasOffset
            ? $" (오프셋 u={context.InspectionOffsetU:0.###}, v={context.InspectionOffsetV:0.###} mm)"
            : "";
        if (rz != 0)
            offsetNote += " [수직, RZ−90°]";

        return rc == 0
            ? StepResult.Ok($"{where} 이동 완료{offsetNote}.")
            : StepResult.Fail($"이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
    }
}
