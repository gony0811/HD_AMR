using HD.AMR.App.Communication;
using HD.AMR.App.Models;
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
///  ④ <b>자세 모드</b> — 상부 면(T/PU/SU)은 "홈 위치_2" 자세 모드에서만, 그 외 면은 홈 모드에서만 진입한다.
///     두 모드 전환은 운영자 수동 전용이라 모드가 맞지 않으면 움직이지 않고 실패한다(<see cref="ModeMismatch"/>).
///
/// 부르는 곳은 ② <see cref="CobotInspectionMoveStep"/>(LINE/CROSS)이다. 같은 anchor 그룹의 연속 task 는
/// 직전 종료 자세가 그대로면 ②가 이 진입 준비를 건너뛰고 면을 따라 다음 시작점으로 바로 간다(액션 사이 복귀 금지).
/// </summary>
internal static class SequenceEntry
{
    /// <summary>홈 도달 판정 관절 허용오차 [도].</summary>
    private const double HomeToleranceDeg = 0.5;

    /// <summary>ready 미티칭 시 홈에서 툴 Z 방향 자동 후퇴 거리 [mm].</summary>
    private const double ReadyToolZOffsetMm = -300.0;

    /// <summary>벽면에서 직선 후퇴 거리 [mm]. 툴 Z+ 방향(벽에서 멀어지는 방향).</summary>
    internal const double RetractDistanceMm = 300.0;

    /// <summary>툴 Z+ 의 BASE 연직 성분이 이보다 작으면(아래로 향하면) 후퇴가 내려가는 것으로 본다.</summary>
    private const double RetractDescendDirZ = -0.05;

    /// <summary>내려가는 후퇴를 수평으로 바꿀 최소 수평 성분(|툴 Z+ 의 BASE XY|). 이보다 작으면(천장처럼
    /// 광축이 거의 연직) 수평 성분만으로는 벽에서 떨어지지 않으므로 툴 Z+ 그대로 물러난다.</summary>
    private const double RetractMinHorizontalFraction = 0.5;

    /// <summary>내려가는 후퇴의 끝점 TCP 최저 높이(BASE z) [mm]. 이보다 낮아지면 움직이지 않고 정지한다.
    /// 2026-10-30 SU 챔퍼(Ry 45°)에서 툴 Z+300 후퇴가 z 229→17mm 로 AMR 쪽으로 내려가 J2·J3 가 접혔다.
    /// <b>실측 전 잠정값</b> — 차체 상면 높이를 재서 맞춘다.</summary>
    internal const double RetractMinDescendEndZMm = 200.0;

    /// <summary>홈 티칭 여부 — 진입 준비를 하는 스텝의 Validate 에서 부른다.</summary>
    public static StepValidation ValidateHome(SequenceContext context)
        => context.Positions.TryGetValue("home", out var home) && home.IsTaught
            ? StepValidation.Ok()
            : StepValidation.Fail("홈 위치 미티칭 — Teaching에서 먼저 저장하세요.");

    /// <summary>상부 자세 모드의 기준 자세 키. 홈 ↔ 홈 2 전환(ready → 홈 1.5 → 홈 1.6 경유)은 벽 충돌 위험이 커서
    /// 자동 운전에서는 하지 않고 운영자가 Teaching 에서 수동으로만 한다(2026-10-29 운영안).</summary>
    internal const string Home2Key = "home2";

    /// <summary>자세 모드 — 로봇이 지금 어느 기준 자세 쪽에 있는지. 자동 운전은 모드를 바꾸지 않는다.</summary>
    internal enum PoseMode { Home, Home2 }

    /// <summary>홈 위치_2 모드가 필요한 면 — 천장(T)·상부 챔퍼(PU/SU). 홈 위치 쪽에서는 벽 충돌·역기구학 오류로 도달 불가.</summary>
    internal static bool IsUpperRoute(string? wallCode)
        => WallCodes.Find(wallCode)?.Orientation is SurfaceOrientation.Ceiling or SurfaceOrientation.ChamferUpper;

    /// <summary>진입 경유점 티칭 여부 — 상부 면은 홈 위치_2, 그 외는 ready.</summary>
    public static StepValidation ValidateRoute(SequenceContext context)
    {
        if (IsUpperRoute(context.WallCode))
            return TaughtJoints(context, Home2Key) is not null
                ? StepValidation.Ok()
                : StepValidation.Fail(
                    $"홈 위치_2 미티칭 — 상부 면({context.WallCode})은 홈 위치_2 자세에서 진입합니다. Teaching에서 먼저 저장하세요.");

        return context.Positions.TryGetValue("ready", out var ready) && ready.IsTaught
            ? StepValidation.Ok()
            : StepValidation.Fail("작업 준비 위치(ready) 미티칭 — Teaching에서 먼저 저장하세요.");
    }

