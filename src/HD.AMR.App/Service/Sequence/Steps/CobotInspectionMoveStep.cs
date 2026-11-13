using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ② Cobot 검사위치 이동 — 두 단계로 정의한다.
///
///  1. <b>작업 준비 위치 이동</b> — Teaching 의 "ready"(작업 준비 위치)로 간다. <see cref="SequenceEntry"/> 의
///     진입 준비(활성 좌표계 정규화 → 필요 시 벽면 후퇴·홈 복귀 → ready)를 그대로 쓴다. 홈→검사 위치를
///     바로 MoveJ 하면 900mm 이하 영역에서 툴이 차체와 부딪힐 수 있어 ready 를 반드시 거친다.
///  2. <b>용접선 시작점 접근</b> — 해당 작업의 용접선 시작점(seamStartW, 맵 좌표)을 코봇 BASE 로 환산해
///     (<see cref="SeamBaseTransform"/>) 면 법선 반대로 <see cref="ApproachStandoffKey"/>(기본 400mm) 물러난 점으로
///     <b>툴(TCP)</b>을 MoveJ 로 보낸다. 자세는 wall_code 가 유효하면 광축이 면을 바라보는 계산 자세,
///     없으면 1단계 도착(ready) 자세를 유지한다.
///
/// 용접선 좌표를 환산할 수 없으면(측위·장착 보정·좌표 없음) 엉뚱한 곳으로 가지 않도록 실패한다 — 예전의
/// 벽별 검사 준비 티칭 위치 폴백은 쓰지 않는다. 정밀 정렬은 뒤의 ③~⑯이 카메라·레이저로 잡는다.
///
/// 경로(면 법선을 알 때): 접근점으로 한 번에 MoveJ 하지 않고 <b>뒤로 빠진 평면(면 이격 + <see cref="PlanarRetreatKey"/>)에서
/// 들어간다</b> — 진입점까지 MoveJ → (필요하면) 후퇴 평면을 따라 MoveL → 면 법선 방향 MoveL 로 접근점.
/// 진입점은 접근점 바로 바깥(들어갈 점)을 먼저 보고, 진입 MoveJ 가 크면 현재 TCP 투영점 쪽 후퇴 평면 점을 찾는다.
/// 움직이기 전에 진입 관절 이동량·경로 연속성까지 역기구학으로 검증하고, 맞는 경로가 없으면 움직이지 않는다.
/// 진입 경유점에서 먼 접근점(챔퍼 아래쪽 끝 등)을 한 번의 관절 보간으로 가면 팔이 면 쪽으로 크게
/// 휘둘렸다(실기 2026-11-02 L4-R03C09 PU #3).
/// </summary>
public class CobotInspectionMoveStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly SeamApproachResolver _seam;
    private readonly ParameterService _param;
    private readonly ILogger<CobotInspectionMoveStep> _logger;

    public CobotInspectionMoveStep(CobotService cobot, SeamApproachResolver seam, ParameterService param,
        ILogger<CobotInspectionMoveStep> logger)
    {
        _cobot = cobot;
        _seam = seam;
        _param = param;
        _logger = logger;
    }

    public string Key => "cobotInspection";
    public string DisplayName => "Cobot 검사위치 이동";
    public int DefaultOrder => 200;

    /// <summary>context.InspectionSurfaceId(0x01~0xFF)에 해당하는 티칭 위치를 찾는다 —
    /// ②의 이동 목표이자 ③(CameraAlign)/④(FlatSurfaceAlign)의 검증에도 동일 규칙 사용.
    /// 같은 SurfaceId 가 여러 행이면 표시 순서(SortOrder→Id) 첫 행. 범위 밖/0 이면 null.</summary>
    public static Data.Entities.TeachingPosition? FindBySurfaceId(SequenceContext context) =>
        context.InspectionSurfaceId is > 0x00 and <= 0xFF
            ? context.Positions.Values
                .Where(p => p.SurfaceId == context.InspectionSurfaceId)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
                .FirstOrDefault()
            : null;

    /// <summary>② 2단계 접근점이 용접선 시작점에서 물러나는 거리 [mm] 파라미터 키 — 툴(TCP) 기준, 모든 면 공통.
    /// 끝점 이동(<see cref="CobotSeamEndMoveStep"/>)도 같은 값을 써야 점1·점2 면 거리가 일치한다.</summary>
    public const string ApproachStandoffKey = "inspection.approachStandoffMm";

    /// <summary>이번 task 의 ② 시작 접근점(BASE, mm [x,y,z]) — 오케스트레이터가 anchor 캐시에 프레임과 짝으로 보관.</summary>
    public const string SeamStartBaseBagKey = "seam.startApproachBase";

    /// <summary>② 가 리치 부족으로 실패했을 때 그 수치(<see cref="Motion.ReachShortfall"/>) — 오케스트레이터가
    /// `stationReachShortfall` 로 보고한다(N23 A안).</summary>
    public const string ReachShortfallBagKey = "seam.reachShortfall";

    /// <summary>anchor 적중 시 캐시 프레임을 쓴 직전 task 의 ② 시작 접근점 — 프레임 평행 이동 기준.</summary>
    public const string AnchorStartBaseBagKey = "anchor.startApproachBase";

    /// <summary>anchor 적중 시 직전 task 종료 TCP(BASE pose) — 그대로면 복귀 없이 연속 이동.</summary>
    public const string PreviousEndTcpBagKey = "anchor.previousEndTcp";

    /// <summary>연속 이동 판정: 현재 TCP 가 직전 종료 TCP 와 이 거리·각도 이내여야 한다(그 사이 조그·정지 없음).</summary>
    internal const double ChainPoseTolMm = 5.0;
    internal const double ChainPoseTolDeg = 2.0;

    /// <summary>② 면 이격 기본값 [mm] — 파라미터가 없거나 범위 밖일 때.</summary>
    public const double DefaultApproachStandoffMm = 400.0;

    /// <summary>② 면 이격 허용 범위 [mm]. 하한은 ③ 카메라 거리 정렬이 들어갈 여지와 벽 충돌 여유.</summary>
    internal const double MinApproachStandoffMm = 200.0;
    internal const double MaxApproachStandoffMm = 1000.0;

    /// <summary>② 면 이격 [mm] — 파라미터 <see cref="ApproachStandoffKey"/>, 없거나 범위 밖이면 기본 400(경고 로그).</summary>
    public static async Task<double> GetApproachStandoffMmAsync(ParameterService param, ILogger? logger = null)
    {
        double? raw = null;
        try { raw = await param.GetDoubleAsync(ApproachStandoffKey); }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }
        var (value, ok) = ResolveApproachStandoffMm(raw);
        if (!ok)
            logger?.LogWarning("'{Key}' 값 {Raw} 이(가) 비었거나 범위({Min:0}~{Max:0}mm) 밖 — 기본 {Default:0}mm 사용",
                ApproachStandoffKey, raw?.ToString() ?? "(없음)", MinApproachStandoffMm, MaxApproachStandoffMm,
                DefaultApproachStandoffMm);
        return value;
    }

    /// <summary>
    /// 같은 anchor 그룹 연속 task 의 면 이격 [mm] 파라미터 키 — 직전 task 종료 자세(면에 붙은 검사 자세)에서 다음 시작점으로
    /// 갈 때 이 거리까지만 면에서 들어 올렸다가 옆으로 옮긴다. 예전에는 첫 진입과 같은 접근점(<see cref="ApproachStandoffKey"/>,
    /// 400mm)까지 빠졌다가 ⑱ 첫 경유점으로 다시 들어와, 이어진 LINE 사이에서 툴이 제자리에서 400mm 왕복했다
    /// (실기 2026-11-09 16:37 L4-R08C08 #2: 들어 올림 409mm → 스테이징 복귀). anchor 적중이면 ③ 카메라 거리 정렬이 없어
    /// 첫 진입 하한(<see cref="MinApproachStandoffMm"/>)의 '③ 여지'가 필요 없다.
    /// </summary>
    public const string ChainStandoffKey = "inspection.chainStandoffMm";

    /// <summary>연속 이동 면 이격 기본값 [mm] — 골판(주름) 돌출보다 충분히 크게.</summary>
    public const double DefaultChainStandoffMm = 100.0;

    /// <summary>연속 이동 면 이격 하한 [mm] — 옆으로 옮기는 동안 면(골판 돌출) 충돌 여유.</summary>
    internal const double MinChainStandoffMm = 30.0;

    /// <summary>
    /// 파라미터 원값 → 연속 이동 면 이격. 하한 이상·첫 진입 이격 이하면 그 값, 아니면 기본값(첫 진입 이격보다 크면 첫 진입 이격).
    /// </summary>
    internal static (double Value, bool Ok) ResolveChainStandoffMm(double? raw, double approachStandoffMm)
        => raw is { } v && v >= MinChainStandoffMm && v <= approachStandoffMm
            ? (v, true)
            : (Math.Min(DefaultChainStandoffMm, approachStandoffMm), raw is null);

    private async Task<double> GetChainStandoffMmAsync(double approachStandoffMm)
    {
        double? raw = null;
        try { raw = await _param.GetDoubleAsync(ChainStandoffKey); }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }
        var (value, ok) = ResolveChainStandoffMm(raw, approachStandoffMm);
        if (!ok)
            _logger.LogWarning("'{Key}' 값 {Raw} 이(가) 범위({Min:0}~{Max:0}mm) 밖 — {Value:0}mm 사용",
                ChainStandoffKey, raw, MinChainStandoffMm, approachStandoffMm, value);
        return value;
    }

    /// <summary>
    /// 이어지는 용접선 판정 거리 [mm] 파라미터 키 — 직전 종료 위치와 다음 시작점이 면을 따라 이 거리 안이면(앞 용접선
    /// 끝 = 다음 용접선 시작) 면에서 띄우지 않고 그 높이에서 바로 이어 간다. 0 이면 끔(항상 <see cref="ChainStandoffKey"/> 까지 띄움).
    /// 실기 2026-11-12 L4-R09C09: 한 줄로 이어진 V01→V04 사이마다 75~83mm 띄웠다가 ⑱ 첫 경유점으로 다시 들어왔다.
    /// </summary>
    public const string ChainContinueTolKey = "inspection.chainContinueTolMm";

    /// <summary>이어지는 용접선 판정 거리 기본값 [mm].</summary>
    public const double DefaultChainContinueTolMm = 50.0;

    /// <summary>이어지는 용접선 판정 거리 상한 [mm] — 이보다 멀리는 낮은 높이로 옆 이동하지 않는다(골판 돌출).</summary>
    internal const double MaxChainContinueTolMm = 100.0;

    /// <summary>띄우지 않고 이어 갈 때 현재 TCP 의 면 이격 하한 [mm] — 이보다 면에 붙어 있으면 종전대로 띄운다.</summary>
    internal const double MinContinueHeightMm = 10.0;

    /// <summary>파라미터 원값 → 이어지는 용접선 판정 거리. 0~상한이면 (그 값, true), 비면 (기본, true), 범위 밖이면 (기본, false).</summary>
    internal static (double Value, bool Ok) ResolveChainContinueTolMm(double? raw)
        => raw is { } v
            ? (v >= 0 && v <= MaxChainContinueTolMm ? (v, true) : (DefaultChainContinueTolMm, false))
            : (DefaultChainContinueTolMm, true);

    private async Task<double> GetChainContinueTolMmAsync()
    {
        double? raw = null;
        try { raw = await _param.GetDoubleAsync(ChainContinueTolKey); }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }
        var (value, ok) = ResolveChainContinueTolMm(raw);
        if (!ok)
            _logger.LogWarning("'{Key}' 값 {Raw} 이(가) 범위(0~{Max:0}mm) 밖 — {Value:0}mm 사용",
                ChainContinueTolKey, raw, MaxChainContinueTolMm, value);
        return value;
    }

    /// <summary>
    /// 이어지는 용접선이면 띄우지 않는 연속 이동 목표 — 다음 시작점 정면, 높이는 현재 TCP 그대로인 BASE 점 [x,y,z].
    /// 조건: 현재 TCP 가 연속 이동 평면(<paramref name="chainBase"/>, 면 이격 <paramref name="chainStandoffMm"/>)보다 면 쪽이고,
    /// 면을 따라 잰 거리가 <paramref name="tolMm"/> 이하이며, 현재 면 이격이 <see cref="MinContinueHeightMm"/> 이상.
    /// 하나라도 아니면 null — 호출측이 종전대로 띄운다.
    /// </summary>
    internal static double[]? ContinuingSeamTargetBase(
        double[] cur, double[] chainBase, double[] wallOut, double chainStandoffMm, double tolMm)
    {
        if (tolMm <= 0) return null;
        double dx = cur[0] - chainBase[0], dy = cur[1] - chainBase[1], dz = cur[2] - chainBase[2];
        var h = dx * wallOut[0] + dy * wallOut[1] + dz * wallOut[2];      // 연속 이동 평면 기준 높이(면 쪽이 음수)
        if (h >= -1.0) return null;                                       // 이미 그 평면 이상 — 띄울 일이 없다
        if (chainStandoffMm + h < MinContinueHeightMm) return null;       // 면에 너무 붙음
        double lx = dx - h * wallOut[0], ly = dy - h * wallOut[1], lz = dz - h * wallOut[2];
        if (Math.Sqrt(lx * lx + ly * ly + lz * lz) > tolMm) return null;  // 이어지지 않는 용접선
        return new[] { chainBase[0] + h * wallOut[0], chainBase[1] + h * wallOut[1], chainBase[2] + h * wallOut[2] };
    }

    /// <summary>
    /// 연속 이동 목표 위치 — 첫 진입 접근점(면에서 <paramref name="approachStandoffMm"/>)을 면 법선을 따라
    /// <paramref name="chainStandoffMm"/> 거리로 당긴 BASE 점 [x,y,z].
    /// </summary>
    internal static double[] ChainTargetBase(double[] approachBase, double[] wallOut, double approachStandoffMm, double chainStandoffMm)
    {
        var d = chainStandoffMm - approachStandoffMm;
        return new[] { approachBase[0] + d * wallOut[0], approachBase[1] + d * wallOut[1], approachBase[2] + d * wallOut[2] };
    }

    /// <summary>파라미터 원값 → ② 면 이격. 범위 안이면 (그 값, true), 아니면 (기본값, false).</summary>
    internal static (double Value, bool Ok) ResolveApproachStandoffMm(double? raw)
        => raw is { } v && v >= MinApproachStandoffMm && v <= MaxApproachStandoffMm
            ? (v, true)
            : (DefaultApproachStandoffMm, false);

    /// <summary>자기 간섭 가드 켜기/끄기 파라미터 — 기본 켜짐. 외곽이 보수적이라(툴 회전 대칭) 현장에서 거짓 거부가
    /// 확인되면 끌 수 있게 둔다.</summary>
    public const string SelfCollisionGuardKey = "inspection.selfCollisionGuard";

    /// <summary>
    /// ② 관절 해가 팔·검사 툴을 차체·리프트 기둥·신호탑에 닿게 하는지(<see cref="CobotEnvelope"/>). 닿으면 사유, 아니면 null.
    /// 스트로크·장착 yaw 를 모르면(티칭 폴백) 판정하지 않는다.
    /// </summary>
    internal static string? SelfCollisionBlocker(double[] joints, SeamApproachResult seam)
    {
        if (seam.StrokeMm is not { } stroke || seam.MountYawDeg is not { } yaw) return null;
        var self = CobotEnvelope.Default.Evaluate(Fr5Kinematics.Fr5Wml, joints, stroke, yaw);
        return self.Collides
            ? $"[Cobot 검사위치 이동] 자기 간섭 — 역기구학 해 [{string.Join(", ", joints.Select(v => v.ToString("0.0")))}]° 에서 " +
              $"{self} (스트로크 {stroke:0}mm). 출발 자세(ready·홈 위치_2)의 IK 가지가 이 목표에 맞지 않습니다 — " +
              "정차 위치·스트로크를 바꾸거나 다른 가지의 진입 자세를 티칭하세요."
            : null;
    }

    /// <summary>
    /// ② 관절 해가 출발 자세와 팔꿈치 가지(J3 부호)가 다르면 사유, 같으면 null. 가지가 바뀌면 MoveJ 가 팔을 편 자세를
    /// 지나 반대편으로 접으며 크게 휘두른다 — 실기 2026-11-02 L4-R03C09 PU #2 에서 J3 −155.8° → +135.1°(291°) 해가 뽑혔다.
    /// </summary>
    internal static string? ElbowFlipBlocker(double[] reference, double[] joints)
        => FairinoRpcClient.IsElbowFlip(reference, joints)
            ? $"[Cobot 검사위치 이동] 팔꿈치 가지 전환 — 목표 해 J3 {joints[2]:0.0}° 가 출발 자세 J3 {reference[2]:0.0}° 와 " +
              $"반대편이라 팔 전체가 뒤집히는 관절 이동이 됩니다 (해 [{string.Join(", ", joints.Select(v => v.ToString("0.0")))}]°). " +
              "같은 가지로는 이 접근점에 도달할 수 없습니다 — 정차 위치를 바꾸거나 이 면에 맞는 진입 자세를 티칭하세요."
            : null;

    private async Task<bool> SelfCollisionGuardOnAsync()
    {
        try { return await _param.GetBoolAsync(SelfCollisionGuardKey) ?? true; }
        catch { return true; }   // DB 미준비 등 — 안전 쪽(켜짐)
    }

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        // 진입 준비(홈 경유)를 이 스텝이 맡으므로 홈 티칭이 선행조건이다.
        if (SequenceEntry.ValidateHome(context) is { IsValid: false } homeError)
            return homeError;

        // 진입 기준 자세 — 상부 면은 홈 위치_2, 그 외는 ready.
        if (SequenceEntry.ValidateRoute(context) is { IsValid: false } routeError)
            return routeError;

        if (context.SeamStartW is not { Length: 3 })
            return StepValidation.Fail("용접선 시작점(seamStartW) 없음 — ACS 작업의 용접선 좌표가 필요합니다.");

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

    /// <summary>
    /// ② 접근 앵커(= u/v·J6 전) 선택 — 하이브리드. <b>위치</b>는 ACS 용접선 접근점(<paramref name="seamBase"/>)에서,
    /// <b>자세</b>는 토글·가용성에 따라 계산된 면 법선(<paramref name="computedPose"/>의 [rx,ry,rz]) 또는
    /// 손으로 맞춘 티칭 자세(<paramref name="taughtPose"/>의 [rx,ry,rz])에서 온다.
    ///
    /// · <paramref name="seamBase"/>가 null(측위·장착보정 없음) → 티칭 포즈 전체로 폴백(종전 동작).
    /// · seamBase 있음 + 토글 ON + computedPose 있음(wall_code 유효) → 위치=seam, 자세=계산 법선.
    /// · 그 외(토글 OFF 또는 computedPose 없음) → 위치=seam, 자세=티칭(종전 동작).
    ///
    /// 광축 둘레 비틀림(roll)은 이후 <see cref="AlignTwistToJ6Async"/>가 J6 절대각으로 재정렬하므로,
    /// 여기서 바뀌는 것은 사실상 <b>광축 방향</b>(티칭 손목 방향 → 면 법선)뿐이다.
    /// </summary>
    internal static (double[] Anchor, bool UsedComputed) BuildApproachAnchor(
        double[] taughtPose, double[]? seamBase, double[]? computedPose, bool useComputed)
    {
        if (seamBase is not { Length: 3 })
            return (taughtPose, false);   // 환산 불가 — 티칭 폴백(위치·자세 모두 티칭)

        var orientation = useComputed && computedPose is { Length: 6 } ? computedPose : taughtPose;
        var anchor = new[]
        {
            seamBase[0], seamBase[1], seamBase[2],
            orientation[3], orientation[4], orientation[5],
        };
        return (anchor, ReferenceEquals(orientation, computedPose));
    }

    /// <summary>u/v 오프셋 합성용 앵커 정규화 — 티칭 자세가 광축(툴 Z) 둘레로 비틀려 저장돼 있어도
    /// (예: J6=180° 수직 자세로 재티칭) 툴 +Y가 베이스 상방(+Z)을 향하도록 트위스트를 제거한다.
    /// u=수평/v=수직 매핑과 ④의 이미지↔툴 축 매핑은 모두 이 표준 자세를 전제하므로,
    /// 정규화 없이는 비틀린 티칭에서 u/v가 90° 돌아간 축으로 움직인다.
    /// 광축이 연직에 가까우면(상방 성분의 XY 투영이 미소) 기준이 모호해 티칭 자세를 그대로 둔다.</summary>
    internal static double[] NormalizeUvAnchor(double[] target, ILogger? logger = null)
    {
        var m = FrameMath.PoseToMatrix(target);
        // 베이스 +Z(상방)의 툴 X/Y축 성분: up·x̂_t = R[2,0], up·ŷ_t = R[2,1]
        double ux = m[2, 0], uy = m[2, 1];
        var n = Math.Sqrt(ux * ux + uy * uy);
        if (n < 0.2) return target;                       // 광축이 연직 근방 — 정규화 불가, 티칭 자세 유지
        // T' = T·Rz(θ): 새 툴 Y=(−sinθ, cosθ)가 상방 투영(ux,uy)와 나란하도록 θ = atan2(−ux, uy)
        var theta = Math.Atan2(-ux, uy) * 180.0 / Math.PI;
        if (Math.Abs(theta) < 1.0) return target;         // 이미 정렬(±1°)
        logger?.LogInformation(
            "[Cobot 검사위치 이동] u/v 앵커 정규화: 티칭 자세 광축 트위스트 {Theta:0.#}° 제거 (툴 +Y → 베이스 상방 정렬)", theta);
        return FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, theta }, target);
    }

    /// <summary>u(툴 X)/v(툴 Y) 병진 오프셋 벡터. 검사 방향(수평/수직)에 따른 광축 회전은 여기서 하지 않고
    /// <see cref="AlignTwistToJ6Async"/> 가 J6 절대각(<see cref="TargetJ6Deg"/>)으로 맞춘다.</summary>
    internal static double[] UvOffset(SequenceContext c) => new[]
    {
        c.InspectionOffsetU, c.InspectionOffsetV, 0.0, 0.0, 0.0, 0.0,
    };

    /// <summary>수평 용접라인 검사 J6 절대각 [도].</summary>
    internal const double HorizontalJ6Deg = 90.0;

    /// <summary>수직 용접라인 검사 J6 절대각 [도] — 검사 대기 자세(J6=180°)와 같다.</summary>
    internal const double VerticalJ6Deg = 180.0;

    /// <summary>
    /// 검사 방향별 J6 절대각. 센서 헤드(레이저·Depth 카메라)가 플랜지 한쪽으로 길게 나와 있어,
    /// J6 를 이 두 값(180°/90°) 밖으로 돌리면 J3–J4 링크암과 간섭한다. 그래서 벽면·이동 방향과 무관하게
    /// 수직 = 180°, 수평 = 90° 로 고정하고, 대기 자세(180°)에서 최대 −90° 만 돌게 한다.
    /// </summary>
    public static double TargetJ6Deg(SequenceContext c)
        => c.InspectionDirection == InspectionMoveDirection.Vertical ? VerticalJ6Deg : HorizontalJ6Deg;

    /// <summary>J6 목표 허용 오차 [도].</summary>
    private const double J6ToleranceDeg = 0.5;

    /// <summary>
    /// <paramref name="pose"/> 를 툴 Z(광축) 둘레로 돌려 역기구학 해의 J6 가 <paramref name="targetJ6Deg"/> 가
    /// 되게 한다. 위치·광축 방향은 그대로이고 광축 둘레 비틀림만 바뀐다. 툴 Z 가 플랜지 Z 와 반대인지 등
    /// 툴 정의에 따라 툴 RZ↔J6 부호가 달라질 수 있어, 첫 보정에서 오차가 커지면 부호를 뒤집는다.
    /// 해는 현재 J1~J5 + 목표 J6 에 가장 가까운 것을 고르므로 270° 같은 반대편 감긴 해로 가지 않는다.
    /// 목표 J6 는 <b>현재 와인딩 쪽</b> 같은 물리각으로 바꿔 쓴다(현재 −230° 에 목표 90° → −270°) — 반대편
    /// +90° 를 기준으로 풀면 손목을 뒤집은 해가 나와 MoveL 이 IK 112 로 실패했다(실기 2026-10-15 천장 T).
    /// </summary>
    public static Task<(double[] Pose, double[] Joints)> AlignTwistToJ6Async(
        CobotService cobot, double[] pose, int tool, InspectionMoveDirection direction, CancellationToken ct)
        => AlignTwistToJ6Async(cobot, pose, tool,
            direction == InspectionMoveDirection.Vertical ? VerticalJ6Deg : HorizontalJ6Deg, ct);

    public static async Task<(double[] Pose, double[] Joints)> AlignTwistToJ6Async(
        CobotService cobot, double[] pose, int tool, double targetJ6Deg, CancellationToken ct)
    {
        var reference = (await cobot.Rpc.GetActualJointPosAsync(ct: ct)).ToArray();
        targetJ6Deg = NearestWinding(targetJ6Deg, reference[5]);
        reference[5] = targetJ6Deg;

        var p = pose;
        var joints = await cobot.Rpc.GetInverseKinNearestAsync(p, reference, tool, 0, ct);
        var err = joints[5] - targetJ6Deg;
        var sign = -1.0;   // 현장 티칭 5점 피팅: 툴 Z 가 플랜지 Z 와 반대 → 툴 RZ +α = J6 −α
        for (var iter = 0; iter < 5 && Math.Abs(err) > J6ToleranceDeg; iter++)
        {
            var next = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, sign * -err }, p);
            var nj = await cobot.Rpc.GetInverseKinNearestAsync(next, reference, tool, 0, ct);
            var nerr = nj[5] - targetJ6Deg;
            if (Math.Abs(nerr) > Math.Abs(err))
            {
                sign = -sign;   // 부호 가정이 틀렸다 — 같은 출발점에서 반대로 돈다
                continue;
            }
            (p, joints, err) = (next, nj, nerr);
        }

        if (Math.Abs(err) > J6ToleranceDeg)
            throw new InvalidOperationException(
                $"J6 를 {targetJ6Deg:0}° 로 맞추지 못했습니다 (IK 결과 J6={joints[5]:0.0}°) — 티칭 자세의 광축 방향을 확인하세요.");
        return (p, joints);
    }

    /// <summary>물리각 <paramref name="angleDeg"/> 와 같은(360° 주기) 값 중 <paramref name="currentDeg"/> 에 가장 가까운 것.</summary>
    internal static double NearestWinding(double angleDeg, double currentDeg)
        => angleDeg + 360.0 * Math.Round((currentDeg - angleDeg) / 360.0);

    /// <summary>
    /// 앵커에 u/v 오프셋을 합성한 목표 — 이후 <see cref="AlignTwistToJ6Async"/> 가 광축 비틀림만 바꾼다.
    /// ③ 게이트 폴백·드라이런 끝점 이동도 같은 순서(정규화 → 합성 → J6 맞춤)를 따라야 한다 —
    /// 식이 갈라지면 ③이 통째로 막힌다(2026-09-25 회귀).
    /// </summary>
    internal static double[] ComposeUvTarget(double[] anchor, SequenceContext c)
        => FrameMath.FromFrame(UvOffset(c), anchor);

    /// <summary>
    /// ③ 카메라 목표거리(레시피 값) — 면 이격을 주지 않은 호출(<see cref="SeamApproachResolver.ResolveAsync"/>
    /// 폴백)과 ⑤ Peak 탐색의 가정 거리에 쓴다. 운영 ② 접근점은 이 값이 아니라 거친 접근 거리
    /// <see cref="ApproachStandoffKey"/>(기본 400mm)로 물러나고, ③·④⁺(레이저 WD 300mm)가 실측으로 확정한다.
    /// ACS <c>params.standoffMm</c>
    /// (<see cref="SequenceContext.StandoffMmOverride"/>)은 쓰지 않는다 — 계약에서 그 단어는 §4.4 의
    /// 정차 이격(벽↔로봇 기준점, 이미 nodePosition 에 반영됨)과 같은 이름이고, 툴 이격은 AMR 이
    /// 정할 값이다(§10 N18).
    /// </summary>
    internal static double ResolveApproachDistanceMm(SequenceContext c)
        => c.CameraTargetDistanceMm > 0 ? c.CameraTargetDistanceMm : SeamBaseTransform.DefaultStandoffMm;

    /// <summary>
    /// <paramref name="current"/> 자세의 광축(툴 <paramref name="axis"/>)을 <paramref name="computed"/> 의 광축 방향으로
    /// 맞추는 <b>최소 회전</b>을 적용한 자세. 광축 둘레 비틀림은 current 를 유지한다 — 이미 면 법선을 보고 있으면
    /// 자세가 그대로라 손목이 움직이지 않는다. 위치는 current 값(호출측이 덮어쓴다).
    /// </summary>
    internal static double[] AlignOpticalAxisKeepTwist(double[] current, double[] computed, ToolAxisDir axis)
    {
        var mc = FrameMath.PoseToMatrix(current);
        var mt = FrameMath.PoseToMatrix(computed);
        var col = (int)axis / 2;
        var sgn = (int)axis % 2 == 0 ? 1.0 : -1.0;
        double[] a = { sgn * mc[0, col], sgn * mc[1, col], sgn * mc[2, col] };   // 현재 광축(베이스)
        double[] b = { sgn * mt[0, col], sgn * mt[1, col], sgn * mt[2, col] };   // 목표 광축(베이스)

        double[] k = { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        var sin = Math.Sqrt(k[0] * k[0] + k[1] * k[1] + k[2] * k[2]);
        var cos = Math.Clamp(a[0] * b[0] + a[1] * b[1] + a[2] * b[2], -1.0, 1.0);
        if (sin < 1e-9)
        {
            if (cos > 0) return current.ToArray();                 // 이미 정렬
            // 정반대 — 회전축이 유일하지 않다. 광축에 수직인 임의 축(현재 툴의 다른 축)으로 180°.
            var other = (col + 1) % 3;
            k = new[] { mc[0, other], mc[1, other], mc[2, other] };
            sin = 0; cos = -1;
        }
        else
        {
            for (var i = 0; i < 3; i++) k[i] /= sin;
        }

        // Rodrigues: R = I·cos + sin·[k]× + (1−cos)·k kᵀ
        var r = new double[3, 3];
        double[,] kx = { { 0, -k[2], k[1] }, { k[2], 0, -k[0] }, { -k[1], k[0], 0 } };
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                r[i, j] = (i == j ? cos : 0) + sin * kx[i, j] + (1 - cos) * k[i] * k[j];

        var m = (double[,])mc.Clone();
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = r[i, 0] * mc[0, j] + r[i, 1] * mc[1, j] + r[i, 2] * mc[2, j];
        return FrameMath.MatrixToPose(m);
    }

    /// <summary>② 용접선 영상 가로 정렬 켜기/끄기 파라미터 — 기본 켜짐. 접근 자세를 광축 둘레로 돌려 용접선이
    /// depth 영상에서 가로선(영상 X 축)으로 보이게 한다. 끄면 종전처럼 J6 180° 비틀림만 맞춘다.</summary>
    public const string LevelSeamInImageKey = "inspection.levelSeamInImage";

    /// <summary>용접선이 광축과 거의 나란하면(영상 평면 성분 &lt; 이 값) 영상 속 방향이 정의되지 않아 정렬하지 않는다.</summary>
    internal const double MinSeamInPlaneComponent = 0.2;

    /// <summary>정렬 회전이 이 각도보다 작으면 돌리지 않는다 [도].</summary>
    internal const double SeamLevelMinTwistDeg = 0.5;

    /// <summary>
    /// <paramref name="pose"/> 를 툴 광축(<paramref name="opticalAxis"/>) 둘레로 몇 도 돌리면 용접선(<paramref name="seamDirBase"/>,
    /// BASE 단위벡터)이 영상 X 축(<paramref name="imageX"/> 툴축)과 나란해지는지 [도, (−180, 180]].
    /// 반대 방향(+180°)도 똑같이 가로선이 되므로 호출측이 두 후보 중 J6 안전 범위에 드는 쪽을 고른다.
    /// 축 매핑이 서로 겹치거나 용접선이 광축과 거의 나란하면 null.
    /// </summary>
    internal static double? SeamLevelTwistDeg(double[] pose, double[] seamDirBase,
        ToolAxisDir opticalAxis, ToolAxisDir imageX, ToolAxisDir imageY)
    {
        int iu = (int)opticalAxis / 2, ix = (int)imageX / 2, iy = (int)imageY / 2;
        if (iu == ix || iu == iy || ix == iy) return null;

        var m = FrameMath.PoseToMatrix(pose);
        var d = new double[3];   // 용접선 방향의 툴 프레임 성분 d_t = Rᵀ·d
        for (var j = 0; j < 3; j++)
            d[j] = m[0, j] * seamDirBase[0] + m[1, j] * seamDirBase[1] + m[2, j] * seamDirBase[2];

        var ex = AxisUnit(imageX);
        var ey = AxisUnit(imageY);
        var u = AxisUnit(opticalAxis);
        var dx = d[0] * ex[0] + d[1] * ex[1] + d[2] * ex[2];
        var dy = d[0] * ey[0] + d[1] * ey[1] + d[2] * ey[2];
        if (Math.Sqrt(dx * dx + dy * dy) < MinSeamInPlaneComponent) return null;

        // 툴을 u 둘레로 φ 돌리면 툴 프레임에서 본 용접선은 −φ 돈다. (ex, ey, u) 가 오른손계면 영상 속 각 α 가
        // α−φ 가 되므로 φ = α, 왼손계(ex×ey = −u)면 α+φ 가 되므로 φ = −α.
        var cx = new[] { ex[1] * ey[2] - ex[2] * ey[1], ex[2] * ey[0] - ex[0] * ey[2], ex[0] * ey[1] - ex[1] * ey[0] };
        var hand = Math.Sign(cx[0] * u[0] + cx[1] * u[1] + cx[2] * u[2]);
        var alpha = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        return NormalizeDeg(hand * alpha);
    }

    /// <summary>위치는 그대로 두고 툴축 <paramref name="axis"/> 둘레로 <paramref name="deg"/> 돌린 자세(툴 프레임 회전).</summary>
    internal static double[] RotateAboutToolAxis(double[] pose, ToolAxisDir axis, double deg)
    {
        var m = FrameMath.PoseToMatrix(pose);
        var k = AxisUnit(axis);
        var a = deg * Math.PI / 180.0;
        double c = Math.Cos(a), s = Math.Sin(a), t = 1 - c;
        double x = k[0], y = k[1], z = k[2];
        var r = new[,]
        {
            { t * x * x + c,     t * x * y - s * z, t * x * z + s * y },
            { t * x * y + s * z, t * y * y + c,     t * y * z - s * x },
            { t * x * z - s * y, t * y * z + s * x, t * z * z + c     },
        };
        var o = (double[,])m.Clone();
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                o[i, j] = m[i, 0] * r[0, j] + m[i, 1] * r[1, j] + m[i, 2] * r[2, j];
        return FrameMath.MatrixToPose(o);
    }

    /// <summary>
    /// ⑤~⑫ 진행축 계산용으로 용접선 방향과 그 면의 광축을 전역 기록한다(<see cref="SeamOrientationMemory"/>).
    /// 용접선 끝점이나 법선 자세가 없으면 앞 기록이 다른 용접선 것이므로 지운다.
    /// </summary>
    private static void RecordSeamOrientation(SeamApproachResult seam, string? wallCode)
    {
        if (seam.SeamDirBase is { Length: 3 } dir && seam.ComputedPose is { Length: 6 } pose)
            SeamOrientationMemory.Record(new SeamOrientationRecord(
                dir.ToArray(), ToolAxisInBase(pose, seam.OpticalAxis), wallCode));
        else
            SeamOrientationMemory.Clear();
    }

    /// <summary>자세 <paramref name="pose"/> 의 툴축 <paramref name="axis"/> 를 BASE 단위벡터로(R·e).</summary>
    internal static double[] ToolAxisInBase(double[] pose, ToolAxisDir axis)
    {
        var m = FrameMath.PoseToMatrix(pose);
        var k = AxisUnit(axis);
        var v = new double[3];
        for (var i = 0; i < 3; i++) v[i] = m[i, 0] * k[0] + m[i, 1] * k[1] + m[i, 2] * k[2];
        return v;
    }

    private static double[] AxisUnit(ToolAxisDir axis)
    {
        var v = new double[3];
        v[(int)axis / 2] = (int)axis % 2 == 0 ? 1.0 : -1.0;
        return v;
    }

    private static double NormalizeDeg(double deg)
    {
        deg %= 360.0;
        if (deg <= -180.0) deg += 360.0;
        else if (deg > 180.0) deg -= 360.0;
        return deg;
    }

    /// <summary>제자리 회전에서 J1~J5 가 이보다 많이 움직이는 해는 쓰지 않는다 [도] — 손목을 뒤집거나 팔을 재구성하는 해다.
    /// 공구 오프셋이 광축과 어긋난 만큼은 J1~J5 도 조금 움직인다.</summary>
    internal const double MaxLevelArmTravelDeg = 20.0;

    /// <summary>J6 를 뺀 J1~J5 관절별 최대 변화 [도].</summary>
    internal static double MaxArmTravelExceptJ6(double[] from, double[] to)
    {
        var m = 0.0;
        for (var i = 0; i < Math.Min(5, Math.Min(from.Length, to.Length)); i++) m = Math.Max(m, Math.Abs(to[i] - from[i]));
        return m;
    }

    /// <summary>
    /// ② 접근점에 도착한 뒤 그 자리에서 광축 둘레로 돌려 용접선이 depth 영상에서 가로선으로 보이게 한다. 후보는 φ 와 φ+180°
    /// (둘 다 가로선)이고, J1~J5 가 거의 그대로(<see cref="MaxLevelArmTravelDeg"/> 이내)이며 J6 가 안전 범위
    /// (<see cref="WObjAttitude.SafeJ6MinDeg"/>~<see cref="WObjAttitude.SafeJ6MaxDeg"/>, 센서 헤드–링크암 간섭 없음) 안인 해 중
    /// 관절 변위가 작은 쪽을 고른다. 정렬하지 못해도 단계는 실패시키지 않는다 — 접근점에는 이미 와 있으므로
    /// 경고만 남기고 종전 진행축(검사 방향 기준)으로 간다. 결과 문구(앞 공백 포함, 정렬 꺼짐이면 빈 문자열)를 돌려준다.
    /// </summary>
    private async Task<string> LevelSeamInPlaceAsync(SequenceContext context, SeamApproachResult seam, CancellationToken ct)
    {
        bool on;
        try { on = await _param.GetBoolAsync(LevelSeamInImageKey) ?? true; }
        catch { on = true; }
        if (!on) return "";

        if (seam.SeamDirBase is not { Length: 3 } dir)
        {
            _logger.LogInformation("[Cobot 검사위치 이동] 용접선 가로 정렬 생략 — 용접선 끝점(seamEndW) 없음");
            return " 용접선 가로 정렬 생략(끝점 없음).";
        }

        var (imageX, _) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
        var (imageY, _) = await WeldSequenceSupport.GetImageYAxisAsync(_param);
        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var curJ = (await _cobot.Rpc.GetActualJointPosAsync(ct: ct)).ToArray();

        if (SeamLevelTwistDeg(cur, dir, seam.OpticalAxis, imageX, imageY) is not { } phi)
        {
            _logger.LogWarning(
                "[Cobot 검사위치 이동] 용접선 가로 정렬 생략 — 영상 축 매핑(X→{X}, Y→{Y}, 광축 {U})이 겹치거나 용접선이 광축과 거의 나란함",
                imageX, imageY, seam.OpticalAxis);
            return " 용접선 가로 정렬 생략(축 매핑).";
        }

        if (Math.Abs(phi) < SeamLevelMinTwistDeg)
        {
            context.SeamLeveledInImage = true;
            return $" 용접선 가로 정렬(이미 가로, {phi:+0.0;-0.0;0}°).";
        }

        var candidates = new List<(double TwistDeg, double? J6, double? TotalDeg)>();
        var solved = new Dictionary<double, (double[] Pose, double[] Joints)>();
        var rejects = new List<string>();
        var guardOn = await SelfCollisionGuardOnAsync();
        foreach (var twist in new[] { phi, NormalizeDeg(phi + 180.0) })
        {
            var p = RotateAboutToolAxis(cur, seam.OpticalAxis, twist);
            double[] j;
            try { j = await _cobot.Rpc.GetInverseKinNearestAsync(p, curJ, context.Tool, 0, ct); }
            catch (InvalidOperationException) { rejects.Add($"{twist:+0.0;-0.0}°: 도달 불가"); candidates.Add((twist, null, null)); continue; }

            var arm = MaxArmTravelExceptJ6(curJ, j);
            if (arm > MaxLevelArmTravelDeg)
            {
                rejects.Add($"{twist:+0.0;-0.0}°: J1~J5 {arm:0}° 이동(손목 재구성)");
                candidates.Add((twist, null, null));
                continue;
            }
            if (guardOn && SelfCollisionBlocker(j, seam) is not null)
            {
                rejects.Add($"{twist:+0.0;-0.0}°: 자기 간섭");
                candidates.Add((twist, null, null));
                continue;
            }
            var total = Enumerable.Range(0, Math.Min(j.Length, curJ.Length)).Sum(i => Math.Abs(j[i] - curJ[i]));
            candidates.Add((twist, j[5], total));
            solved[twist] = (p, j);
        }

        if (WObjAttitude.SelectSafeTwist(curJ[5], candidates) is not { } pick)
        {
            foreach (var c in candidates)
                if (c.J6 is { } j6) rejects.Add($"{c.TwistDeg:+0.0;-0.0}°: J6 {j6:0.0}° 안전 범위 밖");
            var why = string.Join(" / ", rejects);
            _logger.LogWarning(
                "[Cobot 검사위치 이동] 용접선 가로 정렬 생략 — 영상 속 기울기 {Phi:+0.0;-0.0}°, 맞는 회전 없음: {Why}. 종전 진행축으로 진행",
                phi, why);
            return $" 용접선 가로 정렬 생략(기울기 {phi:+0.0;-0.0}°, {why}).";
        }

        var (pose, joints) = solved[pick.TwistDeg];
        var rc = await _cobot.Rpc.MoveJAsync(joints, pose, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
        {
            _logger.LogWarning("[Cobot 검사위치 이동] 용접선 가로 정렬 회전 실패 (rc={Rc}){Desc} — 종전 진행축으로 진행",
                rc, FairinoErrorCodes.Suffix(rc));
            return $" 용접선 가로 정렬 회전 실패(rc={rc}).";
        }

        context.SeamLeveledInImage = true;
        _logger.LogInformation(
            "[Cobot 검사위치 이동] 용접선 가로 정렬 — 제자리 광축 둘레 {Twist:+0.0;-0.0}° (영상 속 기울기 {Phi:+0.0;-0.0}°), J6 {J0:0.0}° → {J6:0.0}°, J1~J5 최대 {Arm:0.0}°",
            pick.TwistDeg, phi, curJ[5], joints[5], MaxArmTravelExceptJ6(curJ, joints));
        return $" 용접선 가로 정렬 {pick.TwistDeg:+0.0;-0.0}°(J6={joints[5]:0.0}°).";
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        // 새로운 접근 사이클에서만 교체한다. ④ 정렬 재시도는 이 기준점을 덮어쓰지 않는다.
        InspectionAlignmentAnchor.Clear(context);
        StepResult result;
        try
        {
            result = await ExecuteCoreAsync(context, ct);
        }
        catch
        {
            // 진입 MoveJ 실패·복귀 경로 차단·취소는 예외로 끝난다 — 이번 접근의 용접선 점 S 를 남기면
            // 이후 세미오토 ④⁺가 엉뚱한 점으로 횡복귀한다.
            InspectionAlignmentAnchor.Clear(context);
            throw;
        }
        if (result.Success)
            InspectionAlignmentAnchor.CaptureIfMissing(context,
                await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct));
        else
            InspectionAlignmentAnchor.Clear(context);   // 실패한 접근의 용접선 점을 남기지 않는다
        return result;
    }

    private async Task<StepResult> ExecuteCoreAsync(SequenceContext context, CancellationToken ct)
    {
        // 접근점은 정차 배치(AMR pose·장착 보정·스트로크)만으로 정해지므로 코봇을 움직이기 전에 환산·리치
        // 점검을 한다 — 1단계 뒤에 실패하면 같은 정차점의 task 마다 ready 왕복을 헛되이 반복한다.
        var approachStandoffMm = await GetApproachStandoffMmAsync(_param, _logger);
        var seam = await _seam.ResolveAsync(
            context, context.SeamStartW, context.SeamEndW, context.Tool, "[Cobot 검사위치 이동]", ct,
            approachStandoffMm);
        if (seam.Blocker is not null)
        {
            if (seam.Shortfall is not null) context.Bag[ReachShortfallBagKey] = seam.Shortfall;
            return StepResult.Fail(seam.Blocker);
        }
        if (seam.Base is null)
            return StepResult.Fail($"[Cobot 검사위치 이동] {seam.Note}");

        context.Bag[SeamStartBaseBagKey] = seam.Base.ToArray();
        // ④⁺ 복귀 기준 — 접근점(이격 400mm)이 아닌 면 위 용접선 점. 틸트 보정 후에도 광축이 이 점을 지나게 복귀한다.
        InspectionAlignmentAnchor.SetSeamPoint(context, seam.SeamPointBase);
        RecordSeamOrientation(seam, context.WallCode);
        // ⑧ Peak2 이동이 점1 과 같은 면 거리를 유지하는 기준(모델 면 법선 — ④ 국부 틸트와 무관).
        if (seam.ComputedPose is { Length: 6 } normalPose)
            context.Bag[WeldSequenceSupport.WallOutNormalBagKey] = WallOutNormal(normalPose, seam.OpticalAxis);
        else
            context.Bag.Remove(WeldSequenceSupport.WallOutNormalBagKey);
        // ⑧ Peak2 이동이 용접선 끝점 쪽으로 가는 기준 — 가로 정렬이 툴을 어느 쪽으로 돌렸든 방향은 용접선이 정한다.
        if (seam.SeamDirBase is { Length: 3 } seamDir)
            context.Bag[WeldSequenceSupport.SeamDirBaseBagKey] = seamDir.ToArray();
        else
            context.Bag.Remove(WeldSequenceSupport.SeamDirBaseBagKey);
        if (ShiftAnchorFrame(context, seam.Base) is { } shiftError)
            return StepResult.Fail(shiftError);

        // ── 같은 anchor 그룹 연속 task: 직전 종료 자세 그대로면 복귀 없이 다음 시작점으로 ─────
        if (await TryChainFromPreviousAsync(context, seam, approachStandoffMm, ct) is { } chained)
            return chained;

        // ── 1단계: 작업 준비 위치(ready) — 홈 위치_2 모드(상부 면)는 홈 위치_2·검사 준비 ─────
        // 자세 모드가 면과 맞지 않으면(수동 전환 필요) 움직이지 않고 실패한다.
        var (entryOk, entryNote) = await SequenceEntry.PrepareAsync(_cobot, context, _logger, ct);
        if (!entryOk)
            return StepResult.Fail(entryNote);

        // ── 2단계: 용접선 시작점에서 400mm 떨어진 접근점 ───────────────
        // 자세: ready 도착 자세에서 광축만 면 법선으로 최소 회전시킨다 — 광축 둘레 비틀림(J6)은 그대로.
        // 계산 법선 자세를 통째로 쓰면 비틀림(툴 X/Y)이 ready 와 달라 J6·손목이 불필요하게 돈다.
        // wall_code 가 없으면 ready 자세를 그대로 유지한다.
        var current = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var usedNormal = seam.ComputedPose is { Length: 6 };
        var orientation = usedNormal
            ? AlignOpticalAxisKeepTwist(current, seam.ComputedPose!, seam.OpticalAxis)
            : current;
        var target = new[]
        {
            seam.Base[0], seam.Base[1], seam.Base[2],
            orientation[3], orientation[4], orientation[5],
        };

        // 광축 둘레 비틀림을 대기 자세 J6(180°)로 맞춘다. 벽은 ready 비틀림이 이미 ≈180° 라 변화가 없지만,
        // 천장처럼 광축을 크게 돌리면 비틀림이 밀려(실기 2026-10-15 T: J6 −210°=물리 150°) 수평 검사의
        // 90° 회전이 당시 안전 범위(물리 90°~180°) 밖으로 나갔다. 맞추지 못하면 경고만 남기고 종전 자세로 간다.
        var twistNote = "";
        if (usedNormal)
        {
            try
            {
                (target, _) = await AlignTwistToJ6Async(_cobot, target, context.Tool, VerticalJ6Deg, ct);
                twistNote = $", 비틀림 J6 {VerticalJ6Deg:0}° 정렬";
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("[Cobot 검사위치 이동] 비틀림 J6 {Target:0}° 정렬 실패 — 작업 준비 자세 비틀림 유지: {Msg}",
                    VerticalJ6Deg, ex.Message);
            }
        }

        var reference = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
        context.SeamLeveledInImage = false;

        // 면 법선을 알면 접근점으로 한 번에 MoveJ 하지 않는다 — 진입 경유점에서 멀리 떨어진 접근점(챔퍼 아래쪽 끝 등)까지
        // 관절 보간으로 가면 팔이 면 쪽으로 크게 휘둘린다(실기 2026-11-02 L4-R03C09 PU #3, 231° MoveJ 중 EMO).
        if (usedNormal)
        {
            var moved = await PlanarApproachAsync(context, seam, current, target, reference, twistNote, entryNote, ct);
            var usedSeam = seam;
            if (!moved.Success)
            {
                // 법선 정면 자세로 갈 수 없으면(특이점·도달 불가·안전 경로 없음) 파라미터가 켜져 있을 때만 광축을 기울여 본다.
                var (tiltResult, tiltSeam, tiltNote) = await TryTiltedApproachAsync(context, current, reference, twistNote, entryNote, ct);
                if (tiltResult is { Success: true })
                {
                    moved = tiltResult;
                    usedSeam = tiltSeam!;
                }
                else if (tiltNote.Length > 0)
                {
                    moved = moved with { Message = moved.Message + tiltNote };
                }
            }
            if (!moved.Success) return moved;

            // 도착한 자리에서 광축 둘레로 돌려 용접선을 depth 영상의 가로선으로 맞춘다. 진입 전에 돌린 자세로 경로를 풀면
            // 큰 회전(PU 수평 용접선 ≈ 100~130°)에서 keep-wrist IK 가 손목을 뒤집은 해를 골라 진입 후보가 전부 거부됐다
            // (실기 2026-11-06 15:57·16:00·16:03 PU, 진입 관절 이동 ~290°). 제자리 회전은 거의 J6 만 돈다.
            var levelNote = await LevelSeamInPlaceAsync(context, usedSeam, ct);
            return levelNote.Length == 0 ? moved : moved with { Message = moved.Message + levelNote };
        }

        // MoveJ(관절 보간)라 손목 가지 전환은 허용한다. 1순위(J6 와인딩 유지) 해가 팔꿈치 반전·J6 범위·자기 간섭에 걸리면
        // 다음 IK 해를 시험한다 — 해가 하나뿐이거나 전부 걸리면 1순위 해의 사유로 실패(종전과 같은 메시지).
        var sols = await _cobot.Rpc.GetInverseKinRankedAsync(target, reference, context.Tool, 0, ct);
        var guard = await SelfCollisionGuardOnAsync();
        string? Blocker(double[] j)
            => ElbowFlipBlocker(reference, j)
               ?? (Inspection.WObjAttitude.J6RangeBlocker(reference[5], j[5]) is { } j6r
                   ? $"[Cobot 검사위치 이동] 용접선 접근점 {j6r} — 움직이지 않았습니다."
                   : null)
               ?? (guard ? SelfCollisionBlocker(j, seam) : null);
        var joints = sols.FirstOrDefault(j => Blocker(j) is null);
        if (joints is null)
            return StepResult.Fail(Blocker(sols[0])!);
        if (!ReferenceEquals(joints, sols[0]))
            _logger.LogInformation("[Cobot 검사위치 이동] 1순위 IK 해 거부({Why}) — 다른 해 [{J}] 사용",
                Blocker(sols[0]), string.Join(", ", joints.Select(v => v.ToString("0.0"))));

        ApproachPathMemory.Clear();   // 평면 경유가 아닌 이동 — 앞 기록으로 복귀하면 엉뚱한 면을 따라간다
        var rc = await _cobot.Rpc.MoveJAsync(joints, target,
            tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"용접선 접근점 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        return StepResult.Ok(
            $"진입 경유점 → {seam.Note} 관절 이동(MoveJ) 완료 [자세=작업 준비 위치 유지(wall_code 없음){twistNote}, " +
            $"J6={joints[5]:0.0}°].{entryNote}");
    }

    /// <summary>② 평면 경유 접근의 후퇴 거리 [mm] 파라미터 키 — 접근점 면 이격 평면에서 이만큼 더 물러난 평면을 따라 이동한다.</summary>
    public const string PlanarRetreatKey = "inspection.planarApproachRetreatMm";

    /// <summary>접근 자세 탐색(틸트) 최대각 [도] 파라미터 키. 0 이하·미설정 = 꺼짐(기본) — 법선 정면만 쓴다.
    /// 켜면 법선 정면 진입이 실패했을 때 광축을 법선에서 상하·좌우로 이 각도까지 기울인 자세를 찾아 다시 진입한다.
    /// 기울인 만큼 검사 영상이 비스듬해지므로 레시피 허용 입사각 안에서만 쓴다.</summary>
    public const string ApproachTiltMaxKey = "inspection.approachTiltSearchMaxDeg";

    internal const double MaxApproachTiltDeg = 30.0;

    /// <summary>틸트 탐색에서 실제 진입 경로까지 풀어 볼 후보 수 상한 — 진입 경로 검증은 IK 호출이 많다.</summary>
    internal const int MaxTiltPlanAttempts = 3;

    /// <summary>틸트 후보 — (상하, 좌우) [도], 5° 간격으로 <paramref name="maxDeg"/> 까지, 기울기 합이 작은 순. (0,0) 제외.
    /// 순서는 <see cref="ApproachPlanner.Candidates"/>(검사 품질을 덜 건드리는 순)를 따른다.</summary>
    internal static List<(double Up, double Side)> TiltCandidates(double maxDeg)
    {
        var steps = new List<double> { 0 };
        for (var a = 5.0; a <= maxDeg + 1e-9; a += 5.0) { steps.Add(-a); steps.Add(a); }
        var space = new ApproachSearchSpace(steps.ToArray(), steps.ToArray(), new[] { 0.0 }, new[] { 0.0 });
        return ApproachPlanner.Candidates(space)
            .Where(c => Math.Abs(c.TiltUpDeg) + Math.Abs(c.TiltSideDeg) > 1e-9)
            .Select(c => (c.TiltUpDeg, c.TiltSideDeg))
            .ToList();
    }

    /// <summary>틸트 후보 목표 자세의 IK 해 중 쓸 만한 것이 있는지 — 손목 여유(|J5| ≥ <see cref="PlanarWristMarginDeg"/>,
    /// 현재와 같은 J5 부호), 같은 팔꿈치 가지, J6 안전 범위. 진입 경로 검증(비쌈) 전에 거르는 값싼 사전 검사.</summary>
    internal static bool TiltTargetUsable(double[] reference, IEnumerable<double[]> solutions)
        => solutions.Any(j => Math.Abs(j[4]) >= PlanarWristMarginDeg
                              && !IsWristFlip(reference, j)
                              && !FairinoRpcClient.IsElbowFlip(reference, j)
                              && WObjAttitude.J6RangeBlocker(reference[5], j[5]) is null);

    /// <summary>
    /// 법선 정면 진입이 실패했을 때의 접근 자세 탐색. <see cref="ApproachTiltMaxKey"/> 가 0 이하면 아무것도 하지 않는다.
    /// 후보 틸트마다 용접선을 다시 환산하고(위치도 기울인 광축을 따라 standoff 만큼 물러난다), 목표 자세 IK 를 값싸게 거른 뒤,
    /// 통과한 앞의 <see cref="MaxTiltPlanAttempts"/> 개만 실제 진입 경로(<see cref="PlanarApproachAsync"/>, 모션 전 전부 검증)를 시도한다.
    /// 반환: (성공 결과 또는 null, 그때의 환산 결과, 실패 시 메시지에 덧붙일 설명).
    /// </summary>
    private async Task<(StepResult? Result, SeamApproachResult? Seam, string Note)> TryTiltedApproachAsync(
        SequenceContext context, double[] current, double[] reference, string twistNote, string entryNote, CancellationToken ct)
    {
        double maxDeg;
        try { maxDeg = await _param.GetDoubleAsync(ApproachTiltMaxKey) ?? 0; }
        catch { maxDeg = 0; }
        if (maxDeg <= 0) return (null, null, "");
        maxDeg = Math.Min(maxDeg, MaxApproachTiltDeg);

        // 법선 정면 진입이 도중 이동 실패(rc≠0)로 끝났으면 팔이 이미 움직였다 — 출발 자세 전제가 깨졌으므로 탐색하지 않는다.
        var now = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        if (!IsSamePose(now, current, ChainPoseTolMm, ChainPoseTolDeg))
            return (null, null, " · 접근 자세 탐색 생략(진입 도중 이동이 있었음)");

        var standoff = await GetApproachStandoffMmAsync(_param, _logger);
        var attempts = 0;
        var screened = 0;
        foreach (var (up, side) in TiltCandidates(maxDeg))
        {
            if (attempts >= MaxTiltPlanAttempts) break;
            var seam = await _seam.ResolveAsync(context, context.SeamStartW, context.SeamEndW, context.Tool,
                $"[Cobot 검사위치 이동 틸트 {up:+0;-0;0}/{side:+0;-0;0}°]", ct, standoff, up, side);
            if (seam.Blocker is not null || seam.Base is null || seam.ComputedPose is not { Length: 6 }) continue;

            var orientation = AlignOpticalAxisKeepTwist(current, seam.ComputedPose, seam.OpticalAxis);
            var target = new[] { seam.Base[0], seam.Base[1], seam.Base[2], orientation[3], orientation[4], orientation[5] };
            try { (target, _) = await AlignTwistToJ6Async(_cobot, target, context.Tool, VerticalJ6Deg, ct); }
            catch (InvalidOperationException) { /* 비틀림 정렬 실패 — 기울인 자세 그대로 시험 */ }

            IReadOnlyList<double[]> sols;
            try { sols = await _cobot.Rpc.GetInverseKinRankedAsync(target, reference, context.Tool, 0, ct); }
            catch (InvalidOperationException) { screened++; continue; }
            screened++;
            if (!TiltTargetUsable(reference, sols)) continue;

            attempts++;
            _logger.LogInformation("[Cobot 검사위치 이동] 접근 자세 탐색 — 광축 틸트 상하 {Up:+0;-0;0}° / 좌우 {Side:+0;-0;0}° 로 진입 시도 ({N}/{Max})",
                up, side, attempts, MaxTiltPlanAttempts);
            var tiltNote = $", 광축 틸트 상하 {up:+0;-0;0}°/좌우 {side:+0;-0;0}°(법선 정면 진입 불가 — 영상이 비스듬함)";
            var moved = await PlanarApproachAsync(context, seam, current, target, reference, twistNote + tiltNote, entryNote, ct);
            if (moved.Success) return (moved, seam, "");
            _logger.LogInformation("[Cobot 검사위치 이동] 틸트 {Up:+0;-0;0}/{Side:+0;-0;0}° 진입도 불가: {Msg}", up, side, moved.Message);

            // 이 후보가 도중까지 움직였으면 다음 후보의 진입 검증(관절 이동량·경로 연속성)이 출발 자세 기준이라 실제와 맞지 않는다 — 중단.
            var after = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            if (!IsSamePose(after, current, ChainPoseTolMm, ChainPoseTolDeg))
                return (moved, null,
                    $" · 접근 자세 탐색 중단 — 틸트 {up:+0;-0;0}/{side:+0;-0;0}° 진입이 도중에 실패해 팔이 이미 움직였습니다");
        }
        return (null, null, $" · 접근 자세 탐색(틸트 ≤{maxDeg:0}°): 후보 {screened}개 검사, 진입 시도 {attempts}개 — 쓸 수 있는 자세 없음");
    }

    /// <summary>
    /// 평면 경유 진입(후퇴 평면을 따라 이동, 진입 MoveJ 는 면 방향 <see cref="MaxEntryInPlaneMm"/> 이내)을 쓰는 면 코드 목록
    /// 파라미터 키 — 쉼표로 구분. 미설정이면 <see cref="DefaultPlanarEntryWalls"/>, "*" 면 모든 면(종전 동작), 빈 값이면 어느 면도 쓰지 않는다.
    /// 목록에 없는 면은 <b>직접 진입</b>이다: 면 방향 거리 제한 없이 들어갈 점까지 MoveJ 한 번에 가되, 움직이기 전에 그 관절 이동
    /// 경로를 따라가며 자기 간섭·면 여유를 본다(<see cref="JointPathBlocker"/>). ACS 리치 시뮬레이터 <c>Acs:Reach:PlanarEntryWalls</c> 와
    /// 같은 값이어야 한다 — 다르면 ACS 가 고른 정차를 여기서 거부하거나 그 반대가 된다.
    /// </summary>
    public const string PlanarEntryWallsKey = "inspection.planarEntryWalls";

    /// <summary>평면 경유 진입 기본 면 — 머리 위 경사면(실기 2026-11-02 PU: 접근점까지 한 번에 MoveJ 하다 팔이 면 쪽으로 휘둘림).</summary>
    internal const string DefaultPlanarEntryWalls = "PU,SU";

    /// <summary><paramref name="wallCode"/> 가 평면 경유 진입을 쓰는 면인가. <paramref name="setting"/> 은
    /// <see cref="PlanarEntryWallsKey"/> 값(null = 기본). 면 코드를 모르면 안전 쪽(평면 경유).</summary>
    internal static bool UsesPlanarEntry(string? setting, string? wallCode)
    {
        if (string.IsNullOrWhiteSpace(wallCode)) return true;
        var list = (setting ?? DefaultPlanarEntryWalls)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Any(w => w == "*" || string.Equals(w, wallCode.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> UsesPlanarEntryAsync(string? wallCode)
    {
        string? setting = null;
        try { setting = await _param.GetAsync(PlanarEntryWallsKey); }
        catch { /* DB 미준비 — 기본값 */ }
        return UsesPlanarEntry(setting, wallCode);
    }

    /// <summary>직접 진입 MoveJ 경로 점검 간격 — 관절별 최대 변화 [도].</summary>
    internal const double EntryPathStepDeg = 5.0;

    /// <summary>직접 진입 MoveJ 경로가 지켜야 할 면 여유 [mm]. 출발·도착 자세의 면 여유가 이보다 작으면 그 작은 쪽이 기준이다
    /// (경로가 양 끝보다 면에 더 가까워지지 않을 것).</summary>
    internal const double EntryPathMarginMm = 100.0;

    /// <summary>
    /// 직접 진입 MoveJ(관절 보간) 경로가 막히는 사유, 지나갈 수 있으면 null — 컨트롤러 없이 오프라인 기구학으로 본다.
    /// <paramref name="from"/> → <paramref name="to"/> 를 관절별 최대 <see cref="EntryPathStepDeg"/> 간격으로 나눈 중간 자세마다
    /// ① 자기 간섭(<paramref name="selfCollision"/> — 차체·기둥·신호탑)이 없고 ② 면 여유(<paramref name="wallClearance"/>)가
    /// min(<see cref="EntryPathMarginMm"/>, 출발 여유, 도착 여유) 아래로 내려가지 않는지 본다.
    /// 평면 경유가 하던 "팔이 면 쪽으로 휘둘리지 않는가"를 경로 자체로 확인한다.
    /// </summary>
    internal static string? JointPathBlocker(double[] from, double[] to,
        Func<double[], string?>? selfCollision, Func<double[], (double ClearanceMm, string Part)>? wallClearance)
    {
        var n = Math.Min(from.Length, to.Length);
        var steps = (int)Math.Ceiling(MaxJointTravel(from, to) / EntryPathStepDeg);
        if (steps <= 1) return null;
        double[] At(double t) => to.Select((v, i) => i < n ? from[i] + (v - from[i]) * t : v).ToArray();
        var floor = wallClearance is null
            ? double.NegativeInfinity
            : Math.Min(EntryPathMarginMm, Math.Min(wallClearance(At(0)).ClearanceMm, wallClearance(to).ClearanceMm)) - 1.0;
        for (var k = 1; k < steps; k++)
        {
            var j = At((double)k / steps);
            if (selfCollision?.Invoke(j) is not null)
                return $"관절 이동 경로 {100.0 * k / steps:0}% 지점 자기 간섭";
            if (wallClearance?.Invoke(j) is { } w && w.ClearanceMm < floor)
                return $"관절 이동 경로 {100.0 * k / steps:0}% 지점 면 여유 {w.ClearanceMm:0}mm < {floor + 1:0}mm ({w.Part})";
        }
        return null;
    }

    internal const double DefaultPlanarRetreatMm = 100.0;
    internal const double MaxPlanarRetreatMm = 400.0;

    /// <summary>진입점→들어갈 점의 면 방향 거리가 이 값 미만이면 면 방향 이동을 생략한다 [mm].</summary>
    internal const double PlanarSkipMm = 50.0;

    /// <summary>면 방향 직선 이동 경로를 역기구학으로 검증하는 간격 [mm].</summary>
    internal const double PlanarSampleMm = 100.0;

    /// <summary>경로 검증의 손목 특이점 여유 — |J5| 가 이보다 작으면 MoveL 이 rc=38 로 거부되거나 손목이 급회전한다 [도].</summary>
    internal const double PlanarWristMarginDeg = PostureLimits.DefaultWristMarginDeg;

    /// <summary>평면 경유 접근 계획 — 위치만(BASE mm). 자세는 전 구간 접근점 자세로 고정한다.</summary>
    /// <param name="Entry">진입점 — 현재 TCP 를 후퇴 평면으로 면 법선 방향 투영한 점.</param>
    /// <param name="Plunge">들어갈 점 — 접근점에서 면 법선 바깥으로 후퇴 거리만큼 물러난 점.</param>
    /// <param name="InPlaneMm">진입점→들어갈 점 거리(면 방향).</param>
    internal sealed record PlanarApproachPlan(double[] Entry, double[] Plunge, double InPlaneMm);

    /// <summary>
    /// 접근점 <paramref name="approach"/> 의 면 이격 평면에서 <paramref name="retreatMm"/> 더 물러난 후퇴 평면 위의
    /// 진입점·들어갈 점을 정한다. <paramref name="wallOut"/> 은 면 바깥 단위 법선(<see cref="WallOutNormal"/>).
    /// </summary>
    internal static PlanarApproachPlan PlanPlanarApproach(double[] cur, double[] approach, double[] wallOut, double retreatMm)
    {
        var h = 0.0;
        for (var i = 0; i < 3; i++) h += (cur[i] - approach[i]) * wallOut[i];
        var entry = new double[3];
        var plunge = new double[3];
        for (var i = 0; i < 3; i++)
        {
            entry[i] = cur[i] - (h - retreatMm) * wallOut[i];
            plunge[i] = approach[i] + retreatMm * wallOut[i];
        }
        var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (plunge[i] - entry[i]) * (plunge[i] - entry[i])));
        return new PlanarApproachPlan(entry, plunge, d);
    }

    /// <summary><paramref name="from"/>→<paramref name="to"/> 직선을 <paramref name="stepMm"/> 이하 간격으로 나눈 점들(시작점 제외, 끝점 포함).</summary>
    internal static List<double[]> SampleSegment(double[] from, double[] to, double stepMm)
    {
        var len = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (to[i] - from[i]) * (to[i] - from[i])));
        var n = Math.Max(1, (int)Math.Ceiling(len / stepMm));
        var pts = new List<double[]>(n);
        for (var k = 1; k <= n; k++)
        {
            var t = (double)k / n;
            pts.Add(new[] { from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t, from[2] + (to[2] - from[2]) * t });
        }
        return pts;
    }

    private async Task<double> GetPlanarRetreatMmAsync()
    {
        try
        {
            var v = await _param.GetDoubleAsync(PlanarRetreatKey);
            if (v is >= 0 and <= MaxPlanarRetreatMm) return v.Value;
            if (v is not null)
                _logger.LogWarning("'{Key}' 값 {Raw} 이(가) 범위(0~{Max:0}mm) 밖 — 기본 {Default:0}mm 사용",
                    PlanarRetreatKey, v, MaxPlanarRetreatMm, DefaultPlanarRetreatMm);
        }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }
        return DefaultPlanarRetreatMm;
    }

    /// <summary>진입 MoveJ 의 관절별 최대 이동량 [도]. 넘으면 그 진입점은 쓰지 않는다 — 손목·팔을 크게 휘두르는 해다
    /// (실기 2026-11-02 13:23 L4-R03C09 H1: 투영 진입점 해가 J4 를 +69°→약 −150° 로, 벽 반대쪽으로 ~220° 돌렸다).
    /// 120° → 140° 완화(ACS 분석, 2026-11-02): 당일 300° 이상 거부는 전부 PU 준비 자세 J4 69° 가 한계 +85° 에 붙어
    /// 생긴 J4 먼 길 회전이었고(준비 자세 문제), 정상 진입은 16~82° 였다. 완화로 통과할 수 있는 손목 뒤집기 해
    /// (14:05 후보: J1 117°·J5 +90→−50°, 144°)는 <see cref="IsWristFlip"/> 로 따로 막는다.</summary>
    internal const double MaxEntryJointTravelDeg = 140.0;

    /// <summary>
    /// 진입 MoveJ 해 <paramref name="joints"/> 를 쓸 수 없는 사유, 쓸 수 있으면 null — 관절 이동량(<see cref="MaxEntryJointTravelDeg"/>),
    /// 팔꿈치·손목 가지 전환, 도착 |J5| 여유, J6 안전 범위, (주면) 자기 간섭 순으로 본다.
    /// </summary>
    internal static string? EntryJointBlocker(double[] reference, double[] joints, Func<double[], string?>? selfCollision = null)
    {
        var travel = MaxJointTravel(reference, joints);
        if (travel > MaxEntryJointTravelDeg) return $"진입 관절 이동 {travel:0}° > {MaxEntryJointTravelDeg:0}°";
        if (FairinoRpcClient.IsElbowFlip(reference, joints)) return "팔꿈치 가지 전환";
        if (IsWristFlip(reference, joints)) return $"손목 가지 전환(J5 {reference[4]:0}° → {joints[4]:0}°)";
        if (Math.Abs(joints[4]) < PlanarWristMarginDeg) return $"손목 특이점(|J5| {Math.Abs(joints[4]):0.0}° < {PlanarWristMarginDeg:0}°)";
        if (WObjAttitude.J6RangeBlocker(reference[5], joints[5]) is { } j6) return j6;
        if (selfCollision?.Invoke(joints) is not null) return "자기 간섭";
        return null;
    }

    /// <summary>손목 가지가 바뀌는지 — J5 부호가 다르면 손목이 뒤집히며(J5=0 특이점 통과) J4·J6 이 크게 돈다.</summary>
    internal static bool IsWristFlip(double[] reference, double[] joints)
        => Math.Sign(reference[4]) != 0 && Math.Sign(joints[4]) != 0 && Math.Sign(reference[4]) != Math.Sign(joints[4]);

    /// <summary>직선 이동 경로 검증 점 사이(≤ <see cref="PlanarSampleMm"/>) 관절별 최대 변화 [도]. 넘으면 그 사이에서 해가
    /// 다른 가지로 갈아탄 것이라 MoveL 이 손목을 급회전하거나 거부한다(같은 13:23 실행: 433° 점프를 통과시켰다).</summary>
    internal const double MaxPathJointStepDeg = 30.0;

    /// <summary>두 관절각 사이 관절별 변화의 최댓값 [도].</summary>
    internal static double MaxJointTravel(double[] from, double[] to)
    {
        var m = 0.0;
        for (var i = 0; i < Math.Min(from.Length, to.Length); i++) m = Math.Max(m, Math.Abs(to[i] - from[i]));
        return m;
    }

    /// <summary>경로 검증 점의 관절이 하드웨어 한계(<see cref="FairinoRpcClient.HardJointMinDeg"/>)에서 지켜야 할 여유 [도].
    /// 검증 점 사이(≤ <see cref="PlanarSampleMm"/>)에서 더 움직이는 몫을 덮는다 — 실기 2026-11-09 09:20 주행 전 복귀의
    /// 수평 벽면 후퇴: J4 75.8° 에서 출발해 직선 이동 중 +85° 한계에 닿아 정지(main=1, sub=19).</summary>
    internal const double PathJointLimitMarginDeg = 2.0;

    /// <summary>관절각이 하드웨어 한계에서 <see cref="PathJointLimitMarginDeg"/> 안쪽이 아니면 사유, 안쪽이면 null.
    /// J6 은 하드웨어 범위로 묶지 않는다(J6 안전 범위는 호출측 규칙).</summary>
    internal static string? JointLimitBlocker(double[] joints)
    {
        for (var i = 0; i < Math.Min(joints.Length, FairinoRpcClient.HardJointMinDeg.Length); i++)
        {
            double lo = FairinoRpcClient.HardJointMinDeg[i], hi = FairinoRpcClient.HardJointMaxDeg[i];
            if (joints[i] < lo + PathJointLimitMarginDeg || joints[i] > hi - PathJointLimitMarginDeg)
                return $"J{i + 1} 관절 한계 근접({joints[i]:0.0}° ∉ [{lo:0}+{PathJointLimitMarginDeg:0}, {hi:0}−{PathJointLimitMarginDeg:0}])";
        }
        return null;
    }

    /// <summary>
    /// 직선 이동 경로의 연속 두 점 해(<paramref name="prev"/>→<paramref name="next"/>)가 이어지지 않는 사유, 이어지면 null.
    /// 관절 점프·팔꿈치 가지 전환·손목 특이점(J5 부호 반전/여유 부족)·관절 한계 근접을 본다 — 기준 가지는 진입점 해 <paramref name="entry"/>.
    /// </summary>
    internal static string? PathStepBlocker(double[] prev, double[] next, double[] entry)
    {
        if (JointLimitBlocker(next) is { } limit) return limit;
        var jump = MaxJointTravel(prev, next);
        if (jump > MaxPathJointStepDeg)
            return $"관절이 {jump:0}° 튐(> {MaxPathJointStepDeg:0}°/{PlanarSampleMm:0}mm) — 경로 도중 해 가지가 바뀜";
        if (FairinoRpcClient.IsElbowFlip(entry, next))
            return $"팔꿈치 가지 전환(J3 {entry[2]:0.0}° → {next[2]:0.0}°)";
        if (Math.Sign(next[4]) != Math.Sign(entry[4]) || Math.Abs(next[4]) < PlanarWristMarginDeg)
            return $"손목 특이점(J5 {entry[4]:0.0}° → {next[4]:0.0}°)";
        return null;
    }

    /// <summary>
    /// 진입 MoveJ 가 후퇴 평면에서 현재 TCP 투영점으로부터 갈 수 있는 최대 면 방향 거리 [mm]. 관절 이동량만 보면
    /// 관절은 조금 움직여도 TCP 가 경사면을 따라 크게 휘는 MoveJ 를 막지 못한다(실기 2026-11-02 13:45·13:48
    /// L4-R03C09 #3: 관절 최대 82° 라 통과해 챔퍼 아래쪽 끝까지 한 번에 MoveJ → EMO). 이보다 먼 구간은 반드시
    /// 후퇴 평면을 따라 MoveL 로 간다. H1(면 방향 384mm)은 종전처럼 바로 들어간다.
    /// </summary>
    internal const double MaxEntryInPlaneMm = 500.0;

    /// <summary>
    /// 진입점 후보 순서 — ① 들어갈 점 자체(면 방향 이동 없이 바로 진입: 투영점에서 <see cref="MaxEntryInPlaneMm"/> 이내일 때만),
    /// ② 현재 TCP 투영점에서 들어갈 점 쪽으로 <see cref="PlanarSampleMm"/> 간격(투영점 쪽부터 = 진입 MoveJ 가 짧은 순,
    /// 투영점에서 <see cref="MaxEntryInPlaneMm"/> 이내까지). 면 방향 거리가 짧으면 ①만.
    /// </summary>
    /// <param name="planar">false = 직접 진입(<see cref="PlanarEntryWallsKey"/> 에 없는 면) — 면 방향 거리 제한 없이
    /// 들어갈 점 직접을 맨 앞에 두고, 투영점부터 들어갈 점 앞까지 전부 후보로 둔다(진입 MoveJ 는 경로 점검을 거친다).</param>
    internal static List<double[]> EntryCandidates(PlanarApproachPlan plan, bool planar = true)
    {
        if (plan.InPlaneMm < PlanarSkipMm) return new List<double[]> { plan.Plunge };

        var cap = planar ? MaxEntryInPlaneMm : double.PositiveInfinity;
        var list = new List<double[]>();
        if (plan.InPlaneMm <= cap) list.Add(plan.Plunge);
        list.Add(plan.Entry);
        var pts = SampleSegment(plan.Entry, plan.Plunge, PlanarSampleMm);
        var step = plan.InPlaneMm / pts.Count;
        for (var k = 0; k < pts.Count - 1; k++)   // 마지막 점 = 들어갈 점(①에서 따로 다룸)
            if ((k + 1) * step <= cap + 1e-6) list.Add(pts[k]);
        return list;
    }

    /// <summary>
    /// 평면 경유 접근 — 진입점까지 MoveJ → (진입점이 들어갈 점이 아니면) 후퇴 평면을 따라 들어갈 점까지 MoveL
    /// → 면 법선 방향으로 접근점까지 MoveL. 자세는 전 구간 접근점 자세(면 법선·J6 정렬)로 고정한다.
    /// 진입점은 <see cref="EntryCandidates"/> 순서로 고르되, 진입 MoveJ 가 관절별 <see cref="MaxEntryJointTravelDeg"/> 이내·
    /// 같은 팔꿈치 가지·자기 간섭 없음이고 그 뒤 경로 전체가 <see cref="PathStepBlocker"/> 를 통과하는 첫 후보다.
    /// 모든 판정은 움직이기 전에 끝내고, 맞는 후보가 없으면 움직이지 않고 실패한다.
    /// </summary>
    private async Task<StepResult> PlanarApproachAsync(
        SequenceContext context, SeamApproachResult seam, double[] current, double[] target, double[] reference,
        string twistNote, string entryNote, CancellationToken ct)
    {
        var retreat = await GetPlanarRetreatMmAsync();
        var wallOut = WallOutNormal(seam.ComputedPose!, seam.OpticalAxis);
        var plan = PlanPlanarApproach(current, target, wallOut, retreat);
        double[] Pose(double[] p) => new[] { p[0], p[1], p[2], target[3], target[4], target[5] };
        string Fmt(double[] v) => string.Join(", ", v.Select(x => x.ToString("0")));

        var guardOn = await SelfCollisionGuardOnAsync();
        // 평면 경유를 쓰지 않는 면은 직접 진입 — 면 방향 거리 제한을 두지 않는 대신 진입 MoveJ 의 관절 이동 경로를 점검한다.
        var planarEntry = await UsesPlanarEntryAsync(context.WallCode);
        var candidates = EntryCandidates(plan, planarEntry);
        var rejects = new List<string>();
        // 면 위의 한 점 — 용접선 점(없으면 접근점에서 후퇴 평면 반대쪽으로 볼 수 없으므로 접근점 자체: 여유를 이격만큼 작게 본다).
        var wallPoint = seam.SeamPointBase is { Length: 3 } sp ? sp : target.Take(3).ToArray();
        string? PathBlocker(double[] j) => planarEntry
            ? null
            : JointPathBlocker(reference, j,
                guardOn ? q => SelfCollisionBlocker(q, seam) : null,
                q => CobotEnvelope.Default.WallClearance(Fr5Kinematics.Fr5Wml, q, wallPoint, wallOut));

        // ── 진입점 선택 + 경로 검증(모션 없음) ────────────────────────────
        double[]? entryPos = null, jEntry = null, jPlunge = null, jTarget = null;
        foreach (var cand in candidates)
        {
            var label = ReferenceEquals(cand, plan.Plunge) ? "들어갈 점 직접" : $"후퇴 평면 [{Fmt(cand)}]";
            // 진입 해는 J6 와인딩 유지 순으로 전부 받아 첫 통과 해를 쓴다 — 예전에는 1등 해 하나만 보고, 그 해가 손목을
            // 뒤집은 해면 같은 자리의 다른(손목 유지) 해가 있어도 이 진입점을 버렸다(실기 2026-11-06 PU "후보 전부 거부").
            IReadOnlyList<double[]> sols;
            try { sols = await _cobot.Rpc.GetInverseKinRankedAsync(Pose(cand), reference, context.Tool, 0, ct); }
            catch (InvalidOperationException) { rejects.Add($"{label}: 도달 불가"); continue; }

            double[]? picked = null;
            string? firstWhy = null;
            for (var si = 0; si < sols.Count && picked is null; si++)
            {
                var solWhy = EntryJointBlocker(reference, sols[si], guardOn ? j => SelfCollisionBlocker(j, seam) : null)
                             ?? PathBlocker(sols[si]);
                if (solWhy is null) picked = sols[si];
                else firstWhy ??= solWhy;
            }
            if (picked is null) { rejects.Add($"{label}: {firstWhy}{(sols.Count > 1 ? $" (해 {sols.Count}개 모두)" : "")}"); continue; }
            var jc = picked;
            if (!ReferenceEquals(jc, sols[0]))
                _logger.LogInformation("[Cobot 검사위치 이동] {Label}: 1순위 해 거부({Why}) — 다른 IK 해 [{J}] 사용",
                    label, firstWhy, string.Join(", ", jc.Select(v => v.ToString("0.0"))));

            // 진입점 → 들어갈 점(면 방향) → 접근점(법선) 경로
            var path = new List<(double[] Pt, bool Planar)>();
            if (!ReferenceEquals(cand, plan.Plunge))
                path.AddRange(SampleSegment(cand, plan.Plunge, PlanarSampleMm).Select(p => (p, true)));
            if (retreat > 0.5)
                path.AddRange(SampleSegment(plan.Plunge, target, PlanarSampleMm).Select(p => (p, false)));

            var seed = jc;
            double[] jp = jc;
            string? why = null;
            foreach (var (pt, planar) in path)
            {
                double[] j;
                try { j = await _cobot.Rpc.GetInverseKinNearestAsync(Pose(pt), seed, context.Tool, 0, ct); }
                catch (InvalidOperationException) { why = $"[{Fmt(pt)}] 도달 불가"; break; }
                if (PathStepBlocker(seed, j, jc) is { } step) { why = $"[{Fmt(pt)}] {step}"; break; }
                if (Inspection.WObjAttitude.J6RangeBlocker(reference[5], j[5]) is { } j6Path) { why = $"[{Fmt(pt)}] {j6Path}"; break; }
                if (guardOn && SelfCollisionBlocker(j, seam) is not null) { why = $"[{Fmt(pt)}] 자기 간섭"; break; }
                seed = j;
                if (planar) jp = j;
            }
            if (why is not null) { rejects.Add($"{label}: 경로 {why}"); continue; }

            (entryPos, jEntry, jPlunge, jTarget) = (cand, jc, jp, seed);
            break;
        }

        if (entryPos is null)
            return StepResult.Fail(
                "[Cobot 검사위치 이동] 안전한 진입 경로 없음 — 움직이지 않았습니다. " +
                $"(접근점은 현재 TCP 정면에서 면을 따라 {plan.InPlaneMm:0}mm, " +
                (planarEntry ? $"진입 MoveJ 는 {MaxEntryInPlaneMm:0}mm 이내만 허용) " : "직접 진입 — 관절 이동 경로 점검) ") +
                $"후보 {candidates.Count}개 모두 거부: {string.Join(" / ", rejects.Take(4))}" +
                (rejects.Count > 4 ? $" 외 {rejects.Count - 4}건" : "") +
                ". 정차 위치를 용접선 쪽으로 옮기거나 이 면의 검사 준비 자세를 확인하세요.");

        var direct = ReferenceEquals(entryPos, plan.Plunge);
        var planarMm = direct ? 0.0 : Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (plan.Plunge[i] - entryPos[i]) * (plan.Plunge[i] - entryPos[i])));
        _logger.LogInformation(
            "[Cobot 검사위치 이동] {Mode}: 진입점 [{E}] (진입 관절 이동 최대 {T:0}°) → 면 방향 {D:0}mm → 들어갈 점 [{P}] → 법선 {R:0}mm 진입" +
            "{Rej}",
            planarEntry ? "평면 경유 접근" : "직접 진입(경로 점검 통과)", Fmt(entryPos), MaxJointTravel(reference, jEntry!), planarMm, Fmt(plan.Plunge), retreat,
            rejects.Count == 0 ? "" : $" (앞 후보 거부: {string.Join(" / ", rejects)})");

        // ── 실행 ─────────────────────────────────────────────────────────
        var rc = await _cobot.Rpc.MoveJAsync(jEntry!, Pose(entryPos), tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"진입점 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        // 진입점에 선 순간부터 복귀는 이 길을 거슬러 가야 한다 — 뒤 이동이 실패·정지해도 기록은 남긴다.
        ApproachPathMemory.Record(new PlanarApproachRecord(
            wallOut, target.Take(3).ToArray(), retreat, entryPos.ToArray(), context.WallCode));

        if (!direct)
        {
            rc = await _cobot.Rpc.MoveLAsync(Pose(plan.Plunge), jPlunge!, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
            if (rc != 0)
                return StepResult.Fail($"후퇴 평면 면 방향 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
        }

        if (retreat > 0.5)
        {
            rc = await _cobot.Rpc.MoveLAsync(target, jTarget!, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
            if (rc != 0)
                return StepResult.Fail($"용접선 접근점 법선 진입 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
        }

        var route = direct
            ? $"후퇴 {retreat:0}mm 지점 관절 이동(MoveJ) → 법선 진입(MoveL)"
            : $"후퇴 평면 진입(MoveJ) → 면 방향 {planarMm:0}mm 직선 이동(MoveL) → 법선 {retreat:0}mm 진입(MoveL)";
        return StepResult.Ok(
            $"진입 경유점 → {seam.Note} {route} 완료 [자세=면 법선{twistNote}, J6={jTarget![5]:0.0}°].{entryNote}");
    }

    /// <summary>anchor 적중 task — Bag 의 캐시 프레임(직전 task 위치)을 이번 시작 접근점으로 평행 이동한다.
    /// 캐시 프레임이 없으면(신규 anchor) 할 일 없음. 프레임은 있는데 기준 시작점이 없으면 이동량을 알 수 없어
    /// 직전 구간을 다시 돌게 되므로 실패.</summary>
    private string? ShiftAnchorFrame(SequenceContext context, double[] startBase)
    {
        if (!context.Bag.TryGetValue(InternalWorkpieceFrame.BagKey, out var fv) || fv is not double[] { Length: 6 } frame)
            return null;
        if (!context.Bag.TryGetValue(AnchorStartBaseBagKey, out var av) || av is not double[] { Length: 3 } anchorStart)
            return "[Cobot 검사위치 이동] anchor 공유 프레임의 기준 시작점이 없어 이번 용접선 위치로 옮길 수 없습니다 — " +
                   "직전 구간을 다시 검사하게 되므로 중단합니다. 같은 그룹을 처음부터(정렬 포함) 다시 실행하세요.";

        // 비드 방향(프레임 X)으로만 옮긴다 — ACS 시작점 차이의 옆 성분(맵·AMR 방향 오차)이 LINE 마다 누적되지 않게.
        var (moved, lateralMm, normalMm, alongX) = InternalWorkpieceFrame.TranslateAlongX(frame, anchorStart, startBase);
        context.Bag[InternalWorkpieceFrame.BagKey] = moved;
        if (alongX)
            _logger.LogInformation(
                "[Cobot 검사위치 이동] anchor 프레임 평행 이동(비드 방향만) Δ=[{Dx:0.0}, {Dy:0.0}, {Dz:0.0}]mm (ACS 옆 성분 {L:+0.0;-0.0}mm·법선 성분 {N:+0.0;-0.0}mm 제외) — 원점 [{X:0.0}, {Y:0.0}, {Z:0.0}]",
                moved[0] - frame[0], moved[1] - frame[1], moved[2] - frame[2], lateralMm, normalMm, moved[0], moved[1], moved[2]);
        else
            _logger.LogWarning(
                "[Cobot 검사위치 이동] anchor 프레임 평행 이동 — ACS 옆 성분 {L:+0.0;-0.0}mm 가 {Max:0}mm 초과(같은 그룹 task 가 옆으로 떨어져 있음) → 면 평행 이동 Δ=[{Dx:0.0}, {Dy:0.0}, {Dz:0.0}]mm (법선 성분 {N:+0.0;-0.0}mm 제외) — 원점 [{X:0.0}, {Y:0.0}, {Z:0.0}]",
                lateralMm, InternalWorkpieceFrame.MaxDroppedLateralMm,
                moved[0] - frame[0], moved[1] - frame[1], moved[2] - frame[2], normalMm, moved[0], moved[1], moved[2]);
        return null;
    }

    /// <summary>
    /// 같은 anchor 그룹 연속 task — 직전 task 종료 자세에서 진입 준비(후퇴·홈 위치_2·검사 준비 왕복) 없이 다음 시작
    /// 접근점으로 간다(사양 §8.1 "액션 사이 홈 왕복 금지"). 조건이 하나라도 안 맞으면 null — 호출측이 종전 경로로 간다.
    /// 경로: ① 면에 연속 이동 이격(<see cref="ChainStandoffKey"/>, 기본 100mm)보다 가까우면 면 법선 방향으로만 그 면까지
    /// 들어 올림(자세 유지) → ② 그 면을 따라 시작점 정면으로 MoveL(같은 면 이격 유지, 광축은 면 법선, 광축 둘레 비틀림은
    /// 직전 종료 자세 유지 — J6 180° 정렬 안 함). 첫 진입 접근점(400mm)까지 빠지지 않는다 — ⑱이 곧바로 첫 경유점으로
    /// 다시 들어오므로 그 왕복은 헛이동이다.
    /// </summary>
    private async Task<StepResult?> TryChainFromPreviousAsync(
        SequenceContext context, SeamApproachResult seam, double approachStandoffMm, CancellationToken ct)
    {
        if (!context.Bag.TryGetValue(PreviousEndTcpBagKey, out var pv) || pv is not double[] { Length: 6 } prevEnd)
            return null;
        if (seam.ComputedPose is not { Length: 6 } computed || seam.Base is not { Length: 3 } startBase)
        {
            _logger.LogInformation("[Cobot 검사위치 이동] 연속 이동 생략 — 면 법선 자세 없음(wall_code) → 진입 준비 경로");
            return null;
        }

        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        if (!IsSamePose(cur, prevEnd, ChainPoseTolMm, ChainPoseTolDeg))
        {
            _logger.LogInformation(
                "[Cobot 검사위치 이동] 연속 이동 생략 — 현재 TCP [{Cur}] 가 직전 종료 [{Prev}] 와 다름(조그·정지 의심) → 진입 준비 경로",
                string.Join(", ", cur.Select(v => v.ToString("0.#"))), string.Join(", ", prevEnd.Select(v => v.ToString("0.#"))));
            return null;
        }

        // 비틀림은 직전 종료 자세 그대로 — 같은 그룹은 검사 방향·프레임 자세가 같아 ⑱이 고를 비틀림과 거의 같다.
        // 진입 경로처럼 J6 180° 로 맞추면 ⑱이 곧바로 되돌린다(2026-10-30 #3: −284.7°→−180°→−284.8°).
        var orientation = AlignOpticalAxisKeepTwist(cur, computed, seam.OpticalAxis);
        var wallOut = WallOutNormal(computed, seam.OpticalAxis);
        var chainStandoffMm = await GetChainStandoffMmAsync(approachStandoffMm);
        var chainBase = ChainTargetBase(startBase, wallOut, approachStandoffMm, chainStandoffMm);
        // 이어지는 용접선(앞 끝 = 다음 시작) — 띄우지 않고 지금 높이에서 시작점 정면으로만 맞춘다. ⑱이 곧바로 첫 경유점으로 간다.
        var continuing = ContinuingSeamTargetBase(cur, chainBase, wallOut, chainStandoffMm, await GetChainContinueTolMmAsync());
        var heightMm = chainStandoffMm;
        if (continuing is not null)
        {
            heightMm = chainStandoffMm + (cur[0] - chainBase[0]) * wallOut[0] + (cur[1] - chainBase[1]) * wallOut[1]
                     + (cur[2] - chainBase[2]) * wallOut[2];
            chainBase = continuing;
        }
        var target =new[] { chainBase[0], chainBase[1], chainBase[2], orientation[3], orientation[4], orientation[5] };
        var lift = PlanChainLift(cur, chainBase, wallOut);
        var liftEnd = lift is null
            ? cur
            : new[] { cur[0] + lift[0], cur[1] + lift[1], cur[2] + lift[2], cur[3], cur[4], cur[5] };

        // 들어 올림·MoveL 은 직교 보간이라 도착 관절은 현재 자세에서 '연속으로 이어지는' 해로 정해진다. 예전에는 도착점만
        // keep-wrist IK 로 풀어 손목 반전(J5 부호)·특이점 여유를 보지 않았고, 그 해가 손목을 뒤집은 해면 MoveL 이 도중에
        // J5=0 을 지나 rc=38/185 로 멈췄다. 이제 경로를 샘플링해 이어 풀고(평면 진입과 같은 PathStepBlocker),
        // 마지막 해를 지령한다. 하나라도 걸리면 움직이지 않고 종전 진입 준비 경로로 넘긴다.
        double[] joints;
        double[] reference;
        try
        {
            reference = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
            var path = new List<double[]>();
            if (lift is not null) path.AddRange(SamplePoseSegment(cur, liftEnd, PlanarSampleMm, ChainSampleDeg));
            path.AddRange(SamplePoseSegment(liftEnd, target, PlanarSampleMm, ChainSampleDeg));

            var (pathJoints, pathBlocker) = await SolvePosePathAsync(_cobot, path, reference, context.Tool, ct);
            if (pathBlocker is not null)
            {
                _logger.LogWarning("[Cobot 검사위치 이동] 연속 이동 생략 — 경로 {Msg} → 진입 준비 경로", pathBlocker);
                return null;
            }
            joints = pathJoints!;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("[Cobot 검사위치 이동] 연속 이동 생략 — 경로 자세 계산 실패: {Msg} → 진입 준비 경로", ex.Message);
            return null;
        }
        if (ElbowFlipBlocker(reference, joints) is { } flip)
        {
            _logger.LogWarning("[Cobot 검사위치 이동] 연속 이동 생략 — {Msg} → 진입 준비 경로", flip);
            return null;
        }
        if (Inspection.WObjAttitude.J6RangeBlocker(reference[5], joints[5]) is { } j6Range)
        {
            _logger.LogWarning("[Cobot 검사위치 이동] 연속 이동 생략 — {Msg} → 진입 준비 경로", j6Range);
            return null;
        }
        if (await SelfCollisionGuardOnAsync() && SelfCollisionBlocker(joints, seam) is { } selfHit)
        {
            _logger.LogWarning("[Cobot 검사위치 이동] 연속 이동 생략 — {Msg} → 진입 준비 경로", selfHit);
            return null;
        }

        var liftNote = "";
        if (lift is not null)
        {
            var lrc = await _cobot.Rpc.MoveByOffsetAsync(cur, user: 0, lift, tool: context.Tool, vel: context.Velocity, ct: ct);
            if (lrc != 0)
            {
                _logger.LogWarning("[Cobot 검사위치 이동] 연속 이동 들어 올림 실패 (rc={Rc}){Desc} → 진입 준비 경로",
                    lrc, FairinoErrorCodes.Suffix(lrc));
                return null;
            }
            liftNote = $", 면 법선 방향 {Math.Sqrt(lift[0] * lift[0] + lift[1] * lift[1] + lift[2] * lift[2]):0}mm 들어 올림";
        }

        var rc = await _cobot.Rpc.MoveLAsync(target, joints, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
        {
            _logger.LogWarning("[Cobot 검사위치 이동] 연속 이동 MoveL 실패 (rc={Rc}){Desc} → 진입 준비 경로",
                rc, FairinoErrorCodes.Suffix(rc));
            return null;
        }

        var dist = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (target[i] - cur[i]) * (target[i] - cur[i])));
        var contNote = continuing is null ? "" : ", 이어지는 용접선 — 띄우지 않음";
        _logger.LogInformation("[Cobot 검사위치 이동] 연속 이동 완료 — 직전 종료 자세에서 {Dist:0}mm{Lift}{Cont} (면 이격 {Height:0}mm, 연속 이동 설정 {Chain:0}mm)",
            dist, liftNote, contNote, heightMm, chainStandoffMm);
        return StepResult.Ok(
            $"연속 이동(복귀 생략) — 직전 종료 자세에서 용접선 시작점 정면(면 이격 {heightMm:0}mm, 첫 진입 {approachStandoffMm:0}mm 대신) " +
            $"직선 이동(MoveL) {dist:0}mm{liftNote}{contNote} " +
            $"[자세=면 법선, 비틀림 직전 종료 유지, J6={joints[5]:0.0}°].");
    }

    /// <summary>면 바깥(벽에서 멀어지는) 단위 법선 — 계산 자세의 광축(면을 향함)의 반대.</summary>
    internal static double[] WallOutNormal(double[] computedPose, ToolAxisDir opticalAxis)
    {
        var m = FrameMath.PoseToMatrix(computedPose);
        var col = (int)opticalAxis / 2;
        var sgn = (int)opticalAxis % 2 == 0 ? 1.0 : -1.0;
        return new[] { -sgn * m[0, col], -sgn * m[1, col], -sgn * m[2, col] };
    }

    /// <summary>연속 이동 들어 올림 — 현재 TCP 가 접근점의 면 이격 평면보다 면에 가까우면 그 평면까지 면 법선 방향으로만
    /// 옮기는 BASE 오프셋 [dx,dy,dz,0,0,0]. 이미 그 평면 이상이면(1mm 허용) null.</summary>
    internal static double[]? PlanChainLift(double[] cur, double[] approachBase, double[] wallOut)
    {
        var h = (cur[0] - approachBase[0]) * wallOut[0] + (cur[1] - approachBase[1]) * wallOut[1]
              + (cur[2] - approachBase[2]) * wallOut[2];
        if (h >= -1.0) return null;
        return new[] { -h * wallOut[0], -h * wallOut[1], -h * wallOut[2], 0.0, 0.0, 0.0 };
    }

    /// <summary>
    /// 직교 경로 샘플(<see cref="SamplePoseSegment"/>)을 <paramref name="startJoints"/> 에서 시작해 앞 점 해 기준 최소 변위 IK 로
    /// 이어 풀고, 점마다 <see cref="PathStepBlocker"/>(관절 점프·팔꿈치·손목 특이점)와 J6 안전 범위(출발 감김 기준)를 본다.
    /// 통과하면 (마지막 점의 해, null) — MoveL 에 그대로 지령할 도착 관절이다. 걸리면 (null, "[x, y, z] 사유").
    /// 도달 불가는 사유로 돌려준다(예외 아님). 코봇은 움직이지 않는다.
    /// </summary>
    internal static async Task<(double[]? Joints, string? Blocker)> SolvePosePathAsync(
        CobotService cobot, IEnumerable<double[]> poses, double[] startJoints, int tool, CancellationToken ct)
    {
        var seed = startJoints;
        foreach (var pose in poses)
        {
            var at = string.Join(", ", pose.Take(3).Select(v => v.ToString("0")));
            double[] j;
            try { j = await cobot.Rpc.GetInverseKinNearestAsync(pose, seed, tool, 0, ct); }
            catch (InvalidOperationException) { return (null, $"[{at}] 도달 불가"); }
            if (PathStepBlocker(seed, j, startJoints) is { } step) return (null, $"[{at}] {step}");
            if (WObjAttitude.J6RangeBlocker(startJoints[5], j[5]) is { } j6) return (null, $"[{at}] {j6}");
            seed = j;
        }
        return (seed, null);
    }

    /// <summary>연속 이동 경로 검증의 자세 샘플 간격 [도] — 위치가 짧아도 자세가 많이 돌면 이 간격으로 더 나눈다.</summary>
    internal const double ChainSampleDeg = 10.0;

    /// <summary>
    /// 직교 직선 이동(MoveL) 경로를 위치 <paramref name="stepMm"/>·회전 <paramref name="stepDeg"/> 이하 간격으로 나눈
    /// BASE pose 들(시작 제외, 끝 포함). 위치는 선형, 자세는 상대 회전(축-각)을 같은 비율로 보간한다.
    /// 상대 회전이 180° 근방(축 퇴화)이면 자세는 끝 자세로 둔다.
    /// </summary>
    internal static List<double[]> SamplePoseSegment(double[] from, double[] to, double stepMm, double stepDeg)
    {
        var len = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (to[i] - from[i]) * (to[i] - from[i])));
        var ra = FrameMath.PoseToMatrix(from);
        var rb = FrameMath.PoseToMatrix(to);

        // 상대 회전 R = Raᵀ·Rb (시작 자세 프레임 기준) → 축-각
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                double s = 0;
                for (var k = 0; k < 3; k++) s += ra[k, i] * rb[k, j];
                r[i, j] = s;
            }
        var angle = Math.Acos(Math.Clamp((r[0, 0] + r[1, 1] + r[2, 2] - 1.0) / 2.0, -1.0, 1.0));
        var sin = Math.Sin(angle);
        double[]? axis = null;
        if (Math.Abs(sin) > 1e-6)
            axis = new[] { (r[2, 1] - r[1, 2]) / (2 * sin), (r[0, 2] - r[2, 0]) / (2 * sin), (r[1, 0] - r[0, 1]) / (2 * sin) };

        var angleDeg = angle * 180.0 / Math.PI;
        var n = Math.Max(1, Math.Max((int)Math.Ceiling(len / stepMm), (int)Math.Ceiling(angleDeg / stepDeg)));
        var pts = new List<double[]>(n);
        for (var k = 1; k <= n; k++)
        {
            var t = (double)k / n;
            var m = new double[4, 4];
            if (k == n || axis is null)
            {
                // 끝점 또는 회전 없음/퇴화 — 회전은 끝(회전 없음이면 시작과 같음) 자세
                var src = k == n || angle > 1e-6 ? rb : ra;
                for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) m[i, j] = src[i, j];
            }
            else
            {
                var rot = AxisAngle(axis, angle * t);
                for (var i = 0; i < 3; i++)
                    for (var j = 0; j < 3; j++)
                    {
                        double s = 0;
                        for (var q = 0; q < 3; q++) s += ra[i, q] * rot[q, j];
                        m[i, j] = s;
                    }
            }
            for (var i = 0; i < 3; i++) m[i, 3] = from[i] + (to[i] - from[i]) * t;
            m[3, 3] = 1.0;
            var pose = FrameMath.MatrixToPose(m);
            if (k == n) { pose[3] = to[3]; pose[4] = to[4]; pose[5] = to[5]; }   // 끝점은 원래 오일러 표현 그대로
            pts.Add(pose);
        }
        return pts;
    }

    /// <summary>단위 축 <paramref name="u"/> 둘레 <paramref name="theta"/>[rad] 회전 행렬(Rodrigues).</summary>
    private static double[,] AxisAngle(double[] u, double theta)
    {
        var c = Math.Cos(theta);
        var s = Math.Sin(theta);
        var v = 1 - c;
        return new[,]
        {
            { c + u[0] * u[0] * v,        u[0] * u[1] * v - u[2] * s, u[0] * u[2] * v + u[1] * s },
            { u[1] * u[0] * v + u[2] * s, c + u[1] * u[1] * v,        u[1] * u[2] * v - u[0] * s },
            { u[2] * u[0] * v - u[1] * s, u[2] * u[1] * v + u[0] * s, c + u[2] * u[2] * v },
        };
    }

    /// <summary>두 BASE pose 가 위치 <paramref name="tolMm"/>·회전 <paramref name="tolDeg"/> 이내인지(회전은 상대 회전각).</summary>
    internal static bool IsSamePose(double[] a, double[] b, double tolMm, double tolDeg)
    {
        var dp = Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
        if (dp > tolMm) return false;
        var ma = FrameMath.PoseToMatrix(a);
        var mb = FrameMath.PoseToMatrix(b);
        var trace = 0.0;
        for (var i = 0; i < 3; i++)
            for (var k = 0; k < 3; k++)
                trace += ma[k, i] * mb[k, i];   // tr(Raᵀ·Rb)
        var angle = Math.Acos(Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0)) * 180.0 / Math.PI;
        return angle <= tolDeg;
    }
}
