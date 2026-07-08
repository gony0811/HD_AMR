using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ② Cobot 검사위치 이동 — 티칭된 검사 준비 위치로 MoveL 직선 이동.
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

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var inspection = context.Positions["inspectionReady"];

        double[] target;
        string where;
        if (inspection.UserFrame is int n && n > 0 && inspection.RelX.HasValue)
        {
            // 작업물 추종: 현재(재등록된) 프레임 T_N에 저장된 상대 pose를 적용해 베이스 목표 계산.
            // 최종 이동은 계산된 베이스 목표로 user:0 → rc=74(좌표계 불일치) 회피.
            var tN = await _cobot.Rpc.GetWObjCoordAsync(n, ct);
            var rel = new[]
            {
                inspection.RelX!.Value, inspection.RelY!.Value, inspection.RelZ!.Value,
                inspection.RelRx!.Value, inspection.RelRy!.Value, inspection.RelRz!.Value,
            };
            target = FrameMath.FromFrame(rel, tN);
            where = $"검사 준비 위치(작업물 #{n} 추종)로";
        }
        else
        {
            target = new[]
            {
                inspection.X!.Value, inspection.Y!.Value, inspection.Z!.Value,
                inspection.Rx!.Value, inspection.Ry!.Value, inspection.Rz!.Value,
            };
            where = "검사 준비 위치로";
        }

        var rc = await _cobot.Rpc.MoveLAsync(target,
            tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);

        return rc == 0
            ? StepResult.Ok($"{where} 이동 완료.")
            : StepResult.Fail($"이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
    }
}