    /// <summary>현재 자세 모드 — 홈 위치_2 가 티칭돼 있고, 홈 위치_2 까지의 관절 거리가 ready(미티칭이면 홈)까지보다
    /// 짧으면 <see cref="PoseMode.Home2"/>. 검사 도중 멈춘 자세도 같은 규칙으로 가까운 쪽에 붙는다.</summary>
    internal static PoseMode ModeOf(double[] cur, SequenceContext ctx)
    {
        if (TaughtJoints(ctx, Home2Key) is not { } home2) return PoseMode.Home;
        var normal = TaughtJoints(ctx, "ready") ?? TaughtJoints(ctx, "home");
        return normal is null || JointDistance(cur, home2) < JointDistance(cur, normal)
            ? PoseMode.Home2
            : PoseMode.Home;
    }

    /// <summary>작업 면과 자세 모드가 맞지 않을 때의 실패 사유. 맞으면 null.
    /// 자동 운전은 모드를 바꾸지 않으므로(홈 1.5·1.6 경유 금지) 운영자 수동 전환이 필요하다.</summary>
    internal static string? ModeMismatch(PoseMode mode, string? wallCode)
    {
        var upper = IsUpperRoute(wallCode);
        if (upper && mode == PoseMode.Home)
            return $"수동 자세 전환 필요 — 상부 면({wallCode})은 홈 위치_2 자세에서만 검사합니다. 현재 홈 위치 자세입니다. " +
                   "Teaching에서 홈 위치 → 작업 준비 위치 → 홈 위치_1.5 → 홈 위치_1.6 → 홈 위치_2 로 전환한 뒤 재시도하세요.";
        if (!upper && mode == PoseMode.Home2)
            return $"수동 자세 전환 필요 — {wallCode ?? "일반"} 면은 홈 위치 자세에서만 검사합니다. 현재 홈 위치_2 자세입니다. " +
                   "Teaching에서 홈 위치_2 → 홈 위치_1.6 → 홈 위치_1.5 → 작업 준비 위치 → 홈 위치 로 전환한 뒤 재시도하세요.";
        return null;
    }

    /// <summary>활성 좌표계 정규화 + 자세 모드 확인 + 진입 경유점 이동. 모드가 작업 면과 맞지 않으면 움직이지 않고
    /// 실패를 돌려준다. 성공 시 결과 메시지에 붙일 주석을 돌려준다.
    /// · 홈 모드(일반 면): 이미 ready 면 그대로, 아니면 <see cref="ReturnToBaseAsync"/>(후퇴 → ready → 홈) → ready.
    /// · 홈 2 모드(T/PU/SU): <see cref="ReturnToBaseAsync"/>(후퇴 → 검사 준비 → 홈 2) → 해당 면 검사 준비(티칭돼 있을 때).</summary>
    public static async Task<(bool Ok, string Note)> PrepareAsync(
        CobotService cobot, SequenceContext context, ILogger logger, CancellationToken ct)
    {
        await NormalizeActiveFramesAsync(cobot, context, logger, ct);
        var frames = $"활성 좌표계 툴 #{context.Tool}/작업물 0 정규화";

        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        var mode = ModeOf(cur, context);
        if (ModeMismatch(mode, context.WallCode) is { } mismatch)
        {
            logger.LogWarning("진입 준비 중단: {Msg}", mismatch);
            return (false, mismatch);
        }

        if (mode == PoseMode.Home2)
        {
            Volatile.Write(ref _lastUpperWall, context.WallCode);   // 이후 면 정보 없는 복귀가 이 면 슬롯만 거슬러 가도록
            var slotKey = TeachingService.WallSlotKey(context.WallCode!);
            if (IsAt(cur, context, slotKey) || IsAt(cur, context, Home2Key) && TaughtJoints(context, slotKey) is null)
            {
                logger.LogInformation("진입 준비: 이미 홈 위치_2 모드 경유점 — 복귀 왕복 생략");
                return (true, $" {frames}, (홈 위치_2 모드, 이미 경유점).");
            }
            var parts2 = new List<string> { frames, "홈 위치_2 모드" };
            parts2.AddRange(await ReturnToBaseAsync(cobot, context, logger, ct));
            if (TaughtJoints(context, slotKey) is not null)
            {
                await MoveJToTaughtAsync(cobot, context, slotKey, $"검사 준비({context.WallCode})", ct);
                parts2.Add($"검사 준비({context.WallCode}) 경유(출발)");
            }
            return (true, $" {string.Join(", ", parts2)}.");
        }

        // 이미 작업 준비(ready) 위치면 그대로 출발한다 — ready 는 홈에서 나와 도착하는 경유점이라
        // 검사 자세 잔류가 아니다. 앞 task 가 ready 에서 실패한 경우 후퇴·홈 왕복을 되풀이하지 않는다.
        if (IsAt(cur, context, "ready"))
        {
            logger.LogInformation("진입 준비: 이미 작업 준비 위치 — 후퇴·홈 왕복 생략");
            return (true, $" {frames}, (이미 작업 준비 위치).");
        }

        var parts = new List<string> { frames };
        var back = await ReturnToBaseAsync(cobot, context, logger, ct);
        parts.Add(back.Count == 0 ? "(이미 홈)" : string.Join(" → ", back));
        if (await MoveToReadyAsync(cobot, context, logger, ct)) parts.Add("작업 준비 위치 경유(출발)");
        return (true, $" {string.Join(", ", parts)}.");
    }

