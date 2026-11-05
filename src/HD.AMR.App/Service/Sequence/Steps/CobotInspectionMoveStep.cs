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

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        // 접근점은 정차 배치(AMR pose·장착 보정·스트로크)만으로 정해지므로 코봇을 움직이기 전에 환산·리치
        // 점검을 한다 — 1단계 뒤에 실패하면 같은 정차점의 task 마다 ready 왕복을 헛되이 반복한다.
        var seam = await _seam.ResolveAsync(
            context, context.SeamStartW, context.SeamEndW, context.Tool, "[Cobot 검사위치 이동]", ct,
            await GetApproachStandoffMmAsync(_param, _logger));
        if (seam.Blocker is not null)
        {
            if (seam.Shortfall is not null) context.Bag[ReachShortfallBagKey] = seam.Shortfall;
            return StepResult.Fail(seam.Blocker);
        }
        if (seam.Base is null)
            return StepResult.Fail($"[Cobot 검사위치 이동] {seam.Note}");

        context.Bag[SeamStartBaseBagKey] = seam.Base.ToArray();
        if (ShiftAnchorFrame(context, seam.Base) is { } shiftError)
            return StepResult.Fail(shiftError);

        // ── 같은 anchor 그룹 연속 task: 직전 종료 자세 그대로면 복귀 없이 다음 시작점으로 ─────
        if (await TryChainFromPreviousAsync(context, seam, ct) is { } chained)
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

        // 면 법선을 알면 접근점으로 한 번에 MoveJ 하지 않는다 — 진입 경유점에서 멀리 떨어진 접근점(챔퍼 아래쪽 끝 등)까지
        // 관절 보간으로 가면 팔이 면 쪽으로 크게 휘둘린다(실기 2026-11-02 L4-R03C09 PU #3, 231° MoveJ 중 EMO).
        if (usedNormal)
            return await PlanarApproachAsync(context, seam, current, target, reference, twistNote, entryNote, ct);

        var joints = await _cobot.Rpc.GetInverseKinKeepWristAsync(target, reference, context.Tool, 0, ct);
        if (ElbowFlipBlocker(reference, joints) is { } flip)
            return StepResult.Fail(flip);
        if (Inspection.WObjAttitude.J6RangeBlocker(reference[5], joints[5]) is { } j6Range)
            return StepResult.Fail($"[Cobot 검사위치 이동] 용접선 접근점 {j6Range} — 움직이지 않았습니다.");
        if (await SelfCollisionGuardOnAsync() && SelfCollisionBlocker(joints, seam) is { } selfHit)
            return StepResult.Fail(selfHit);

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

    internal const double DefaultPlanarRetreatMm = 100.0;
    internal const double MaxPlanarRetreatMm = 400.0;

    /// <summary>진입점→들어갈 점의 면 방향 거리가 이 값 미만이면 면 방향 이동을 생략한다 [mm].</summary>
    internal const double PlanarSkipMm = 50.0;

    /// <summary>면 방향 직선 이동 경로를 역기구학으로 검증하는 간격 [mm].</summary>
    internal const double PlanarSampleMm = 100.0;

    /// <summary>경로 검증의 손목 특이점 여유 — |J5| 가 이보다 작으면 MoveL 이 rc=38 로 거부되거나 손목이 급회전한다 [도].</summary>
    internal const double PlanarWristMarginDeg = 5.0;

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

    /// <summary>
    /// 직선 이동 경로의 연속 두 점 해(<paramref name="prev"/>→<paramref name="next"/>)가 이어지지 않는 사유, 이어지면 null.
    /// 관절 점프·팔꿈치 가지 전환·손목 특이점(J5 부호 반전/여유 부족)을 본다 — 기준 가지는 진입점 해 <paramref name="entry"/>.
    /// </summary>
    internal static string? PathStepBlocker(double[] prev, double[] next, double[] entry)
    {
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
    internal static List<double[]> EntryCandidates(PlanarApproachPlan plan)
    {
        if (plan.InPlaneMm < PlanarSkipMm) return new List<double[]> { plan.Plunge };

        var list = new List<double[]>();
        if (plan.InPlaneMm <= MaxEntryInPlaneMm) list.Add(plan.Plunge);
        list.Add(plan.Entry);
        var pts = SampleSegment(plan.Entry, plan.Plunge, PlanarSampleMm);
        var step = plan.InPlaneMm / pts.Count;
        for (var k = 0; k < pts.Count - 1; k++)   // 마지막 점 = 들어갈 점(①에서 따로 다룸)
            if ((k + 1) * step <= MaxEntryInPlaneMm + 1e-6) list.Add(pts[k]);
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
        var candidates = EntryCandidates(plan);
        var rejects = new List<string>();

        // ── 진입점 선택 + 경로 검증(모션 없음) ────────────────────────────
        double[]? entryPos = null, jEntry = null, jPlunge = null, jTarget = null;
        foreach (var cand in candidates)
        {
            var label = ReferenceEquals(cand, plan.Plunge) ? "들어갈 점 직접" : $"후퇴 평면 [{Fmt(cand)}]";
            double[] jc;
            try { jc = await _cobot.Rpc.GetInverseKinKeepWristAsync(Pose(cand), reference, context.Tool, 0, ct); }
            catch (InvalidOperationException) { rejects.Add($"{label}: 도달 불가"); continue; }

            var travel = MaxJointTravel(reference, jc);
            if (travel > MaxEntryJointTravelDeg) { rejects.Add($"{label}: 진입 관절 이동 {travel:0}° > {MaxEntryJointTravelDeg:0}°"); continue; }
            if (FairinoRpcClient.IsElbowFlip(reference, jc)) { rejects.Add($"{label}: 팔꿈치 가지 전환"); continue; }
            if (IsWristFlip(reference, jc)) { rejects.Add($"{label}: 손목 가지 전환(J5 {reference[4]:0}° → {jc[4]:0}°)"); continue; }
            if (Inspection.WObjAttitude.J6RangeBlocker(reference[5], jc[5]) is { } j6Entry) { rejects.Add($"{label}: {j6Entry}"); continue; }
            if (guardOn && SelfCollisionBlocker(jc, seam) is not null) { rejects.Add($"{label}: 자기 간섭"); continue; }

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
                $"(접근점은 현재 TCP 정면에서 면을 따라 {plan.InPlaneMm:0}mm, 진입 MoveJ 는 {MaxEntryInPlaneMm:0}mm 이내만 허용) " +
                $"후보 {candidates.Count}개 모두 거부: {string.Join(" / ", rejects.Take(4))}" +
                (rejects.Count > 4 ? $" 외 {rejects.Count - 4}건" : "") +
                ". 정차 위치를 용접선 쪽으로 옮기거나 이 면의 검사 준비 자세를 확인하세요.");

        var direct = ReferenceEquals(entryPos, plan.Plunge);
        var planarMm = direct ? 0.0 : Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (plan.Plunge[i] - entryPos[i]) * (plan.Plunge[i] - entryPos[i])));
        _logger.LogInformation(
            "[Cobot 검사위치 이동] 평면 경유 접근: 진입점 [{E}] (진입 관절 이동 최대 {T:0}°) → 면 방향 {D:0}mm → 들어갈 점 [{P}] → 법선 {R:0}mm 진입" +
            "{Rej}",
            Fmt(entryPos), MaxJointTravel(reference, jEntry!), planarMm, Fmt(plan.Plunge), retreat,
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

        var (moved, normalMm) = InternalWorkpieceFrame.TranslateInPlane(frame, anchorStart, startBase);
        context.Bag[InternalWorkpieceFrame.BagKey] = moved;
        _logger.LogInformation(
            "[Cobot 검사위치 이동] anchor 프레임 평행 이동 Δ=[{Dx:0.0}, {Dy:0.0}, {Dz:0.0}]mm (법선 성분 {N:+0.0;-0.0}mm 제외) — 원점 [{X:0.0}, {Y:0.0}, {Z:0.0}]",
            moved[0] - frame[0], moved[1] - frame[1], moved[2] - frame[2], normalMm, moved[0], moved[1], moved[2]);
        return null;
    }

    /// <summary>
    /// 같은 anchor 그룹 연속 task — 직전 task 종료 자세에서 진입 준비(후퇴·홈 위치_2·검사 준비 왕복) 없이 다음 시작
    /// 접근점으로 간다(사양 §8.1 "액션 사이 홈 왕복 금지"). 조건이 하나라도 안 맞으면 null — 호출측이 종전 경로로 간다.
    /// 경로: ① 면에 standoff 보다 가까우면 면 법선 방향으로만 standoff 면까지 들어 올림(자세 유지) → ② 그 면을 따라
    /// 접근점으로 MoveL(같은 면 이격 유지, 광축은 면 법선, 광축 둘레 비틀림은 직전 종료 자세 유지 — J6 180° 정렬 안 함).
    /// </summary>
    private async Task<StepResult?> TryChainFromPreviousAsync(
        SequenceContext context, SeamApproachResult seam, CancellationToken ct)
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
        var target = new[] { startBase[0], startBase[1], startBase[2], orientation[3], orientation[4], orientation[5] };
        double[] joints;
        double[] reference;
        try
        {
            reference = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
            joints = await _cobot.Rpc.GetInverseKinKeepWristAsync(target, reference, context.Tool, 0, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("[Cobot 검사위치 이동] 연속 이동 생략 — 목표 자세 계산 실패: {Msg} → 진입 준비 경로", ex.Message);
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

        var lift = PlanChainLift(cur, startBase, WallOutNormal(computed, seam.OpticalAxis));
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
        _logger.LogInformation("[Cobot 검사위치 이동] 연속 이동 완료 — 직전 종료 자세에서 {Dist:0}mm{Lift}", dist, liftNote);
        return StepResult.Ok(
            $"연속 이동(복귀 생략) — 직전 종료 자세에서 {seam.Note} 직선 이동(MoveL) {dist:0}mm{liftNote} " +
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
