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
///  ③ <b>작업 준비 위치 경유</b> — 홈에서 검사 위치로 MoveJ 시 900mm 이하 영역에서 툴이 차체와 충돌할 수
///     있다. 홈 복귀 후 "ready" 티칭 위치(홈에서 툴 Z −300mm)로 먼저 이동해 충돌 영역을 벗어난다.
///     미티칭이면 홈에서 툴 Z −300mm 오프셋을 자동 계산해 사용한다.
///
/// 부르는 곳은 ② <see cref="CobotInspectionMoveStep"/>(LINE/CROSS)이다. 앵커 히트 경로
/// (inspectionRun·wobjReset만 실행)는 직전 정렬 자세를 그대로 재사용하는 것이 목적이라 ①을 타지 않는다.
/// </summary>
internal static class SequenceEntry
{
    /// <summary>홈 도달 판정 관절 허용오차 [도].</summary>
    private const double HomeToleranceDeg = 0.5;

    /// <summary>ready 미티칭 시 홈에서 툴 Z 방향 자동 후퇴 거리 [mm].</summary>
    private const double ReadyToolZOffsetMm = -300.0;

    /// <summary>벽면에서 직선 후퇴 거리 [mm]. 툴 Z+ 방향(벽에서 멀어지는 방향).</summary>
    private const double RetractDistanceMm = 300.0;

    /// <summary>홈 티칭 여부 — 진입 준비를 하는 스텝의 Validate 에서 부른다.</summary>
    public static StepValidation ValidateHome(SequenceContext context)
        => context.Positions.TryGetValue("home", out var home) && home.IsTaught
            ? StepValidation.Ok()
            : StepValidation.Fail("홈 위치 미티칭 — Teaching에서 먼저 저장하세요.");

    /// <summary>활성 좌표계 정규화 + 안전 홈 복귀 + 작업 준비 위치 경유. 결과 메시지에 붙일 주석을 돌려준다.
    /// 홈에 없을 때(이전 검사 자세 잔류) 벽면 직선 후퇴 → ready 경유 → 홈 순서로 차체 충돌을 회피한다.</summary>
    public static async Task<string> PrepareAsync(
        CobotService cobot, SequenceContext context, ILogger logger, CancellationToken ct)
    {
        await NormalizeActiveFramesAsync(cobot, context, logger, ct);

        // 이미 작업 준비(ready) 위치면 그대로 출발한다 — ready 는 홈에서 나와 도착하는 경유점이라
        // 검사 자세 잔류가 아니다. 앞 task 가 ready 에서 실패한 경우 후퇴·홈 왕복을 되풀이하지 않는다.
        if (await IsAtReadyAsync(cobot, context, ct))
        {
            logger.LogInformation("진입 준비: 이미 작업 준비 위치 — 후퇴·홈 왕복 생략");
            return $" 활성 좌표계 툴 #{context.Tool}/작업물 0 정규화, (이미 작업 준비 위치).";
        }

        // 이전 검사 자세에서 홈으로 바로 MoveJ 하면 차체 충돌 위험 → 벽면 후퇴 + ready 경유.
        var retracted = false;
        var viaReady = false;
        if (!await IsAtHomeAsync(cobot, context, ct))
        {
            retracted = await RetractFromWallAsync(cobot, context, logger, ct);
            viaReady = await ReturnViaReadyAsync(cobot, context, logger, ct);
        }

        var moved = await EnsureCobotAtHomeAsync(cobot, context, ct);
        var readyMoved = await MoveToReadyAsync(cobot, context, logger, ct);

        var parts = new List<string> { $"활성 좌표계 툴 #{context.Tool}/작업물 0 정규화" };
        if (retracted) parts.Add("벽면 후퇴");
        if (viaReady) parts.Add("작업 준비 위치 경유(복귀)");
        parts.Add(moved ? "코봇 홈 복귀" : "(이미 홈)");
        if (readyMoved) parts.Add("작업 준비 위치 경유(출발)");
        return $" {string.Join(", ", parts)}.";
    }

    private static async Task<bool> IsAtHomeAsync(CobotService cobot, SequenceContext ctx, CancellationToken ct)
    {
        if (!ctx.Positions.TryGetValue("home", out var home) || !home.IsTaught) return true;
        var homeJoints = new[]
        {
            home.J1!.Value, home.J2!.Value, home.J3!.Value,
            home.J4!.Value, home.J5!.Value, home.J6!.Value,
        };
        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        return IsWithinJointTolerance(cur, homeJoints);
    }

