using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ② Cobot 검사위치 이동 — 두 단계로 정의한다.
///
///  1. <b>작업 준비 위치 이동</b> — Teaching 의 "ready"(작업 준비 위치)로 간다. <see cref="SequenceEntry"/> 의
///     진입 준비(활성 좌표계 정규화 → 필요 시 벽면 후퇴·홈 복귀 → ready)를 그대로 쓴다. 홈→검사 위치를
///     바로 MoveJ 하면 900mm 이하 영역에서 툴이 차체와 부딪힐 수 있어 ready 를 반드시 거친다.
///  2. <b>용접선 시작점 접근</b> — 해당 작업의 용접선 시작점(seamStartW, 맵 좌표)을 코봇 BASE 로 환산해
///     (<see cref="SeamBaseTransform"/>) 면 법선 반대로 <see cref="ApproachStandoffMm"/>(400mm) 물러난 점으로
///     <b>툴(TCP)</b>을 MoveJ 로 보낸다. 자세는 wall_code 가 유효하면 광축이 면을 바라보는 계산 자세,
///     없으면 1단계 도착(ready) 자세를 유지한다.
///
/// 용접선 좌표를 환산할 수 없으면(측위·장착 보정·좌표 없음) 엉뚱한 곳으로 가지 않도록 실패한다 — 예전의
/// 벽별 검사 준비 티칭 위치 폴백은 쓰지 않는다. 정밀 정렬은 뒤의 ③~⑯이 카메라·레이저로 잡는다.
///
/// 관절 이동인 이유: ready→접근점은 자세 변화가 큰 구간이라 MoveL 로 가면 손목 특이점(J5≈0)을 쓸고
/// 지나갈 수 있다(rc=38). 대신 TCP 경로가 호를 그리므로 그 사이에 구조물이 없어야 한다.
/// </summary>
public class CobotInspectionMoveStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly SeamApproachResolver _seam;
    private readonly ILogger<CobotInspectionMoveStep> _logger;

    public CobotInspectionMoveStep(CobotService cobot, SeamApproachResolver seam,
        ILogger<CobotInspectionMoveStep> logger)
    {
        _cobot = cobot;
        _seam = seam;
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

    /// <summary>② 2단계 접근점이 용접선 시작점에서 물러나는 거리 [mm] — 툴(TCP) 기준.</summary>
    public const double ApproachStandoffMm = 400.0;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        // 진입 준비(홈 경유)를 이 스텝이 맡으므로 홈 티칭이 선행조건이다.
        if (SequenceEntry.ValidateHome(context) is { IsValid: false } homeError)
            return homeError;

        if (!context.Positions.TryGetValue("ready", out var ready) || !ready.IsTaught)
            return StepValidation.Fail("작업 준비 위치(ready) 미티칭 — Teaching에서 먼저 저장하세요.");

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
            "② u/v 앵커 정규화: 티칭 자세 광축 트위스트 {Theta:0.#}° 제거 (툴 +Y → 베이스 상방 정렬)", theta);
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
    /// </summary>
    public static Task<(double[] Pose, double[] Joints)> AlignTwistToJ6Async(
        CobotService cobot, double[] pose, int tool, InspectionMoveDirection direction, CancellationToken ct)
        => AlignTwistToJ6Async(cobot, pose, tool,
            direction == InspectionMoveDirection.Vertical ? VerticalJ6Deg : HorizontalJ6Deg, ct);

    public static async Task<(double[] Pose, double[] Joints)> AlignTwistToJ6Async(
        CobotService cobot, double[] pose, int tool, double targetJ6Deg, CancellationToken ct)
    {
        var reference = (await cobot.Rpc.GetActualJointPosAsync(ct: ct)).ToArray();
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

    /// <summary>
    /// 앵커에 u/v 오프셋을 합성한 목표 — 이후 <see cref="AlignTwistToJ6Async"/> 가 광축 비틀림만 바꾼다.
    /// ③ 게이트 폴백·드라이런 끝점 이동도 같은 순서(정규화 → 합성 → J6 맞춤)를 따라야 한다 —
    /// 식이 갈라지면 ③이 통째로 막힌다(2026-09-25 회귀).
    /// </summary>
    internal static double[] ComposeUvTarget(double[] anchor, SequenceContext c)
        => FrameMath.FromFrame(UvOffset(c), anchor);

    /// <summary>
    /// ② 접근점이 면에서 물러나는 거리 [mm] = ③ 카메라 목표거리(레시피 값).
    ///
    /// ②는 대충 데려다 놓고 ③이 실측 depth 로 확정하는 구조라, ②가 물러나는 거리는 ③이 수렴시킬
    /// 거리와 <b>같아야 한다</b>. ACS <c>params.standoffMm</c>
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
            context, context.SeamStartW, context.SeamEndW, context.Tool, "②", ct, ApproachStandoffMm);
        if (seam.Blocker is not null)
            return StepResult.Fail(seam.Blocker);
        if (seam.Base is null)
            return StepResult.Fail($"② {seam.Note}");

        // ── 1단계: 작업 준비 위치(ready) ─────────────────────────────
        var entryNote = await SequenceEntry.PrepareAsync(_cobot, context, _logger, ct);

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

        var reference = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
        var joints = await _cobot.Rpc.GetInverseKinKeepWristAsync(target, reference, context.Tool, 0, ct);

        var rc = await _cobot.Rpc.MoveJAsync(joints, target,
            tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"용접선 접근점 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        var pose = usedNormal ? "자세=면 법선" : "자세=작업 준비 위치 유지(wall_code 없음)";
        return StepResult.Ok(
            $"작업 준비 위치 → {seam.Note} 관절 이동(MoveJ) 완료 [{pose}, J6={joints[5]:0.0}°].{entryNote}");
    }
}
