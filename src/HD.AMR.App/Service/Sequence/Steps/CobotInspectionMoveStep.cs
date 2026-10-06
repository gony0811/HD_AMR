using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ② Cobot 검사위치 이동 — 티칭된 검사 준비 위치에 툴프레임 u/v 오프셋을 합성한 목표로 <b>MoveJ 관절 이동</b>.
///
/// <b>목표 위치는 ACS 가 보낸 용접선(seamStartW)에서 온다.</b> 정차점 하나에 여러 task(액션)가 실려
/// 오므로, 벽(Wall ID)으로 고른 티칭 위치 하나로만 가면 2번째 task 가 1번째와 같은 자리로 간다. 그래서
/// seamStartW(맵 좌표)를 코봇 BASE 로 환산해(<see cref="SeamBaseTransform"/>) 면에서 ③ 카메라 목표거리만큼
/// 물러난 <b>접근점</b>으로 간다. <b>자세는 티칭 위치 값을 그대로 쓴다</b> — 손으로 맞춰 검증한 값이고,
/// 같은 벽의 어느 용접선이든 바라보는 방향은 같기 때문(위치만 벽을 따라 달라진다).
/// 여기는 거친 접근이고, 정밀 정렬은 뒤의 ③~⑯이 카메라·레이저로 잡는다.
/// seamStartW 가 없으면(UI 단독 실행) 종전대로 티칭 위치로 이동한다.
///
/// 관절 이동인 이유: 홈에서 검사 준비 위치까지는 거리·자세 변화가 큰 구간이라, 직선 이동(MoveL)으로 가면
/// 자세를 직교 공간에서 보간하다 중간에 손목 특이점(J5≈0)을 쓸고 지나갈 수 있다(rc=38·손목 급회전).
/// 관절 이동은 각 축이 두 끝값 사이에서 단조로 변하므로 그 구간이 생기지 않는다. 대신 TCP 경로가 직선이
/// 아니라 호를 그리므로, 티칭 시 홈↔검사위치 사이에 구조물이 없는지 확인해야 한다.
/// 면을 따라가는 짧은 이동(③~⑱)은 직선이어야 하므로 그대로 MoveL 이다.
///
/// 코봇을 처음 움직이는 스텝이라 <see cref="SequenceEntry"/> 의 진입 준비(활성 좌표계 정규화 + 홈 복귀)를
/// 자기 앞에서 수행한다 — 예전 ① AmrMoveStep 이 하던 일이다(그 스텝의 AMR 이동은 끝내 미구현이었고,
/// AMR 은 ACS 가 VDA5050 order 로 옮긴다).
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

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        // 진입 준비(홈 복귀)를 이 스텝이 맡으므로 홈 티칭이 선행조건이다.
        if (SequenceEntry.ValidateHome(context) is { IsValid: false } homeError)
            return homeError;

        if (context.InspectionSurfaceId is <= 0x00 or > 0xFF)
            return StepValidation.Fail("검사 Wall ID 미설정 (0x01~0xFF) — ② 파라미터에서 선택하세요.");

        var pos = FindBySurfaceId(context);
        if (pos is null)
            return StepValidation.Fail(
                $"Wall 0x{context.InspectionSurfaceId:X2}에 해당하는 티칭 위치가 없습니다 — Teaching에서 Wall ID를 지정하세요.");
        if (!pos.IsTaught)
            return StepValidation.Fail(
                $"Wall 0x{context.InspectionSurfaceId:X2} '{pos.Name}' 미티칭 — Teaching에서 먼저 저장하세요.");

        if (Math.Abs(context.InspectionOffsetU) > 500 || Math.Abs(context.InspectionOffsetV) > 500)
            return StepValidation.Fail("검사 오프셋 u/v 범위 초과 (±500 mm 이내).");

        // ②의 면 후퇴 거리도 같은 값을 쓰므로 ③(CameraAlignStep)과 같은 범위를 여기서도 본다.
        if (context.CameraTargetDistanceMm is < 100 or > 1000)
            return StepValidation.Fail("③ 카메라 목표 거리 범위 초과 (100~1000mm) — ②의 면 후퇴 거리도 같은 값입니다.");

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

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        // 재실행·실패 시 옛 목표가 남아 ③이 엉뚱한 기준으로 판정하는 것을 막는다(세미오토 재시도).
        context.Bag.Remove(WeldSequenceSupport.InspectTargetPoseBagKey);

        var inspection = FindBySurfaceId(context)
            ?? throw new InvalidOperationException($"Wall 0x{context.InspectionSurfaceId:X2} 티칭 위치 없음");

        // 위치는 ACS 용접선에서, 자세는 티칭 값 그대로 — task 마다 달라지는 것은 위치뿐이다.
        // 리치 점검은 정차 배치(AMR pose·장착 보정·스트로크·공구 길이)만으로 결정되므로 코봇을 움직이기
        // 전에 한다 — 진입 준비(후퇴·홈·ready) 뒤에 실패하면 같은 정차점의 task 마다 헛동작을 반복한다.
        var seam = await _seam.ResolveAsync(
            context, context.SeamStartW, context.SeamEndW, context.Tool, "②", ct);
        if (seam.Blocker is not null)
            return StepResult.Fail(seam.Blocker);   // 리치 부족 — 티칭 폴백으로 덮지 않는다

        // 진입 준비 — 잔류 작업물 프레임/공구를 정규화하고 홈에서 출발시킨다(목표 계산 전에 해야
        // 작업물 추종 pose 계산이 정규화된 프레임 기준으로 나온다).
        var entryNote = await SequenceEntry.PrepareAsync(_cobot, context, _logger, ct);

        var (taught, where) = await ComputeTargetPoseAsync(_cobot, inspection, ct);

        // 위치는 ACS 용접선 접근점에서, 자세는 토글·가용성에 따라 계산 법선 또는 티칭.
        var (target, usedComputed) = BuildApproachAnchor(taught, seam.Base, seam.ComputedPose, seam.UseComputedOrientation);
        // u/v 축 기준 정규화 — 광축 둘레 최종 비틀림은 아래 J6 절대각 맞춤이 정한다.
        target = NormalizeUvAnchor(target, _logger);

        var orientationLabel = $"[0x{context.InspectionSurfaceId:X2} {inspection.Name}]";
        if (seam.Base is not null)
        {
            where = usedComputed
                ? $"{seam.Note} — 자세는 계산된 면 법선"
                : $"{seam.Note} — 자세는 티칭 {orientationLabel} 유지";
        }
        else
        {
            where = $"{orientationLabel} {where}";   // 티칭 폴백(위치·자세 모두 티칭)
            _logger.LogInformation("② {Note}", seam.Note);
        }

        // 회귀 비교용 — 계산 법선 자세와 티칭 자세의 (rx,ry,rz)를 항상 함께 남긴다(토글 검증·현장 대조).
        if (seam.ComputedPose is { Length: 6 } cp)
            _logger.LogInformation(
                "② 자세 비교: 계산법선 rxyz=[{Crx:0.0},{Cry:0.0},{Crz:0.0}] vs 티칭 rxyz=[{Trx:0.0},{Try:0.0},{Trz:0.0}] " +
                "(토글 {Toggle}, 사용={Used})",
                cp[3], cp[4], cp[5], taught[3], taught[4], taught[5],
                seam.UseComputedOrientation ? "ON" : "OFF", usedComputed ? "계산법선" : "티칭");

        // 툴프레임 오프셋: offset[0]=u(툴 X = 수평, 좌+/우−), offset[1]=v(툴 Y = 수직, 상+/하−).
        // 실측 확인 매핑 — 과거 [v, u] 순서는 v 가 수평으로 나가는 축 교차 오류였음.
        var hasOffset = context.InspectionOffsetU != 0 || context.InspectionOffsetV != 0;
        var composed = ComposeUvTarget(target, context);

        // 광축 둘레 비틀림은 J6 절대각으로 정한다 — 수직 180°, 수평 90° (센서 헤드–링크암 간섭 회피).
        var targetJ6 = TargetJ6Deg(context);
        double[] finalPose, joints;
        try
        {
            (finalPose, joints) = await AlignTwistToJ6Async(_cobot, composed, context.Tool, targetJ6, ct);
        }
        catch (InvalidOperationException ex)
        {
            return StepResult.Fail(ex.Message);
        }

        var rc = await _cobot.Rpc.MoveJAsync(joints, finalPose,
            tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);

        var offsetNote = hasOffset
            ? $" (오프셋 u={context.InspectionOffsetU:0.###}, v={context.InspectionOffsetV:0.###} mm)"
            : "";
        offsetNote += context.InspectionDirection == InspectionMoveDirection.Vertical
            ? $" [수직, J6={joints[5]:0.0}°]"
            : $" [수평, J6={joints[5]:0.0}°]";

        if (rc != 0)
            return StepResult.Fail($"이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        // ③ 이 "② 목표에 와 있는가" 를 검사할 때 쓸 기준. 티칭 위치로 재계산하면 ACS 경로(seam 접근점)
        // 에서 항상 어긋나므로, 실제로 지령한 값을 남긴다.
        context.Bag[WeldSequenceSupport.InspectTargetPoseBagKey] = finalPose;

        return StepResult.Ok($"{where} 관절 이동(MoveJ) 완료{offsetNote}.{entryNote}");
    }
}
