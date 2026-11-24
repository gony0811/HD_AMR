using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Motion;
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
///  ④ <b>자세 모드</b> — 상부 면(T/PU/SU)은 "홈 위치_2", 바닥·하부 챔퍼(B/PL/SL)는 "홈 위치_3"(티칭돼 있을 때),
///     그 외 면은 홈 모드에서만 진입한다. 모드 전환은 운영자 수동 전용이라 모드가 맞지 않으면 움직이지 않고
///     실패한다(<see cref="ModeMismatch"/>).
///
/// 부르는 곳은 ② <see cref="CobotInspectionMoveStep"/>(LINE/CROSS)이다. 같은 anchor 그룹의 연속 task 는
/// 직전 종료 자세가 그대로면 ②가 이 진입 준비를 건너뛰고 면을 따라 다음 시작점으로 바로 간다(액션 사이 복귀 금지).
/// </summary>
internal static class SequenceEntry
{
    /// <summary>홈 도달 판정 관절 허용오차 [도].</summary>
    private const double HomeToleranceDeg = 0.5;

    /// <summary>
    /// 경유점 사이 이동 경로(관절 공간 직선) 위로 보는 관절 허용 편차 [도]. 홈 ↔ 작업 준비 위치처럼 MoveJ 로 오가는 구간에서
    /// 정지·취소로 도중에 멈춘 팔은 벽 앞이 아니다 — 여기서 벽면 후퇴를 하면 팔을 차체 쪽으로 접어 넣는다
    /// (실기 2026-11-16 10:29: 홈 18mm 앞에서 멈춘 팔을 300mm 후퇴시켜 전완이 리프트 기둥에 충돌).
    /// </summary>
    internal const double RouteToleranceDeg = 3.0;

    /// <summary>이동 후 도착으로 보는 관절 허용 편차 [도] — 이보다 멀리 멈췄으면 그 이동은 끝나지 않은 것이다.</summary>
    internal const double ArrivalToleranceDeg = 1.0;

    /// <summary>벽면 후퇴 경로의 자기 간섭 여유 [mm] — 경로 위 자세가 차체·리프트 기둥·신호탑에 이보다 가까워지면서
    /// 출발 자세보다 여유가 줄면 후퇴하지 않는다. 닿는 자세(여유 음수)는 항상 막는다.</summary>
    internal const double RetractSelfMarginMm = 30.0;

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

    /// <summary>최저 높이 판정을 적용하는 최소 하강량 [mm]. 후퇴가 이만큼 넘게 내려갈 때만 끝점 높이를 본다.
    /// 옆벽처럼 광축이 거의 수평이면 300mm 후퇴에 몇 mm 만 내려가 팔이 접히지 않는데, 낮은 용접선(시작 z 가
    /// 이미 최저 높이 아래)에서는 1mm 만 내려가도 막혀 홈 복귀가 안 됐다(2026-11-16 L3-R01C15: z −209→−220mm,
    /// 2026-11-15 z −544→−546mm).</summary>
    internal const double RetractMinBlockingDescentMm = 30.0;

    /// <summary>홈 티칭 여부 — 진입 준비를 하는 스텝의 Validate 에서 부른다.</summary>
    public static StepValidation ValidateHome(SequenceContext context)
        => context.Positions.TryGetValue("home", out var home) && home.IsTaught
            ? StepValidation.Ok()
            : StepValidation.Fail("홈 위치 미티칭 — Teaching에서 먼저 저장하세요.");

    /// <summary>상부 자세 모드의 기준 자세 키. 홈 ↔ 홈 2 전환(ready → 홈 1.5 → 홈 1.6 경유)은 벽 충돌 위험이 커서
    /// 자동 운전에서는 하지 않고 운영자가 Teaching 에서 수동으로만 한다(2026-10-29 운영안).</summary>
    internal const string Home2Key = "home2";

    /// <summary>바닥·하부 챔퍼(B/PL/SL) 자세 모드의 기준 자세 키(2026-11-17 리프트 탈거 후 운영안). 홈 위치_2 처럼
    /// 자동 운전은 이 모드로 바꾸지 않고, 운영자가 Teaching 에서 '이동'으로 옮겨 둔다.</summary>
    internal const string Home3Key = "home3";

    /// <summary>바닥(B) 전용 자세 모드의 기준 자세 키(2026-11-17) — 티칭돼 있으면 B 는 홈 위치_4, PL·SL 은 홈 위치_3 을 쓴다.
    /// 미티칭이면 B 도 종전처럼 홈 위치_3 을 쓴다.</summary>
    internal const string Home4Key = "home4";

    /// <summary>자세 모드 — 로봇이 지금 어느 기준 자세 쪽에 있는지. 자동 운전은 모드를 바꾸지 않는다.</summary>
    internal enum PoseMode { Home, Home2, Home3, Home4 }

    private static readonly PoseMode[] SideModes = { PoseMode.Home2, PoseMode.Home3, PoseMode.Home4 };

    /// <summary>홈 위치_2 모드가 필요한 면 — 천장(T)·상부 챔퍼(PU/SU). 홈 위치 쪽에서는 벽 충돌·역기구학 오류로 도달 불가.</summary>
    internal static bool IsUpperRoute(string? wallCode)
        => WallCodes.Find(wallCode)?.Orientation is SurfaceOrientation.Ceiling or SurfaceOrientation.ChamferUpper;

