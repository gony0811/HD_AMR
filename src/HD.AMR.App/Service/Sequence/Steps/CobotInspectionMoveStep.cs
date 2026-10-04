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

    /// <summary>u/v 오프셋 합성용 앵커 정규화 — 티칭 자세가 광축(툴 Z) 둘레로 비틀려 저장돼 있어도
    /// (예: 수직 모드 RZ−90° 상태에서 재티칭) 툴 +Y가 베이스 상방(+Z)을 향하도록 트위스트를 제거한다.
    /// u=수평/v=수직 매핑과 수평/수직(RZ) 회전, ④의 이미지↔툴 축 매핑은 모두 이 표준 자세를 전제하므로,
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

    /// <summary>u(툴 X)/v(툴 Y) 오프셋 + 수직 모드 RZ+90° 를 담은 툴프레임 오프셋 벡터.
    /// 수직 검사 시 카메라 센서를 벽면 기준 반시계방향(CCW) 90° 회전시켜 수직 스캔 방향에 맞춘다.
    /// u/v 앵커 정규화(NormalizeUvAnchor)는 수직 모드에서 건너뛰므로 RZ 만으로 J6 변위를 결정한다.</summary>
    internal static double[] UvOffset(SequenceContext c) => new[]
    {
        c.InspectionOffsetU, c.InspectionOffsetV, 0.0, 0.0, 0.0,
        c.InspectionDirection == InspectionMoveDirection.Vertical ? 90.0 : 0.0,
    };

    /// <summary>
    /// 앵커에 u/v·RZ 오프셋을 합성한 <b>실제 지령 목표</b>.
    /// <see cref="Communication.FairinoRpcClient.MoveJByToolOffsetAsync"/> 내부식과 같아야 한다 —
    /// ③ 게이트가 이 값을 기준으로 판정하므로 식이 갈라지면 ③이 통째로 막힌다(2026-09-25 회귀).
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

        // 진입 준비 — 잔류 작업물 프레임/공구를 정규화하고 홈에서 출발시킨다(목표 계산 전에 해야
        // 작업물 추종 pose 계산이 정규화된 프레임 기준으로 나온다).
        var entryNote = await SequenceEntry.PrepareAsync(_cobot, context, _logger, ct);

        var (target, where) = await ComputeTargetPoseAsync(_cobot, inspection, ct);
        // 수직 모드에서는 u/v 앵커 정규화를 건너뛴다 — 정규화(+88°)와 수직 RZ(−90°)가 합쳐지면
        // 의도와 다른 J6 회전이 된다. 수직 모드의 RZ 오프셋만으로 카메라 센서 방향을 맞춘다.
        if (context.InspectionDirection != InspectionMoveDirection.Vertical)
            target = NormalizeUvAnchor(target, _logger);
        where = $"[0x{context.InspectionSurfaceId:X2} {inspection.Name}] {where}";

        // 위치는 ACS 용접선에서, 자세는 티칭 값 그대로 — task 마다 달라지는 것은 위치뿐이다.
        var (seamBase, seamNote, blocker) = await _seam.ResolveAsync(
            context, context.SeamStartW, context.SeamEndW, context.Tool, "②", ct);
        if (blocker is not null)
            return StepResult.Fail(blocker);   // 리치 부족 — 티칭 폴백으로 덮지 않는다
        if (seamBase is not null)
        {
            target = new[] { seamBase[0], seamBase[1], seamBase[2], target[3], target[4], target[5] };
            where = $"{seamNote} — 자세는 티칭 [0x{context.InspectionSurfaceId:X2} {inspection.Name}] 유지";
        }
        else
        {
            _logger.LogInformation("② {Note}", seamNote);
        }

        // 툴프레임 오프셋: offset[0]=u(툴 X = 수평, 좌+/우−), offset[1]=v(툴 Y = 수직, 상+/하−).
        // 실측 확인 매핑 — 과거 [v, u] 순서는 v 가 수평으로 나가는 축 교차 오류였음.
        // 수직 검사 방향이면 툴 RZ −90° 회전을 합성 (병진 u/v는 회전 전 대기자세 축 기준이라 의미 불변).
        var offset = UvOffset(context);
        var rz = offset[5];
        var hasOffset = context.InspectionOffsetU != 0 || context.InspectionOffsetV != 0;

        int rc;
        if (context.InspectionDirection == InspectionMoveDirection.Vertical)
        {
            // 수직 모드: IK config 0–7 중 현재 관절에 가장 가까운 해를 선택한다.
            // config −1(자동)은 시무 방향(위→아래 vs 아래→위)에 따라 J6 분기가 바뀌어
            // RZ 위치가 180° 반전되는 문제가 있다.
            var curJoints = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
            rc = await _cobot.Rpc.MoveJByToolOffsetNearestAsync(target, user: 0, offset,
                curJoints, tool: context.Tool, vel: context.Velocity, ct: ct);
        }
        else
        {
            rc = await _cobot.Rpc.MoveJByToolOffsetAsync(target, user: 0, offset,
                tool: context.Tool, vel: context.Velocity, ct: ct);
        }

        var offsetNote = hasOffset
            ? $" (오프셋 u={context.InspectionOffsetU:0.###}, v={context.InspectionOffsetV:0.###} mm)"
            : "";
        if (rz != 0)
            offsetNote += $" [수직, RZ{rz:+0;-0}°]";

        if (rc != 0)
            return StepResult.Fail($"이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        // ③ 이 "② 목표에 와 있는가" 를 검사할 때 쓸 기준. 티칭 위치로 재계산하면 ACS 경로(seam 접근점)
        // 에서 항상 어긋나므로, 실제로 지령한 값을 남긴다. 합성식은 MoveJByToolOffsetAsync 와 동일.
        context.Bag[WeldSequenceSupport.InspectTargetPoseBagKey] = ComposeUvTarget(target, context);

        return StepResult.Ok($"{where} 관절 이동(MoveJ) 완료{offsetNote}.{entryNote}");
    }
}
