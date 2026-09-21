using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

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

        await NormalizeActiveFramesAsync(context, ct);
        var moved = await EnsureCobotAtHomeAsync(home, context, ct);

        // TODO: AMR 검사위치 이동 명령 구현

        var frameNote = $" 활성 좌표계 툴 #{context.Tool}/작업물 0 정규화.";
        return moved
            ? StepResult.Ok($"코봇 홈 복귀 완료 (AMR 이동은 미구현).{frameNote}")
            : StepResult.Ok($"코봇이 이미 홈 위치에 있습니다 (AMR 이동은 미구현).{frameNote}");
    }

    /// <summary>시퀀스 첫 단계에서 활성 좌표계를 기준값(공구 context.Tool, 작업물 0=베이스)으로 정규화한다.
    /// 이전 실행이 반납하지 못한 작업물 프레임이나 펜던트에서 바뀐 활성 공구가 남아 있으면, 이후 단계의
    /// 앵커 계산(FK→베이스)과 역기구학이 그 값을 기준으로 해석돼 엉뚱한 위치로 가거나 rc=38/112 를 낸다.
    /// 무변위 MoveJ 라 로봇은 움직이지 않는다. 이 펌웨어는 활성 좌표계 실측 조회를 지원하지 않아 '이미 맞는지'를
    /// 신뢰할 수 없으므로(추정값으로 건너뛰면 콜드스타트에서 잔류 프레임을 놓친다) 조건 없이 재설정한다.</summary>
    private async Task NormalizeActiveFramesAsync(SequenceContext ctx, CancellationToken ct)
    {
        // 로그용 현재값(추정 허용 — 판단이 아니라 기록에만 쓴다).
        var (curTool, curUser) = await _cobot.Rpc.ResolveActiveFramesAsync(ct, strict: false);
        _logger.LogInformation("① 활성 좌표계 정규화: 툴 #{CurT}/작업물 #{CurU}(추정) → 툴 #{T}/작업물 0",
            curTool, curUser, ctx.Tool);

        var rc = await _cobot.Rpc.ResetActiveFrameAsync(ctx.Tool, 0, ct);
        if (rc != 0)
            throw new InvalidOperationException(
                $"활성 좌표계 정규화 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — " +
                $"툴 #{ctx.Tool}/작업물 0 으로 맞추지 못했습니다. 코봇 페이지의 '활성 좌표계 초기화'로 복구하세요.");
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