    /// <summary>
    /// 현재 자세 모드의 기준 자세로 안전하게 돌아온다 — 시퀀스 진입·종료, AMR 주행 전 복귀가 모두 이 경로를 쓴다.
    /// 자세 모드(<see cref="ModeOf"/>)는 바꾸지 않는다:
    /// · 홈 모드: 벽면 후퇴 → ready → 홈 (종전 경로)
    /// · 홈 2 모드: 벽면 후퇴 → 상부 면 검사 준비(티칭돼 있고 홈 2 보다 가까울 때) → 홈 2. 홈으로는 가지 않는다.
    /// 경유점(ready·홈 2·상부 면 검사 준비) 위에 있을 때는 벽 앞이 아니므로 벽면 후퇴를 하지 않는다.
    /// 지나온 동작 이름 목록을 돌려준다(이미 기준 자세면 빈 목록).
    /// </summary>
    public static async Task<List<string>> ReturnToBaseAsync(
        CobotService cobot, SequenceContext ctx, ILogger logger, CancellationToken ct)
    {
        var done = new List<string>();
        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        var mode = ModeOf(cur, ctx);
        var baseKey = mode == PoseMode.Home2 ? Home2Key : "home";
        if (IsAt(cur, ctx, baseKey))
        {
            ApproachPathMemory.Clear();
            return done;
        }

        var onWaypoint = IsAt(cur, ctx, "ready") || IsAt(cur, ctx, Home2Key)
                         || UpperSlotKeys().Any(k => IsAt(cur, ctx, k));
        if (!onWaypoint)
        {
            // ② 가 후퇴 평면을 따라 들어왔으면 같은 길로 나온다 — 300mm 후퇴 후 경유점으로 바로 MoveJ 하면, 면을 따라
            // 멀리 내려간 자세(챔퍼 아래쪽 끝 등)에서 팔이 면 쪽으로 휘둘린다(실기 2026-11-02 13:57 L4-R03C09 #3 복귀 EMO).
            if (await RetracePlanarAsync(cobot, ctx, logger, ct) is { } retraced)
            {
                done.Add(retraced);
                cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
            }
            else if (await RetractFromWallAsync(cobot, ctx, logger, ct))
            {
                done.Add("벽면 후퇴");
                cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
            }
        }

        if (mode == PoseMode.Home2)
        {
            if (UpperViaSlot(cur, ctx, LastUpperWall) is { } slot)
            {
                await MoveJToTaughtAsync(cobot, ctx, slot.Key, $"검사 준비({slot.Code})", ct);
                done.Add($"검사 준비({slot.Code}) 경유");
            }
            await MoveJToTaughtAsync(cobot, ctx, Home2Key, "홈 위치_2", ct);
            done.Add("홈 위치_2 복귀");
            ApproachPathMemory.Clear();
            logger.LogInformation("기준 자세 복귀(홈 위치_2 모드): {Route}", string.Join(" → ", done));
            return done;
        }

        if (await ReturnViaReadyAsync(cobot, ctx, logger, ct)) done.Add("작업 준비 위치 경유");
        if (await EnsureCobotAtHomeAsync(cobot, ctx, ct)) done.Add("홈 복귀");
        ApproachPathMemory.Clear();
        return done;
    }