    /// <summary>홈 위치_3 모드 면 — 바닥(B)·하부 챔퍼(PL/SL).</summary>
    internal static bool IsLowerRoute(string? wallCode)
        => WallCodes.Find(wallCode)?.Orientation is SurfaceOrientation.Floor or SurfaceOrientation.ChamferLower;

    /// <summary>면이 요구하는 자세 모드. 바닥(B)은 홈 위치_4 가 티칭돼 있으면 홈 위치_4, 하부 면(B·PL·SL)은 홈 위치_3 이
    /// 티칭돼 있으면 홈 위치_3, 둘 다 없으면 종전 홈 모드.</summary>
    internal static PoseMode RequiredMode(string? wallCode, bool home3Taught, bool home4Taught = false)
        => IsUpperRoute(wallCode) ? PoseMode.Home2
         : IsFloor(wallCode) && home4Taught ? PoseMode.Home4
         : IsLowerRoute(wallCode) && home3Taught ? PoseMode.Home3
         : PoseMode.Home;

    /// <summary>
    /// 면별 운영 ② 출발 관절각 — <see cref="PrepareAsync"/> 가 ② 직전에 세우는 자세: 상부 면(T/PU/SU)은 그 면 검사 준비 슬롯
    /// (미티칭이면 홈 위치_2), 하부 면(B/PL/SL)은 홈 위치_4·_3 이 티칭돼 있으면 그 면 검사 준비 슬롯(미티칭이면 그 홈), 그 외는
    /// 작업 준비 위치(ready). <paramref name="joints"/> = 키 → 티칭된 관절값(미티칭 null). 리치 맵·ACS 프로필 전송이 같은 규칙을 쓴다.
    /// </summary>
    internal static double[]? StartJointsFor(string wallCode, Func<string, double[]?> joints)
        => IsUpperRoute(wallCode)
            ? joints(TeachingService.WallSlotKey(wallCode)) ?? joints(Home2Key)
            : IsLowerRoute(wallCode)
              && ((wallCode == "B" ? joints(Home4Key) : null) ?? joints(Home3Key)) is { } lowHome
                ? joints(TeachingService.WallSlotKey(wallCode)) ?? lowHome
                : joints("ready");

    private static bool IsFloor(string? wallCode) => WallCodes.Find(wallCode)?.Orientation is SurfaceOrientation.Floor;

    /// <summary>이 컨텍스트(티칭 상태)에서 면이 요구하는 자세 모드.</summary>
    private static PoseMode RequiredMode(string? wallCode, SequenceContext ctx)
        => RequiredMode(wallCode, Home3Taught(ctx), Home4Taught(ctx));

    /// <summary>모드의 기준 자세 키.</summary>
    internal static string BaseKeyOf(PoseMode mode) => mode switch
    {
        PoseMode.Home2 => Home2Key,
        PoseMode.Home3 => Home3Key,
        PoseMode.Home4 => Home4Key,
        _ => "home",
    };

    /// <summary>모드의 기준 자세 이름.</summary>
    internal static string BaseNameOf(PoseMode mode) => mode switch
    {
        PoseMode.Home2 => "홈 위치_2",
        PoseMode.Home3 => "홈 위치_3",
        PoseMode.Home4 => "홈 위치_4",
        _ => "홈 위치",
    };

    /// <summary>면이 이 모드(홈 위치_2·_3·_4)의 면인지 — 그 면의 검사 준비 슬롯을 모드 경유점으로 쓴다.
    /// 바닥(B)은 홈 위치_4 가 티칭돼 있으면 홈 위치_4, 아니면 홈 위치_3 쪽이다.</summary>
    private static bool BelongsTo(PoseMode mode, string? wallCode, SequenceContext ctx) => mode switch
    {
        PoseMode.Home2 => IsUpperRoute(wallCode),
        PoseMode.Home3 => IsLowerRoute(wallCode) && !(IsFloor(wallCode) && Home4Taught(ctx)),
        PoseMode.Home4 => IsFloor(wallCode) && Home4Taught(ctx),
        _ => false,
    };

    private static bool Home3Taught(SequenceContext ctx) => TaughtJoints(ctx, Home3Key) is not null;
    private static bool Home4Taught(SequenceContext ctx) => TaughtJoints(ctx, Home4Key) is not null;

    /// <summary>진입 경유점 티칭 여부 — 상부 면은 홈 위치_2, 바닥은 홈 위치_4·_3, 하부 챔퍼는 홈 위치_3(있으면), 그 외는 ready.</summary>
    public static StepValidation ValidateRoute(SequenceContext context)
    {
        if (IsUpperRoute(context.WallCode))
            return TaughtJoints(context, Home2Key) is not null
                ? StepValidation.Ok()
                : StepValidation.Fail(
                    $"홈 위치_2 미티칭 — 상부 면({context.WallCode})은 홈 위치_2 자세에서 진입합니다. Teaching에서 먼저 저장하세요.");
        if (RequiredMode(context.WallCode, context) is PoseMode.Home3 or PoseMode.Home4)
            return StepValidation.Ok();

        return context.Positions.TryGetValue("ready", out var ready) && ready.IsTaught
            ? StepValidation.Ok()
            : StepValidation.Fail("작업 준비 위치(ready) 미티칭 — Teaching에서 먼저 저장하세요.");
    }

