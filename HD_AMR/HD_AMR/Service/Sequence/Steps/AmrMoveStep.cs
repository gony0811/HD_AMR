using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ① AMR 검사위치 이동 — 코봇이 홈 위치에 있는지 확인/복귀 후 AMR을 검사위치로 이동.
/// AMR 이동 명령은 아직 미구현(TODO).
/// </summary>
public class AmrMoveStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ILogger<AmrMoveStep> _logger;

    private const double HomeToleranceDeg = 0.5;

    public AmrMoveStep(CobotService cobot, ILogger<AmrMoveStep> logger)
    {
        _cobot = cobot;
        _logger = logger;
    }

    public string Key => "amrMove";
    public string DisplayName => "AMR 검사위치 이동";
    public int DefaultOrder => 100;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        if (!context.Positions.TryGetValue("home", out var home) || !home.IsTaught)
            return StepValidation.Fail("홈 위치 미티칭 — Teaching에서 먼저 저장하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var home = context.Positions["home"];

        var moved = await EnsureCobotAtHomeAsync(home, context, ct);

        // TODO: AMR 검사위치 이동 명령 구현

        return moved
            ? StepResult.Ok("코봇 홈 복귀 완료 (AMR 이동은 미구현).")
            : StepResult.Ok("코봇이 이미 홈 위치에 있습니다 (AMR 이동은 미구현).");
    }

    /// <summary>현재 관절각이 홈과 다르면 MoveJ로 복귀. 이동했으면 true.</summary>
    private async Task<bool> EnsureCobotAtHomeAsync(
        Data.Entities.TeachingPosition home, SequenceContext ctx, CancellationToken ct)
    {
        var homeJoints = new[]
        {
            home.J1!.Value, home.J2!.Value, home.J3!.Value,
            home.J4!.Value, home.J5!.Value, home.J6!.Value,
        };

        var cur = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
        if (IsWithinJointTolerance(cur, homeJoints)) return false;

        var homePose = new[]
        {
            home.X!.Value, home.Y!.Value, home.Z!.Value,
            home.Rx!.Value, home.Ry!.Value, home.Rz!.Value,
        };

        var rc = await _cobot.Rpc.MoveJAsync(homeJoints, homePose,
            tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException($"홈 이동(MoveJ) 실패 (rc={rc}).");

        return true;
    }

    private static bool IsWithinJointTolerance(double[] cur, double[] target)
    {
        for (var i = 0; i < 6; i++)
            if (Math.Abs(cur[i] - target[i]) > HomeToleranceDeg) return false;
        return true;
    }
}