    /// <summary>복귀 경로 재추적 — 기록된 면 이격 평면보다 이만큼 더 벽 쪽(뒤)이면 그 면 앞이 아니라고 본다 [mm].</summary>
    internal const double RetraceMaxBehindMm = 50.0;

    /// <summary>후퇴 평면보다 이만큼 넘게 바깥이면 그 면 앞이 아니라고(기록이 오래됨) 본다 [mm].</summary>
    internal const double RetraceMaxAheadMm = 400.0;

    /// <summary>복귀 경로 재추적 계획 — 위치만(BASE mm). 자세는 현재 자세 그대로 유지한다.</summary>
    /// <param name="Lift">면 법선 방향으로 후퇴 평면까지 물러난 점. 이미 후퇴 평면 이상이면 null.</param>
    /// <param name="Entry">돌아갈 진입점 — 현재가 후퇴 평면보다 바깥이면 그 높이로 옮긴 진입점.</param>
    /// <param name="InPlaneMm">면 방향 이동 거리.</param>
    internal sealed record RetracePlan(double[]? Lift, double[] Entry, double InPlaneMm);

    /// <summary>
    /// 현재 TCP 위치 <paramref name="cur"/> 에서 ② 기록 <paramref name="rec"/> 의 길을 거슬러 가는 계획.
    /// 현재가 그 면 앞(면 이격 평면 뒤 <see cref="RetraceMaxBehindMm"/> ~ 후퇴 평면 앞 <see cref="RetraceMaxAheadMm"/>)이
    /// 아니면 null — 기록이 지금 자세와 무관하다.
    /// </summary>
    internal static RetracePlan? PlanRetrace(double[] cur, PlanarApproachRecord rec)
    {
        var n = rec.WallOut;
        var h = 0.0;
        for (var i = 0; i < 3; i++) h += (cur[i] - rec.ApproachPoint[i]) * n[i];
        if (h < -RetraceMaxBehindMm || h > rec.RetreatMm + RetraceMaxAheadMm) return null;

        double[]? lift = null;
        var level = rec.RetreatMm;
        if (h < rec.RetreatMm - 1.0)
            lift = Enumerable.Range(0, 3).Select(i => cur[i] + (rec.RetreatMm - h) * n[i]).ToArray();
        else
            level = h;   // 이미 후퇴 평면보다 바깥 — 그 높이 그대로 면을 따라 간다(벽 쪽으로 다시 들어가지 않는다)

        var start = lift ?? cur.Take(3).ToArray();
        var entry = Enumerable.Range(0, 3).Select(i => rec.Entry[i] + (level - rec.RetreatMm) * n[i]).ToArray();
        var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (entry[i] - start[i]) * (entry[i] - start[i])));
        // 면을 따라 이렇게 멀리 가야 한다면 기록이 지금 상황(AMR 이동·수동 조그 뒤)과 무관하다 — 따라가지 않는다.
        if (d > RetraceMaxInPlaneMm) return null;
        return new RetracePlan(lift, entry, d);
    }

    /// <summary>
    /// ② 평면 경유 접근 기록(<see cref="ApproachPathMemory"/>)이 있고 현재 TCP 가 그 면 앞이면, 면 법선 방향으로 후퇴
    /// 평면까지 물러난 뒤(MoveL) 후퇴 평면을 따라 진입점까지(MoveL) 돌아온다. 자세는 현재 자세 그대로.
    /// 움직이기 전에 경로 전체를 역기구학으로 검증하고, 걸리면 <b>움직이지 않고 예외</b> — 그 자리에서 경유점으로
    /// MoveJ 하는 종전 경로가 바로 위험한 동작이기 때문이다. 기록이 없거나 지금 자세와 무관하면 null(종전 벽면 후퇴).
    /// </summary>
    /// <summary>② 평면 경유 기록을 거슬러 갈 때 면 방향 이동의 상한 [mm] — 코봇 리치(~1.85m)를 넘는 거리는 오래된 기록으로 본다.</summary>
    internal const double RetraceMaxInPlaneMm = 1800.0;

    private static async Task<string?> RetracePlanarAsync(
        CobotService cobot, SequenceContext ctx, ILogger logger, CancellationToken ct)
    {
        if (ApproachPathMemory.Last is not { } rec) return null;

        var pose = await cobot.Rpc.GetTcpPoseInBaseAsync(ctx.Tool, ct);
        if (PlanRetrace(pose, rec) is not { } plan)
        {
            logger.LogInformation("복귀: ② 평면 경유 기록이 현재 자세와 무관(그 면 앞이 아님) — 기록 폐기, 종전 벽면 후퇴");
            ApproachPathMemory.Clear();
            return null;
        }

        var skipPlanar = plan.InPlaneMm < CobotInspectionMoveStep.PlanarSkipMm;
        if (plan.Lift is null && skipPlanar)
            return "후퇴 평면 진입점(이미 도착)";

        double[] Pose(double[] p) => new[] { p[0], p[1], p[2], pose[3], pose[4], pose[5] };
        string Fmt(double[] v) => string.Join(", ", v.Select(x => x.ToString("0")));

        // ── 경로 검증(모션 없음) ─────────────────────────────────────────
        var curJoints = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        var start = pose.Take(3).ToArray();
        var path = new List<(double[] Pt, bool Planar)>();
        if (plan.Lift is { } lift)
            path.AddRange(CobotInspectionMoveStep.SampleSegment(start, lift, CobotInspectionMoveStep.PlanarSampleMm).Select(p => (p, false)));
        if (!skipPlanar)
            path.AddRange(CobotInspectionMoveStep.SampleSegment(plan.Lift ?? start, plan.Entry, CobotInspectionMoveStep.PlanarSampleMm)
                .Select(p => (p, true)));

        var seed = curJoints;
        double[]? jLift = null, jEntry = null;
        foreach (var (pt, planar) in path)
        {
            double[] j;
            try { j = await cobot.Rpc.GetInverseKinNearestAsync(Pose(pt), seed, ctx.Tool, 0, ct); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"복귀 경로 [{Fmt(pt)}] 도달 불가 — 움직이지 않고 정지합니다. 조그로 면에서 빼낸 뒤 재시도하세요. ({ex.Message})");
            }
            if (CobotInspectionMoveStep.PathStepBlocker(seed, j, curJoints) is { } why)
                throw new InvalidOperationException(
                    $"복귀 경로 [{Fmt(pt)}] {why} — 움직이지 않고 정지합니다. 조그로 면에서 빼낸 뒤 재시도하세요.");
            seed = j;
            if (planar) jEntry = j; else jLift = j;
        }

        logger.LogInformation(
            "복귀: ② 평면 경유 역순 — 현재 [{Cur}] → 법선 후퇴 [{Lift}] → 면 방향 {D:0}mm → 진입점 [{Entry}] (면 {Wall}, 경로 검증 {N}점 통과)",
            Fmt(start), plan.Lift is null ? "생략" : Fmt(plan.Lift), skipPlanar ? 0 : plan.InPlaneMm, Fmt(plan.Entry),
            rec.WallCode ?? "-", path.Count);

        // ── 실행 ─────────────────────────────────────────────────────────
        if (plan.Lift is { } l)
        {
            var rc = await cobot.Rpc.MoveLAsync(Pose(l), jLift!, tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
            if (rc != 0)
                throw new InvalidOperationException($"복귀 법선 후퇴(MoveL) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — 정지합니다.");
        }
        if (!skipPlanar)
        {
            var rc = await cobot.Rpc.MoveLAsync(Pose(plan.Entry), jEntry!, tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
            if (rc != 0)
                throw new InvalidOperationException($"복귀 면 방향 이동(MoveL) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — 정지합니다.");
        }

        return skipPlanar
            ? "법선 후퇴(후퇴 평면)"
            : $"법선 후퇴 → 후퇴 평면 {plan.InPlaneMm:0}mm 역주행(진입점)";
    }

    private static IEnumerable<string> UpperSlotKeys()
        => WallCodes.All.Where(w => IsUpperRoute(w.Code)).Select(w => TeachingService.WallSlotKey(w.Code));

    private static string? _lastUpperWall;

    /// <summary>마지막으로 홈 위치_2 모드로 진입한 상부 면(T/PU/SU) — 면 정보 없이 호출되는 복귀(주행 전 복귀 등)가
    /// 그 면의 검사 준비 슬롯을 거슬러 가도록 기억한다. 다른 면의 슬롯을 빌려 쓰지 않기 위함.</summary>
    internal static string? LastUpperWall => Volatile.Read(ref _lastUpperWall);

    /// <summary>홈 2 모드 복귀 때 경유할 상부 면 검사 준비 슬롯 — 진입 경로(홈 2 → 검사 준비 → 접근점)를 거슬러 간다.
    /// 면은 context 의 wall_code(상부 면일 때), 없으면(주행 전 복귀) 마지막으로 진입한 상부 면(<paramref name="lastUpperWall"/>).
    /// <b>그 면의 슬롯만</b> 쓴다 — 비어 있으면(미티칭) 다른 면(T·SU 등)의 슬롯으로 대신하지 않고 바로 홈 2 로 간다
    /// (2026-11-06 현장: PU 시험 뒤 주행 전 복귀가 가장 가까운 검사 준비 T 를 경유). 면을 모르면 경유 없이 홈 2.
    /// 그 슬롯이 홈 2 보다 가까울 때만 경유한다 — 이미 홈 2 근처면 바로 홈 2 로 간다.</summary>
    internal static (string Key, string Code)? UpperViaSlot(double[] cur, SequenceContext ctx, string? lastUpperWall = null)
    {
        if (TaughtJoints(ctx, Home2Key) is not { } home2) return null;
        var code = IsUpperRoute(ctx.WallCode) ? ctx.WallCode!
            : IsUpperRoute(lastUpperWall) ? lastUpperWall!
            : null;
        if (code is null) return null;

        var key = TeachingService.WallSlotKey(code);
        if (TaughtJoints(ctx, key) is not { } j) return null;
        return JointDistance(cur, j) < JointDistance(cur, home2) ? (key, code) : null;
    }

    /// <summary>관절 거리 — 축별 차이(±360° 감기 정규화)의 최댓값 [도].</summary>
    internal static double JointDistance(double[] a, double[] b)
    {
        var max = 0.0;
        for (var i = 0; i < 6; i++)
        {
            var d = (a[i] - b[i]) % 360.0;
            if (d > 180.0) d -= 360.0;
            else if (d < -180.0) d += 360.0;
            max = Math.Max(max, Math.Abs(d));
        }
        return max;
    }

    private static double[]? TaughtJoints(SequenceContext ctx, string key)
        => ctx.Positions.TryGetValue(key, out var p) && p.IsTaught && p.J1.HasValue
            ? new[] { p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value }
            : null;

    internal static bool IsAt(double[] cur, SequenceContext ctx, string key)
        => TaughtJoints(ctx, key) is { } j && IsWithinJointTolerance(cur, j);

    /// <summary>현재 관절각이 현재 자세 모드의 기준 홈(홈 모드=home, 홈 2 모드=home2)에 있는지 판정한다.</summary>
    internal static bool IsAtBaseHome(double[] cur, SequenceContext ctx, out string baseKey, out string baseName)
    {
        var mode = ModeOf(cur, ctx);
        baseKey = mode == PoseMode.Home2 ? Home2Key : "home";
        baseName = mode == PoseMode.Home2 ? "홈 위치_2" : "홈 위치";
        return IsAt(cur, ctx, baseKey);
    }

    /// <summary>티칭 위치(BASE)로 MoveJ — 현재 관절 쪽으로 감기를 맞춘다. 이미 그 자리면 움직이지 않는다.</summary>
    private static async Task MoveJToTaughtAsync(
        CobotService cobot, SequenceContext ctx, string key, string name, CancellationToken ct)
    {
        var p = ctx.Positions[key];
        var joints = TaughtJoints(ctx, key)
            ?? throw new InvalidOperationException($"{name} 미티칭 — Teaching에서 먼저 저장하세요.");
        var cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        if (IsWithinJointTolerance(cur, joints)) return;

        var pose = new[] { p.X!.Value, p.Y!.Value, p.Z!.Value, p.Rx!.Value, p.Ry!.Value, p.Rz!.Value };
        var rc = await cobot.Rpc.MoveJAsync(FairinoRpcClient.UnwrapToward(joints, cur), pose,
            tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException($"{name} 이동(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
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
            var joints = await ComputedReadyJointsAsync(cobot, ctx, readyPose, cur, ct);
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
            var joints = await ComputedReadyJointsAsync(cobot, ctx, readyPose, cur, ct);
            rc = await cobot.Rpc.MoveJAsync(joints, readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }

        if (rc != 0)
            throw new InvalidOperationException(
                $"작업 준비 위치 경유 이동(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        logger.LogInformation("홈 복귀 전 작업 준비 위치 경유 완료 ({Src})", src);
        return true;
    }

    /// <summary>
    /// ready 미티칭(홈에서 툴 Z 자동 계산) 자세의 MoveJ 관절 해. 컨트롤러 자동 IK(config −1)는 J6 를 ±180° 범위로
    /// 돌려주므로 그대로 MoveJ 하면 현재 J6 −181° 에 해 +179° 처럼 같은 자세에서도 손목이 한 바퀴 돈다.
    /// 그래서 현재 관절 쪽으로 감아 푼 최소 변위 해를 쓰고, 그 해가 J6 안전 범위(현재 감김 기준) 밖이면
    /// 움직이지 않고 실패한다 — 티칭된 ready 경로(<see cref="FairinoRpcClient.UnwrapToward"/>)와 같은 보호.
    /// </summary>
    private static async Task<double[]> ComputedReadyJointsAsync(
        CobotService cobot, SequenceContext ctx, double[] readyPose, double[] cur, CancellationToken ct)
    {
        var joints = await cobot.Rpc.GetInverseKinNearestAsync(readyPose, cur, ctx.Tool, 0, ct);
        if (Inspection.WObjAttitude.J6RangeBlocker(cur[5], joints[5]) is { } j6)
            throw new InvalidOperationException(
                $"작업 준비 위치(홈 기준 자동 계산) {j6} — 움직이지 않았습니다. Teaching에서 작업 준비 위치를 저장하세요.");
        return joints;
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

    /// <summary>벽면 후퇴 계획. <paramref name="Horizontal"/> 이면 BASE 오프셋(<paramref name="OffsetBase"/>)으로,
    /// 아니면 툴 오프셋(<paramref name="ToolOffset"/> — 광축 반대 방향)으로 움직인다. <paramref name="Blocker"/> 가 null 이 아니면 움직이면 안 된다.</summary>
    internal sealed record RetractPlan(bool Horizontal, double[] OffsetBase, double[] EndTcp, string? Blocker, double[] ToolOffset);

    /// <summary>
    /// 현재 TCP(BASE)에서의 벽면 후퇴를 계획한다 — 움직이기 전에 끝점을 정한다.
    /// · 툴 Z+ 가 아래를 향하고 수평 성분이 충분하면(상부 챔퍼처럼 광축이 위·바깥을 보는 자세) 툴 Z+ 를
    ///   BASE 수평면에 투영한 방향으로 물러난다 — 높이는 유지하고 벽에서만 떨어진다. 툴 Z+ 그대로면 그만큼
    ///   AMR 쪽으로 내려가 팔이 접힌다(2026-10-30 SU: z 229→17mm).
    /// · 그 외(옆벽처럼 광축이 수평, 천장처럼 거의 연직)는 종전대로 툴 Z+.
    /// · 끝점이 내려가면서 <see cref="RetractMinDescendEndZMm"/> 아래로 가면 Blocker.
    /// </summary>
    internal static RetractPlan PlanRetract(double[] tcpPose, double distanceMm = RetractDistanceMm,
        double minDescendEndZMm = RetractMinDescendEndZMm, ToolAxisDir opticalAxis = ToolAxisDir.MinusZ)
    {
        // 후퇴 방향 = 광축(카메라가 면을 바라보는 툴축)의 반대. 현장 광축 −Z 면 툴 +Z(종전 동작과 같다).
        var m = FrameMath.PoseToMatrix(tcpPose);
        int col = (int)opticalAxis / 2;
        double away = (int)opticalAxis % 2 == 0 ? -1.0 : 1.0;   // +축 광축이면 −, −축 광축이면 + 방향으로 물러난다
        double zx = away * m[0, col], zy = away * m[1, col], zz = away * m[2, col];
        var h = Math.Sqrt(zx * zx + zy * zy);

        var horizontal = zz < RetractDescendDirZ && h >= RetractMinHorizontalFraction;
        var dir = horizontal ? new[] { zx / h, zy / h, 0.0 } : new[] { zx, zy, zz };
        var offset = new[] { dir[0] * distanceMm, dir[1] * distanceMm, dir[2] * distanceMm, 0.0, 0.0, 0.0 };
        var end = new[] { tcpPose[0] + offset[0], tcpPose[1] + offset[1], tcpPose[2] + offset[2] };

        string? blocker = null;
        if (offset[2] < -1.0 && end[2] < minDescendEndZMm)
            blocker = $"벽면 후퇴 끝점이 너무 낮음 — z {tcpPose[2]:0}→{end[2]:0}mm (최저 {minDescendEndZMm:0}mm). " +
                      "후퇴 방향이 AMR 쪽으로 내려가 팔이 접힙니다. 움직이지 않고 정지 — 펜던트/조그로 벽에서 떼어 낸 뒤 재시도하세요.";
        var toolOffset = new double[6];
        toolOffset[col] = away * distanceMm;
        return new RetractPlan(horizontal, offset, end, blocker, toolOffset);
    }

    /// <summary>현재 위치에서 벽면 후퇴(MoveL) — 방향·끝점은 <see cref="PlanRetract"/>. 벽에서 떨어져 MoveJ 경로에
    /// 차체 충돌 여유를 확보한다. 성공하면 true.
    /// 끝점이 위험하거나 후퇴 이동이 실패(rc≠0·예외·현재 포즈 조회 실패)하면 <b>더 움직이지 않고 예외</b> —
    /// false 를 돌려주면 호출측이 벽 앞 자세 그대로 경유점으로 MoveJ 해 팔이 벽 쪽으로 휘둘린다(실기 EMO 와 같은 동작).</summary>
    public static async Task<bool> RetractFromWallAsync(
        CobotService cobot, SequenceContext ctx, ILogger logger, CancellationToken ct)
    {
        double[] currentPose;
        try
        {
            currentPose = await cobot.Rpc.GetTcpPoseInBaseAsync(ctx.Tool, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "벽면 후퇴 전 현재 포즈 조회 실패 — 움직이지 않고 정지");
            throw new InvalidOperationException(
                $"벽면 후퇴 불가 — 현재 TCP 포즈를 읽지 못했습니다({ex.Message}). 벽 앞 자세에서 바로 관절 이동하면 " +
                "팔이 벽 쪽으로 휘둘릴 수 있어 정지합니다. 펜던트/조그로 벽에서 떼어 낸 뒤 재시도하세요.", ex);
        }

        if (ctx.OpticalAxis is not { } opticalAxis)
            throw new InvalidOperationException(
                $"벽면 후퇴 불가 — '{WeldSequenceSupport.DepthAxisKey}' 광축 파라미터가 저장돼 있지 않아 벽에서 멀어지는 방향을 " +
                "알 수 없습니다. 움직이지 않고 정지합니다. 카메라 페이지에서 광축을 설정·저장하거나 펜던트로 벽에서 떼어 내세요.");

        var plan = PlanRetract(currentPose, opticalAxis: opticalAxis);
        var poseText = string.Join(", ", currentPose.Select(v => v.ToString("0.#")));
        var endText = string.Join(", ", plan.EndTcp.Select(v => v.ToString("0.#")));
        if (plan.Blocker is not null)
        {
            logger.LogWarning("벽면 후퇴 중단: 현재=[{Pose}] → 끝점=[{End}] — {Msg}", poseText, endText, plan.Blocker);
            throw new InvalidOperationException(plan.Blocker);
        }

        try
        {
            int rc;
            if (plan.Horizontal)
            {
                logger.LogInformation("벽면 직선 후퇴 시작: 현재=[{Pose}], 수평 {Dist}mm(툴Z+ 가 아래를 향함 — 높이 유지) → 끝점=[{End}]",
                    poseText, RetractDistanceMm, endText);
                rc = await cobot.Rpc.MoveByOffsetAsync(
                    currentPose, user: 0, plan.OffsetBase, tool: ctx.Tool, vel: ctx.Velocity, ct: ct);
            }
            else
            {
                logger.LogInformation("벽면 직선 후퇴 시작: 현재=[{Pose}], 광축({Axis}) 반대로 {Dist}mm → 끝점=[{End}]",
                    poseText, opticalAxis, RetractDistanceMm, endText);
                rc = await cobot.Rpc.MoveByToolOffsetAsync(
                    currentPose, user: 0, plan.ToolOffset, tool: ctx.Tool, vel: ctx.Velocity, ct: ct);
            }

            if (rc != 0)
            {
                logger.LogWarning("벽면 후퇴 실패 (rc={Rc}){Desc} — 움직이지 않고 정지",
                    rc, FairinoErrorCodes.Suffix(rc));
                throw new InvalidOperationException(RetractFailedMessage($"rc={rc}{FairinoErrorCodes.Suffix(rc)}"));
            }

            logger.LogInformation("벽면 후퇴 완료 ({Dist}mm{Mode})", RetractDistanceMm, plan.Horizontal ? ", 수평" : "");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "벽면 후퇴 중 예외 — 움직이지 않고 정지");
            throw new InvalidOperationException(RetractFailedMessage(ex.Message), ex);
        }
    }

    internal static string RetractFailedMessage(string cause)
        => $"벽면 후퇴 실패({cause}) — 벽 앞 자세에서 바로 관절 이동하면 팔이 벽 쪽으로 휘둘릴 수 있어 정지합니다. " +
           "펜던트/조그로 벽에서 떼어 낸 뒤 재시도하세요.";

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