    /// <summary>현재 자세 모드 — 홈 위치_2 가 티칭돼 있고, 홈 위치_2 까지의 관절 거리가 ready(미티칭이면 홈)까지보다
    /// 짧으면 <see cref="PoseMode.Home2"/>. 홈 위치_3 도 같은 규칙으로 비교한다(가장 가까운 기준 자세, 같으면 홈 모드).
    /// 검사 도중 멈춘 자세도 같은 규칙으로 가까운 쪽에 붙는다.</summary>
    internal static PoseMode ModeOf(double[] cur, SequenceContext ctx)
    {
        var normal = TaughtJoints(ctx, "ready") ?? TaughtJoints(ctx, "home");
        var mode = PoseMode.Home;
        var best = normal is null ? double.PositiveInfinity : JointDistance(cur, normal);
        foreach (var m in SideModes)
        {
            if (TaughtJoints(ctx, BaseKeyOf(m)) is not { } j) continue;
            var d = JointDistance(cur, j);
            if (d < best) { best = d; mode = m; }
        }
        return mode;
    }

    private static int _rememberedMode = -1;   // −1 = 모름

    /// <summary>마지막으로 확인된 자세 모드 — 기준 자세·경유점 위에 있었거나 진입 준비가 통과한 모드. 앱을 다시 띄우면 모른다(null).</summary>
    internal static PoseMode? RememberedMode
        => Volatile.Read(ref _rememberedMode) is var m && m >= 0 ? (PoseMode)m : null;

    internal static void RememberMode(PoseMode mode) => Volatile.Write(ref _rememberedMode, (int)mode);

    /// <summary>테스트용 — 기억을 지운다.</summary>
    internal static void ForgetMode() => Volatile.Write(ref _rememberedMode, -1);

    /// <summary>
    /// 지금 따를 자세 모드. 기준 자세·경유점 위에 있으면 그 자리가 정한다. 벽 앞에 뻗은 채(검사 도중 멈춘 자세)면
    /// <b>시작할 때의 모드</b>(<paramref name="remembered"/>)를 따른다 — 관절 거리로 가까운 쪽을 고르면, 천장에서 면을 따라
    /// 멀리 뻗은 자세가 홈 위치 쪽으로 판정돼 홈 위치_2 로 시작한 검사가 홈 위치로 돌아갔다(실기 2026-11-13 05:01 T L4-R10C11).
    /// 기억이 없으면(앱 재시작 직후) 종전처럼 가까운 쪽.
    /// </summary>
    internal static PoseMode PickMode(bool atHome2Side, bool atHomeSide, PoseMode nearest, PoseMode? remembered)
        => PickMode(atHome2Side ? PoseMode.Home2 : atHomeSide ? PoseMode.Home : null, nearest, remembered);

    /// <summary><paramref name="atSide"/> = 기준 자세·경유점 위에 있으면 그 모드(아니면 null).</summary>
    internal static PoseMode PickMode(PoseMode? atSide, PoseMode nearest, PoseMode? remembered)
        => atSide ?? remembered ?? nearest;

    /// <summary>현재 관절각에서 따를 자세 모드. 기준 자세·경유점 위면 그 모드를 기억해 둔다.</summary>
    internal static PoseMode ResolveMode(double[] cur, SequenceContext ctx)
    {
        PoseMode? atSide = null;
        foreach (var m in SideModes)
        {
            if (TaughtJoints(ctx, BaseKeyOf(m)) is null) continue;
            if (IsAt(cur, ctx, BaseKeyOf(m)) || SlotKeysOf(m, ctx).Any(k => IsAt(cur, ctx, k))) { atSide = m; break; }
        }
        if (atSide is null && (IsAt(cur, ctx, "home") || IsAt(cur, ctx, "ready"))) atSide = PoseMode.Home;
        var mode = PickMode(atSide, ModeOf(cur, ctx), RememberedMode);
        if (atSide is not null) RememberMode(mode);
        return mode;
    }