    /// <summary>현재 관절각이 티칭된 ready 와 일치하면 true. 미티칭(자동 계산 ready)은 관절 기준이 없어 false.</summary>
    private static async Task<bool> IsAtReadyAsync(CobotService cobot, SequenceContext ctx, CancellationToken ct)
    {
        if (!ctx.Positions.TryGetValue("ready", out var ready) || !ready.IsTaught) return false;
        var readyJoints = new[]
        {
            ready.J1!.Value, ready.J2!.Value, ready.J3!.Value,
            ready.J4!.Value, ready.J5!.Value, ready.J6!.Value,
        };
        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        return IsWithinJointTolerance(cur, readyJoints);
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

    /// <summary>티칭 관절값으로 MoveJ 할 때는 <see cref="FairinoRpcClient.UnwrapToward"/> 로 현재 관절 쪽 ±360°
    /// 감기를 맞춰 지령한다 — 판정(<see cref="IsWithinJointTolerance"/>)은 감아서 비교하는데 지령은 원값이면,
    /// 실제 J6=−179.6° · 티칭 J6=+180.4° 처럼 같은 자세에서도 J6 가 한 바퀴(+360°) 돈다(2026-10-07 현장).
    /// 현재 관절각이 홈과 다르면 MoveJ로 복귀. 이동했으면 true. 시퀀스 진입(②·⑱ᶜ)과
    /// 종료(<see cref="CobotHomeReturnStep"/>) 양쪽이 같은 경로를 쓴다.</summary>
    public static async Task<bool> EnsureCobotAtHomeAsync(
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

        var rc = await cobot.Rpc.MoveJAsync(FairinoRpcClient.UnwrapToward(homeJoints, cur), homePose,
            tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException($"홈 이동(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        return true;
    }

    /// <summary>홈 복귀 후 작업 준비(ready) 위치로 이동. 홈→검사 위치 MoveJ 경로에서 툴이 차체에
    /// 충돌하는 것을 방지한다. 티칭된 ready 위치가 있으면 사용, 없으면 홈에서 툴 Z −300mm 자동 계산.</summary>
    public static async Task<bool> MoveToReadyAsync(
        CobotService cobot, SequenceContext ctx, ILogger logger, CancellationToken ct)
    {
        var (readyPose, readyJoints, src) = await ResolveReadyAsync(cobot, ctx, logger, ct);
        if (readyPose is null) return false;

        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        if (readyJoints is not null && IsWithinJointTolerance(cur, readyJoints))
            return false;

        int rc;
        if (readyJoints is not null)
        {
            rc = await cobot.Rpc.MoveJAsync(FairinoRpcClient.UnwrapToward(readyJoints, cur), readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }
        else
        {
            var joints = await cobot.Rpc.GetInverseKinForMoveAsync(readyPose, ctx.Tool, 0, ct);
            rc = await cobot.Rpc.MoveJAsync(joints, readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }

        if (rc != 0)
            throw new InvalidOperationException(
                $"작업 준비 위치 이동(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        logger.LogInformation("작업 준비 위치로 이동 완료 ({Src})", src);
        return true;
    }

    /// <summary>검사 후 홈 복귀 전에 작업 준비(ready) 위치를 경유한다. 검사 자세에서 직접 홈으로
    /// MoveJ 시 충돌 위험이 있으므로 ready 를 거쳐 안전하게 홈에 도달한다.</summary>
    public static async Task<bool> ReturnViaReadyAsync(
        CobotService cobot, SequenceContext ctx, ILogger logger, CancellationToken ct)
    {
        var home = ctx.Positions["home"];
        var homeJoints = new[]
        {
            home.J1!.Value, home.J2!.Value, home.J3!.Value,
            home.J4!.Value, home.J5!.Value, home.J6!.Value,
        };

        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        if (IsWithinJointTolerance(cur, homeJoints)) return false;

        var (readyPose, readyJoints, src) = await ResolveReadyAsync(cobot, ctx, logger, ct);
        if (readyPose is null) return false;

        if (readyJoints is not null && IsWithinJointTolerance(cur, readyJoints))
            return false;

        int rc;
        if (readyJoints is not null)
        {
            rc = await cobot.Rpc.MoveJAsync(FairinoRpcClient.UnwrapToward(readyJoints, cur), readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }
        else
        {
            var joints = await cobot.Rpc.GetInverseKinForMoveAsync(readyPose, ctx.Tool, 0, ct);
            rc = await cobot.Rpc.MoveJAsync(joints, readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }

        if (rc != 0)
            throw new InvalidOperationException(
                $"작업 준비 위치 경유 이동(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        logger.LogInformation("홈 복귀 전 작업 준비 위치 경유 완료 ({Src})", src);
        return true;
    }

    private static async Task<(double[]? Pose, double[]? Joints, string Src)> ResolveReadyAsync(
        CobotService cobot, SequenceContext ctx, ILogger logger, CancellationToken ct)
    {
        if (ctx.Positions.TryGetValue("ready", out var ready) && ready.IsTaught)
        {
            var pose = new[]
            {
                ready.X!.Value, ready.Y!.Value, ready.Z!.Value,
                ready.Rx!.Value, ready.Ry!.Value, ready.Rz!.Value,
            };
            var joints = new[]
            {
                ready.J1!.Value, ready.J2!.Value, ready.J3!.Value,
                ready.J4!.Value, ready.J5!.Value, ready.J6!.Value,
            };
            return (pose, joints, "티칭");
        }

        // 미티칭: 홈에서 툴 Z −300mm 자동 계산
        if (!ctx.Positions.TryGetValue("home", out var home) || !home.IsTaught)
            return (null, null, "");

        var homePose = new[]
        {
            home.X!.Value, home.Y!.Value, home.Z!.Value,
            home.Rx!.Value, home.Ry!.Value, home.Rz!.Value,
        };
        var offset = new[] { 0.0, 0.0, ReadyToolZOffsetMm, 0.0, 0.0, 0.0 };
        var computed = FrameMath.FromFrame(offset, homePose);
        logger.LogInformation(
            "작업 준비 위치 미티칭 — 홈에서 툴 Z {Offset:0}mm 자동 계산: [{Pose}]",
            ReadyToolZOffsetMm, string.Join(", ", computed.Select(v => v.ToString("0.##"))));
        return (computed, null, $"자동(홈+툴Z {ReadyToolZOffsetMm:0}mm)");
    }

    /// <summary>현재 위치에서 툴 Z+ 방향으로 직선 후퇴(MoveL). 벽에서 떨어져 MoveJ 경로에
    /// 차체 충돌 여유를 확보한다. 실패 시 경고만 남기고 false.</summary>
    public static async Task<bool> RetractFromWallAsync(
        CobotService cobot, SequenceContext ctx, ILogger logger, CancellationToken ct)
    {
        try
        {
            var currentPose = await cobot.Rpc.GetTcpPoseInBaseAsync(ctx.Tool, ct);
            var offset = new[] { 0.0, 0.0, RetractDistanceMm, 0.0, 0.0, 0.0 };
            logger.LogInformation("벽면 직선 후퇴 시작: 현재=[{Pose}], 툴Z+{Dist}mm",
                string.Join(", ", currentPose.Select(v => v.ToString("0.#"))), RetractDistanceMm);

            var rc = await cobot.Rpc.MoveByToolOffsetAsync(
                currentPose, user: 0, offset, tool: ctx.Tool, vel: ctx.Velocity, ct: ct);

            if (rc != 0)
            {
                logger.LogWarning("벽면 후퇴 실패 (rc={Rc}){Desc} — 직접 MoveJ 시도",
                    rc, FairinoErrorCodes.Suffix(rc));
                return false;
            }

            logger.LogInformation("벽면 후퇴 완료 ({Dist}mm)", RetractDistanceMm);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "벽면 후퇴 중 예외 — 직접 MoveJ 시도");
            return false;
        }
    }

    /// <summary>현재 관절각이 목표(홈/ready)와 전 축 <see cref="HomeToleranceDeg"/> 이내인지.
    /// 각 축 차이는 ±360° 감아 [−180,180]°로 정규화해 비교한다 — J6 같은 축이 +180° vs −180°(= 같은
    /// 물리 자세, 한 바퀴 차)로 표현돼도 "같은 위치"로 본다. 정규화를 안 하면 홈에 있어도 360° 차이로
    /// '홈 아님'이 되어 블라인드 벽면 후퇴가 베이스 방향으로 실행되는 위험이 있다(<see cref="FairinoRpcClient.UnwrapToward"/>와 동일 규약).</summary>
    internal static bool IsWithinJointTolerance(double[] cur, double[] target)
    {
        for (var i = 0; i < 6; i++)
        {
            var d = (cur[i] - target[i]) % 360.0;   // ±360° 감기 → [−180,180]
            if (d > 180.0) d -= 360.0;
            else if (d < -180.0) d += 360.0;
            if (Math.Abs(d) > HomeToleranceDeg) return false;
        }
        return true;
    }
}
