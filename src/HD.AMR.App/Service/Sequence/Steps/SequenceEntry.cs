using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 시퀀스 진입 준비 — <b>코봇을 실제로 움직이는 첫 스텝이 자기 앞에서 직접 부른다.</b>
///
/// 원래 ① AmrMoveStep 이 하던 일이다. 그 스텝의 이름값(AMR 이동)은 끝내 구현되지 않았고(ACS 가 VDA5050
/// order 로 AMR 을 옮긴다), 실제로 한 일은 아래 둘뿐이라 스텝을 없애고 책임만 옮겼다:
///
///  ① <b>활성 좌표계 정규화</b> — 이전 실행이 반납하지 못한 작업물 프레임이나 펜던트에서 바뀐 활성 공구가
///     남아 있으면, 이후 단계의 앵커 계산(FK→베이스)과 역기구학이 그 값을 기준으로 해석돼 엉뚱한 위치로
///     가거나 rc=38/112 를 낸다. 무변위 MoveJ 라 로봇은 움직이지 않는다. 이 펌웨어는 활성 좌표계 실측
///     조회를 지원하지 않아 '이미 맞는지'를 신뢰할 수 없으므로(추정값으로 건너뛰면 콜드스타트에서 잔류
///     프레임을 놓친다) 조건 없이 재설정한다.
///  ② <b>코봇 홈 복귀</b> — 검사 경로는 홈에서 출발하는 것을 전제로 티칭돼 있다. 이미 홈이면 움직이지 않는다.
///
/// 부르는 곳은 "코봇을 움직이기 시작하는 스텝" 둘이다 — ② <see cref="CobotInspectionMoveStep"/>(LINE/CROSS)
/// 와 ⑱ᶜ <see cref="CornerInspectionRunStep"/>(CORNER3). 앵커 히트 경로(inspectionRun·wobjReset 만 실행)는
/// 직전 정렬 자세를 그대로 재사용하는 것이 목적이라 예전에도 ①을 타지 않았고, 지금도 타지 않는다.
/// </summary>
internal static class SequenceEntry
{
    /// <summary>홈 도달 판정 관절 허용오차 [도].</summary>
    private const double HomeToleranceDeg = 0.5;

    /// <summary>홈 티칭 여부 — 진입 준비를 하는 스텝의 Validate 에서 부른다.</summary>
    public static StepValidation ValidateHome(SequenceContext context)
        => context.Positions.TryGetValue("home", out var home) && home.IsTaught
            ? StepValidation.Ok()
            : StepValidation.Fail("홈 위치 미티칭 — Teaching에서 먼저 저장하세요.");

    /// <summary>활성 좌표계 정규화 + 홈 복귀. 결과 메시지에 붙일 주석을 돌려준다.</summary>
    public static async Task<string> PrepareAsync(
        CobotService cobot, SequenceContext context, ILogger logger, CancellationToken ct)
    {
        await NormalizeActiveFramesAsync(cobot, context, logger, ct);
        var moved = await EnsureCobotAtHomeAsync(cobot, context, ct);

        return $" 활성 좌표계 툴 #{context.Tool}/작업물 0 정규화" + (moved ? ", 코봇 홈 복귀." : " (이미 홈).");
    }

    private static async Task NormalizeActiveFramesAsync(
        CobotService cobot, SequenceContext ctx, ILogger logger, CancellationToken ct)
    {
        // 로그용 현재값(추정 허용 — 판단이 아니라 기록에만 쓴다).
        var (curTool, curUser) = await cobot.Rpc.ResolveActiveFramesAsync(ct, strict: false);
        logger.LogInformation("진입 준비: 활성 좌표계 툴 #{CurT}/작업물 #{CurU}(추정) → 툴 #{T}/작업물 0",
            curTool, curUser, ctx.Tool);

        var rc = await cobot.Rpc.ResetActiveFrameAsync(ctx.Tool, 0, ct);
        if (rc != 0)
            throw new InvalidOperationException(
                $"활성 좌표계 정규화 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — " +
                $"툴 #{ctx.Tool}/작업물 0 으로 맞추지 못했습니다. 코봇 페이지의 '활성 좌표계 초기화'로 복구하세요.");
    }

    /// <summary>현재 관절각이 홈과 다르면 MoveJ로 복귀. 이동했으면 true.</summary>
    private static async Task<bool> EnsureCobotAtHomeAsync(
        CobotService cobot, SequenceContext ctx, CancellationToken ct)
    {
        var home = ctx.Positions["home"];
        var homeJoints = new[]
        {
            home.J1!.Value, home.J2!.Value, home.J3!.Value,
            home.J4!.Value, home.J5!.Value, home.J6!.Value,
        };

        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        if (IsWithinJointTolerance(cur, homeJoints)) return false;

        var homePose = new[]
        {
            home.X!.Value, home.Y!.Value, home.Z!.Value,
            home.Rx!.Value, home.Ry!.Value, home.Rz!.Value,
        };

        var rc = await cobot.Rpc.MoveJAsync(homeJoints, homePose,
            tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException($"홈 이동(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        return true;
    }

    private static bool IsWithinJointTolerance(double[] cur, double[] target)
    {
        for (var i = 0; i < 6; i++)
            if (Math.Abs(cur[i] - target[i]) > HomeToleranceDeg) return false;
        return true;
    }
}