    /// <summary>작업 면과 자세 모드가 맞지 않을 때의 실패 사유. 맞으면 null.
    /// 자동 운전은 모드를 바꾸지 않으므로(홈 1.5·1.6 경유 금지) 운영자 수동 전환이 필요하다.</summary>
    /// <param name="home3Taught">홈 위치_3 티칭 여부 — 미티칭이면 하부 챔퍼도 홈 모드로 본다.</param>
    /// <param name="home4Taught">홈 위치_4 티칭 여부 — 미티칭이면 바닥은 홈 위치_3(또는 홈) 모드.</param>
    internal static string? ModeMismatch(PoseMode mode, string? wallCode, bool home3Taught = false, bool home4Taught = false)
    {
        var need = RequiredMode(wallCode, home3Taught, home4Taught);
        if (need == mode) return null;
        var via = need == PoseMode.Home2 && mode == PoseMode.Home ? "(작업 준비·홈 위치_1.5·1.6 자동 경유)"
            : need == PoseMode.Home && mode == PoseMode.Home2 ? "(홈 위치_1.6·1.5·작업 준비 자동 경유)"
            : "";
        return $"수동 자세 전환 필요 — {wallCode ?? "일반"} 면은 {BaseNameOf(need)} 자세에서만 검사합니다. " +
               $"현재 {BaseNameOf(mode)} 자세입니다. Teaching에서 {BaseNameOf(need)} '이동'{via}으로 전환한 뒤 재시도하세요.";
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
        var mode = ResolveMode(cur, context);
        if (ModeMismatch(mode, context.WallCode, Home3Taught(context), Home4Taught(context)) is { } mismatch)
        {
            logger.LogWarning("진입 준비 중단: {Msg}", mismatch);
            return (false, mismatch);
        }
        RememberMode(mode);   // 이 모드로 검사를 시작한다 — 도중에 멈춰도 복귀는 이 모드의 기준 자세로

        if (mode != PoseMode.Home)
        {
            Volatile.Write(ref _lastModeWall, context.WallCode);   // 이후 면 정보 없는 복귀가 이 면 슬롯만 거슬러 가도록
            var baseKey = BaseKeyOf(mode);
            var baseName = BaseNameOf(mode);
            var slotKey = TeachingService.WallSlotKey(context.WallCode!);
            if (IsAt(cur, context, slotKey) || IsAt(cur, context, baseKey) && TaughtJoints(context, slotKey) is null)
            {
                logger.LogInformation("진입 준비: 이미 {Base} 모드 경유점 — 복귀 왕복 생략", baseName);
                return (true, $" {frames}, ({baseName} 모드, 이미 경유점).");
            }
            var parts2 = new List<string> { frames, $"{baseName} 모드" };
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
    /// 자세 모드(<see cref="ResolveMode"/> — 벽 앞에 뻗은 채면 시작할 때의 모드)는 바꾸지 않는다:
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
        var mode = ResolveMode(cur, ctx);
        var baseKey = BaseKeyOf(mode);
        if (IsAt(cur, ctx, baseKey))
        {
            ApproachPathMemory.Clear();
            return done;
        }

        var onWaypoint = IsAt(cur, ctx, "ready")
                         || SideModes.Any(m => IsAt(cur, ctx, BaseKeyOf(m)) || SlotKeysOf(m, ctx).Any(k => IsAt(cur, ctx, k)));
        if (!onWaypoint && RouteSegmentOf(cur, ctx, mode) is { } route)
        {
            // 경유점 사이를 오가다 멈춘 자세 — 벽 앞이 아니므로 후퇴하지 않고 그 경로를 따라 경유점으로 돌아간다.
            logger.LogInformation("기준 자세 복귀: {Route} 이동 경로 위에서 멈춘 자세 — 벽면 후퇴 없이 경유점으로 복귀", route);
            onWaypoint = true;
        }
        // 벽 앞에서 물러났다면 그 면 평면(면 위 한 점·면에서 코봇 쪽 법선) — 다음 관절 이동이 면 쪽으로 휘는지 본다.
        (double[] Point, double[] Out)? wall = null;
        if (!onWaypoint)
        {
            // ② 가 후퇴 평면을 따라 들어왔으면 같은 길로 나온다 — 300mm 후퇴 후 경유점으로 바로 MoveJ 하면, 면을 따라
            // 멀리 내려간 자세(챔퍼 아래쪽 끝 등)에서 팔이 면 쪽으로 휘둘린다(실기 2026-11-02 13:57 L4-R03C09 #3 복귀 EMO).
            var planarRec = ApproachPathMemory.Last;
            var beforeRetract = await cobot.Rpc.GetTcpPoseInBaseAsync(ctx.Tool, ct);
            if (await RetracePlanarAsync(cobot, ctx, logger, ct) is { } retraced)
            {
                done.Add(retraced);
                cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
                if (planarRec is not null) wall = (planarRec.ApproachPoint, planarRec.WallOut);
            }
            else if (await RetractFromWallAsync(cobot, ctx, logger, ct))
            {
                done.Add("벽면 후퇴");
                cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
                if (ctx.OpticalAxis is { } axis && Unit(PlanRetract(beforeRetract, opticalAxis: axis).OffsetBase) is { } outDir)
                    wall = (beforeRetract.Take(3).ToArray(), outDir);
            }
        }

        // 다음 관절 이동 목표 — 홈 2 모드는 상부 면 검사 준비(있으면) → 기준 자세, 홈 모드는 작업 준비 → 홈.
        var viaSlot = mode != PoseMode.Home ? ModeViaSlot(cur, ctx, mode, LastModeWall) : null;
        if (wall is { } w)
        {
            var (nextKey, nextName) = viaSlot is { } vs ? (vs.Key, $"검사 준비({vs.Code})")
                : mode != PoseMode.Home ? (baseKey, BaseNameOf(mode))
                : TaughtJoints(ctx, "ready") is not null ? ("ready", "작업 준비 위치") : ("home", "홈 위치");
            if (await EnsureClearJointPathAsync(cobot, ctx, logger, w.Point, w.Out, nextKey, nextName, ct) is { } cleared)
            {
                done.Add(cleared);
                cur = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
            }
        }

        if (mode != PoseMode.Home)
        {
            if (viaSlot is { } slot)
            {
                await MoveJToTaughtAsync(cobot, ctx, slot.Key, $"검사 준비({slot.Code})", ct);
                done.Add($"검사 준비({slot.Code}) 경유");
            }
            await MoveJToTaughtAsync(cobot, ctx, baseKey, BaseNameOf(mode), ct);
            done.Add($"{BaseNameOf(mode)} 복귀");
            ApproachPathMemory.Clear();
            logger.LogInformation("기준 자세 복귀({Base} 모드): {Route}", BaseNameOf(mode), string.Join(" → ", done));
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

    /// <summary>모드(홈 위치_2·_3·_4)에 속한 면들의 검사 준비 슬롯 키. 홈 모드는 없다(ready 를 쓴다).</summary>
    private static IEnumerable<string> SlotKeysOf(PoseMode mode, SequenceContext ctx)
        => WallCodes.All.Where(w => BelongsTo(mode, w.Code, ctx)).Select(w => TeachingService.WallSlotKey(w.Code));

    private static string? _lastModeWall;

    /// <summary>마지막으로 홈 위치_2·_3 모드로 진입한 면 — 면 정보 없이 호출되는 복귀(주행 전 복귀 등)가
    /// 그 면의 검사 준비 슬롯을 거슬러 가도록 기억한다. 다른 면의 슬롯을 빌려 쓰지 않기 위함.</summary>
    internal static string? LastModeWall => Volatile.Read(ref _lastModeWall);

    /// <summary>홈 2 모드 복귀 때 경유할 상부 면 검사 준비 슬롯 — 진입 경로(홈 2 → 검사 준비 → 접근점)를 거슬러 간다.
    /// 면은 context 의 wall_code(상부 면일 때), 없으면(주행 전 복귀) 마지막으로 진입한 상부 면(<paramref name="lastUpperWall"/>).
    /// <b>그 면의 슬롯만</b> 쓴다 — 비어 있으면(미티칭) 다른 면(T·SU 등)의 슬롯으로 대신하지 않고 바로 홈 2 로 간다
    /// (2026-11-06 현장: PU 시험 뒤 주행 전 복귀가 가장 가까운 검사 준비 T 를 경유). 면을 모르면 경유 없이 홈 2.
    /// 그 슬롯이 홈 2 보다 가까울 때만 경유한다 — 이미 홈 2 근처면 바로 홈 2 로 간다.</summary>
    internal static (string Key, string Code)? UpperViaSlot(double[] cur, SequenceContext ctx, string? lastUpperWall = null)
        => ModeViaSlot(cur, ctx, PoseMode.Home2, lastUpperWall);

    /// <summary><see cref="UpperViaSlot"/> 의 일반형 — 홈 위치_2(상부 면)·홈 위치_3(하부 면) 모드 복귀 때 경유할 검사 준비 슬롯.</summary>
    internal static (string Key, string Code)? ModeViaSlot(double[] cur, SequenceContext ctx, PoseMode mode, string? lastWall = null)
    {
        if (mode == PoseMode.Home || TaughtJoints(ctx, BaseKeyOf(mode)) is not { } baseJ) return null;
        var code = BelongsTo(mode, ctx.WallCode, ctx) ? ctx.WallCode!
            : BelongsTo(mode, lastWall, ctx) ? lastWall!
            : null;
        if (code is null) return null;

        var key = TeachingService.WallSlotKey(code);
        if (TaughtJoints(ctx, key) is not { } j) return null;
        return JointDistance(cur, j) < JointDistance(cur, baseJ) ? (key, code) : null;
    }

    /// <summary>
    /// 관절 공간에서 a → b 직선(MoveJ 경로)까지의 편차 — 직선 위 가장 가까운 점과의 축별 차이 최댓값 [도].
    /// 차이는 a 기준으로 ±360° 감아 잰다. a 와 b 가 같으면 a 까지의 거리.
    /// </summary>
    internal static double JointSegmentDeviation(double[] cur, double[] a, double[] b)
    {
        static double Wrap(double d)
        {
            d %= 360.0;
            return d > 180.0 ? d - 360.0 : d < -180.0 ? d + 360.0 : d;
        }
        var ab = new double[6];
        var ac = new double[6];
        double dot = 0, len2 = 0;
        for (var i = 0; i < 6; i++)
        {
            ab[i] = Wrap(b[i] - a[i]);
            ac[i] = Wrap(cur[i] - a[i]);
            dot += ab[i] * ac[i];
            len2 += ab[i] * ab[i];
        }
        var t = len2 < 1e-9 ? 0.0 : Math.Clamp(dot / len2, 0.0, 1.0);
        var max = 0.0;
        for (var i = 0; i < 6; i++) max = Math.Max(max, Math.Abs(ac[i] - ab[i] * t));
        return max;
    }

    /// <summary>
    /// 현재 관절이 자세 모드의 경유점 사이 이동 경로 위(<see cref="RouteToleranceDeg"/> 이내)면 그 구간 이름, 아니면 null.
    /// 홈 모드 = 홈 ↔ 작업 준비 위치, 홈 2 모드 = 홈 위치_2 ↔ 상부 면 검사 준비(티칭된 것).
    /// </summary>
    internal static string? RouteSegmentOf(double[] cur, SequenceContext ctx, PoseMode mode)
    {
        if (mode == PoseMode.Home)
            return TaughtJoints(ctx, "home") is { } home && TaughtJoints(ctx, "ready") is { } ready
                   && JointSegmentDeviation(cur, home, ready) <= RouteToleranceDeg
                ? "홈 ↔ 작업 준비 위치"
                : null;

        if (TaughtJoints(ctx, BaseKeyOf(mode)) is not { } baseJ) return null;
        foreach (var key in SlotKeysOf(mode, ctx))
            if (TaughtJoints(ctx, key) is { } slot && JointSegmentDeviation(cur, baseJ, slot) <= RouteToleranceDeg)
                return $"{BaseNameOf(mode)} ↔ {(mode == PoseMode.Home2 ? "상부" : mode == PoseMode.Home4 ? "바닥" : "하부")} 면 검사 준비";
        return null;
    }

    /// <summary>
    /// 이동이 목표에 닿지 못하고 멈췄으면 사유, 닿았으면 null. MoveJ 는 정지·취소로 멈춰도 성공(rc=0)으로 돌아오므로
    /// 실제 관절로 다시 확인한다 — 안 그러면 도중에 멈춘 자세를 "이동 완료"로 기록한다(실기 2026-11-16 10:27:49).
    /// </summary>
    internal static string? ArrivalShortfall(double[] now, double[] target, string name)
    {
        var d = JointDistance(now, target);
        return d <= ArrivalToleranceDeg
            ? null
            : $"{name} 이동이 도중에 멈췄습니다 — 목표까지 관절 최대 {d:0.0}° 남음(정지·취소·결함). 팔이 경유점 사이에 있습니다.";
    }

    /// <summary>
    /// 벽면 후퇴 경로 위 자세 <paramref name="joints"/> 가 팔·검사 툴을 차체·리프트 기둥·신호탑에 닿게 하거나,
    /// <see cref="RetractSelfMarginMm"/> 안으로 출발 자세보다 더 붙게 하면 사유, 아니면 null(<see cref="CobotEnvelope"/>).
    /// 리프트 높이는 모르므로 완전 하강(0mm)으로 본다 — 차체·신호탑까지는 가장 가까운 경우다.
    /// </summary>
    internal static string? RetractSelfBlocker(double[] startJoints, double[] joints, CobotEnvelope? envelope = null)
    {
        var env = envelope ?? CobotEnvelope.Default;
        var kin = Fr5Kinematics.Fr5Wml;
        var at = env.Evaluate(kin, joints, 0, CobotObstacleZone.FallbackMountYawDeg);
        if (at.Collides) return $"자기 간섭 — {at}";
        if (at.ClearanceMm >= RetractSelfMarginMm) return null;
        var start = env.Evaluate(kin, startJoints, 0, CobotObstacleZone.FallbackMountYawDeg);
        return at.ClearanceMm < start.ClearanceMm - 1.0
            ? $"자기 간섭 여유 부족 — {at} (출발 자세 {start.ClearanceMm:0}mm 에서 더 붙음, 최소 {RetractSelfMarginMm:0}mm)"
            : null;
    }

    /// <summary>MoveJ 뒤 실제 관절이 목표에 닿았는지 확인한다(잠시 기다리며 다시 읽는다). 취소됐으면 취소로, 못 닿았으면 예외.</summary>
    private static async Task ConfirmArrivedAsync(CobotService cobot, double[] target, string name, CancellationToken ct)
    {
        string? shortfall = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var now = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
            shortfall = ArrivalShortfall(now, target, name);
            if (shortfall is null) return;
            await Task.Delay(250, ct);
        }
        throw new InvalidOperationException(shortfall);
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
        var mode = ResolveMode(cur, ctx);
        baseKey = BaseKeyOf(mode);
        baseName = BaseNameOf(mode);
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
        await ConfirmArrivedAsync(cobot, joints, name, ct);
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
        await ConfirmArrivedAsync(cobot, homeJoints, "홈 위치", ct);

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
        double[] readyTarget;
        if (readyJoints is not null)
        {
            readyTarget = readyJoints;
            rc = await cobot.Rpc.MoveJAsync(FairinoRpcClient.UnwrapToward(readyJoints, cur), readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }
        else
        {
            readyTarget = await ComputedReadyJointsAsync(cobot, ctx, readyPose, cur, ct);
            rc = await cobot.Rpc.MoveJAsync(readyTarget, readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }

        if (rc != 0)
            throw new InvalidOperationException(
                $"작업 준비 위치 이동(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
        await ConfirmArrivedAsync(cobot, readyTarget, "작업 준비 위치", ct);

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
        double[] readyTarget;
        if (readyJoints is not null)
        {
            readyTarget = readyJoints;
            rc = await cobot.Rpc.MoveJAsync(FairinoRpcClient.UnwrapToward(readyJoints, cur), readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }
        else
        {
            readyTarget = await ComputedReadyJointsAsync(cobot, ctx, readyPose, cur, ct);
            rc = await cobot.Rpc.MoveJAsync(readyTarget, readyPose,
                tool: ctx.Tool, user: 0, vel: ctx.Velocity, ct: ct);
        }

        if (rc != 0)
            throw new InvalidOperationException(
                $"작업 준비 위치 경유 이동(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
        await ConfirmArrivedAsync(cobot, readyTarget, "작업 준비 위치", ct);

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
    /// · <see cref="RetractMinBlockingDescentMm"/> 넘게 내려가면서 끝점이 <see cref="RetractMinDescendEndZMm"/> 아래로 가면 Blocker.
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
        if (offset[2] < -RetractMinBlockingDescentMm && end[2] < minDescendEndZMm)
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

        // 경로 검증(모션 없음) — 직선 후퇴는 툴 자세를 유지하므로 팔이 접히며 손목이 한계까지 돌 수 있다
        // (실기 2026-11-09 09:20: 수평 후퇴 중 J4 75.8°→85° 한계 정지). 움직이기 전에 경로를 이어 풀어 본다.
        var pathBlocker = await RetractPathBlockerAsync(cobot, ctx, currentPose, plan.EndTcp, ct);
        if (pathBlocker is not null)
        {
            logger.LogWarning("벽면 후퇴 중단(경로 검증): 현재=[{Pose}] → 끝점=[{End}] — {Msg}", poseText, endText, pathBlocker);
            throw new InvalidOperationException(
                $"벽면 후퇴 경로 {pathBlocker} — 움직이지 않고 정지합니다. 펜던트/조그로 손목 여유를 확보하며 벽에서 떼어 낸 뒤 재시도하세요.");
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

    /// <summary>
    /// 벽면 후퇴 직선(현재 TCP → <paramref name="endTcp"/>, 자세 유지)을 <see cref="CobotInspectionMoveStep.PlanarSampleMm"/>
    /// 간격으로 현재 관절에서 이어 풀어, 점마다 <see cref="CobotInspectionMoveStep.PathStepBlocker"/>(관절 한계 근접·점프·
    /// 팔꿈치·손목 특이점)와 <see cref="RetractSelfBlocker"/>(차체·받침 기둥·신호탑 간섭)를 본다. 걸리면 "[점] 사유", 통과하면 null. 관절·IK 조회 실패도 사유로 돌려준다(움직이지 않는다).
    /// </summary>
    private static async Task<string?> RetractPathBlockerAsync(
        CobotService cobot, SequenceContext ctx, double[] currentPose, double[] endTcp, CancellationToken ct)
    {
        double[] curJoints;
        try { curJoints = await cobot.Rpc.GetActualJointPosAsync(ct: ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"검증 불가 — 현재 관절을 읽지 못했습니다({ex.Message})";
        }

        var seed = curJoints;
        foreach (var pt in RetractSamplePoints(currentPose, endTcp))
        {
            var at = string.Join(", ", pt.Select(v => v.ToString("0")));
            double[] j;
            try
            {
                j = await cobot.Rpc.GetInverseKinNearestAsync(
                    new[] { pt[0], pt[1], pt[2], currentPose[3], currentPose[4], currentPose[5] }, seed, ctx.Tool, 0, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"[{at}] 도달 불가({ex.Message})";
            }
            if (CobotInspectionMoveStep.PathStepBlocker(seed, j, curJoints) is { } why)
                return $"[{at}] {why}";
            if (RetractSelfBlocker(curJoints, j) is { } self)
                return $"[{at}] {self} — 여기는 벽 앞이 아니라 차체 쪽일 수 있습니다";
            seed = j;
        }
        return null;
    }

    /// <summary>복귀 관절 이동 전 면 여유 확보 — 더 물러날 거리 후보 [mm](수평). 0 = 높이만 맞춘다.</summary>
    internal static readonly double[] ClearExtraBackMm = { 0, 100, 200 };

    /// <summary>복귀 관절 이동 전 면 여유 확보 — 다음 경유점 높이 쪽으로 옮기는 간격 [mm].</summary>
    internal const double ClearHeightStepMm = 200.0;

    private static double[]? Unit(double[] v)
    {
        var n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return n > 1e-6 ? new[] { v[0] / n, v[1] / n, v[2] / n } : null;
    }

    /// <summary>
    /// 면 여유 확보 이동 후보(BASE 오프셋 [dx,dy,dz] mm) — 짧은 순. 면에서 수평으로 더 물러나기(<see cref="ClearExtraBackMm"/>)와
    /// 다음 경유점 높이(<paramref name="targetZ"/>) 쪽으로 <see cref="ClearHeightStepMm"/> 씩 옮기기(그 높이를 넘지 않음)를 조합한다.
    /// 면 법선에 수평 성분이 없으면(천장·바닥) 물러나기는 빼고 높이만 본다.
    /// </summary>
    internal static List<double[]> ClearMoveCandidates(double[] tcp, double[] wallOut, double targetZ)
    {
        var h = Math.Sqrt(wallOut[0] * wallOut[0] + wallOut[1] * wallOut[1]);
        var backs = h > 0.2 ? ClearExtraBackMm : new[] { 0.0 };
        var dzTotal = targetZ - tcp[2];
        var dzs = new List<double> { 0 };
        for (var s = ClearHeightStepMm; s < Math.Abs(dzTotal); s += ClearHeightStepMm) dzs.Add(Math.Sign(dzTotal) * s);
        if (Math.Abs(dzTotal) > 1) dzs.Add(dzTotal);
        var list = new List<(double Cost, double[] Off)>();
        foreach (var b in backs)
        foreach (var dz in dzs)
        {
            if (b == 0 && dz == 0) continue;
            var bx = h > 0.2 ? wallOut[0] / h * b : 0;
            var by = h > 0.2 ? wallOut[1] / h * b : 0;
            list.Add((b + Math.Abs(dz), new[] { bx, by, dz }));
        }
        return list.OrderBy(t => t.Cost).Select(t => t.Off).ToList();
    }

    /// <summary>목표 관절을 현재 관절 쪽으로 ±360° 감아 맞춘다(컨트롤러 MoveJ 와 같은 보간 경로를 보기 위해).</summary>
    private static double[] UnwrapNear(double[] target, double[] from)
        => target.Select((v, i) => from[i] + (((v - from[i]) % 360.0 + 540.0) % 360.0 - 180.0)).ToArray();

    /// <summary>
    /// 기준 자세 복귀의 다음 관절 이동(→ <paramref name="targetKey"/>)이 면 쪽으로 휘는지 오프라인 기구학으로 보고, 휘면 먼저 곧게 옮긴다.
    /// <para>기준: 이동 경로 내내 팔·검사 툴이 면 평면(<paramref name="wallPoint"/>, 법선 <paramref name="wallOut"/>)에서
    /// min(<see cref="CobotInspectionMoveStep.EntryPathMarginMm"/>, 출발 여유, 도착 여유) 밖에 있을 것 —
    /// ② 직접 진입 MoveJ 점검(<see cref="CobotInspectionMoveStep.JointPathBlocker"/>)과 같은 기준이다.</para>
    /// <para>통과하면 아무것도 하지 않는다(null). 안 되면 <see cref="ClearMoveCandidates"/> 를 짧은 순으로 보아, 직선 이동 경로
    /// (역기구학·손목·차체 간섭 — <see cref="RetractPathBlockerAsync"/>)와 그 뒤 관절 이동(면 여유 + 차체 간섭)이 모두 통과하는
    /// 첫 후보로 MoveL 한다. 높은 곳에서 300mm 만 물러나 낮은 경유점으로 MoveJ 하면 팔이 내려오며 면 쪽으로 휜다 —
    /// 더 물러나기보다 높이를 먼저 맞추는 편이 효과가 크다(실기 2026-11-20 19:26 SL 상부 z 1255mm → 작업 준비 EMO:
    /// 모델상 300mm 후퇴 −92mm, 800mm 후퇴 +54mm, 300mm 후퇴 + 600mm 하강 +148mm).</para>
    /// 맞는 후보가 없으면 움직이지 않고 예외 — 종전처럼 휘는 경로로 가지 않는다.
    /// </summary>
    private static async Task<string?> EnsureClearJointPathAsync(CobotService cobot, SequenceContext ctx, ILogger logger,
        double[] wallPoint, double[] wallOut, string targetKey, string targetName, CancellationToken ct)
    {
        if (TaughtJoints(ctx, targetKey) is not { } targetRaw
            || !ctx.Positions.TryGetValue(targetKey, out var tp) || tp.Z is not double targetZ)
            return null;

        var kin = Fr5Kinematics.Fr5Wml;
        var env = CobotEnvelope.Default;
        (double ClearanceMm, string Part) Wall(double[] q) => env.WallClearance(kin, q, wallPoint, wallOut);
        string? Self(double[] q)
        {
            var c = env.Evaluate(kin, q, 0, CobotObstacleZone.FallbackMountYawDeg);
            return c.Collides ? c.ToString() : null;
        }

        var curJ = await cobot.Rpc.GetActualJointPosAsync(ct: ct);
        var direct = CobotInspectionMoveStep.JointPathBlocker(curJ, UnwrapNear(targetRaw, curJ), null, Wall);
        if (direct is null) return null;

        var pose = await cobot.Rpc.GetTcpPoseInBaseAsync(ctx.Tool, ct);
        string Fmt(double[] v) => string.Join(", ", v.Select(x => x.ToString("0")));
        logger.LogWarning("기준 자세 복귀: {Target} 관절 이동이 면 쪽으로 휨({Why}) — 곧게 물러나기·높이 맞추기 후보 탐색 (현재 TCP [{Pose}], 목표 높이 {Z:0}mm)",
            targetName, direct, Fmt(pose.Take(3).ToArray()), targetZ);

        var candidates = ClearMoveCandidates(pose, wallOut, targetZ);
        var rejects = new List<string>();
        foreach (var off in candidates)
        {
            var back = Math.Sqrt(off[0] * off[0] + off[1] * off[1]);
            var label = $"뒤 {back:0}mm·{(off[2] < 0 ? "아래" : "위")} {Math.Abs(off[2]):0}mm";
            var end = new[] { pose[0] + off[0], pose[1] + off[1], pose[2] + off[2] };
            if (await RetractPathBlockerAsync(cobot, ctx, pose, end, ct) is { } pathWhy) { rejects.Add($"{label}: {pathWhy}"); continue; }

            double[] jEnd;
            try
            {
                jEnd = await cobot.Rpc.GetInverseKinNearestAsync(new[] { end[0], end[1], end[2], pose[3], pose[4], pose[5] },
                    curJ, ctx.Tool, 0, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { rejects.Add($"{label}: 도달 불가({ex.Message})"); continue; }

            if (CobotInspectionMoveStep.JointPathBlocker(jEnd, UnwrapNear(targetRaw, jEnd), Self, Wall) is { } why)
            {
                rejects.Add($"{label}: {why}");
                continue;
            }

            logger.LogInformation("기준 자세 복귀: 면 여유 확보 이동 {Label} → [{End}] (거부 {Count}건)", label, Fmt(end), rejects.Count);
            var rc = await cobot.Rpc.MoveByOffsetAsync(pose, user: 0, new[] { off[0], off[1], off[2], 0, 0, 0 },
                tool: ctx.Tool, vel: ctx.Velocity, ct: ct);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"면 여유 확보 이동({label}) 실패 rc={rc}{FairinoErrorCodes.Suffix(rc)} — 움직이지 않고 정지합니다. 펜던트/조그로 벽에서 떼어 낸 뒤 재시도하세요.");
            return $"면 여유 확보({label})";
        }

        logger.LogWarning("기준 자세 복귀: 면 여유 확보 후보 {Count}개 모두 불가 — {Rejects}", candidates.Count, string.Join(" / ", rejects.Take(4)));
        throw new InvalidOperationException(
            $"{targetName} 로 가는 관절 이동이 면 쪽으로 휩니다({direct}). 곧게 물러나기·높이 맞추기 후보 {candidates.Count}개 모두 불가 " +
            $"({string.Join(" / ", rejects.Take(3))}) — 움직이지 않고 정지합니다. 펜던트/조그로 벽에서 떼어 낸 뒤 재시도하세요.");
    }

    /// <summary>벽면 후퇴 경로 검증 점 — 출발점 제외, 끝점 포함(<see cref="CobotInspectionMoveStep.SampleSegment"/>).</summary>
    internal static List<double[]> RetractSamplePoints(double[] currentPose, double[] endTcp)
        => CobotInspectionMoveStep.SampleSegment(currentPose.Take(3).ToArray(), endTcp.Take(3).ToArray(),
            CobotInspectionMoveStep.PlanarSampleMm);

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
