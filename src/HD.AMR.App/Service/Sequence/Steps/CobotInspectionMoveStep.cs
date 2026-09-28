using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;
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
    private readonly AMRService _amr;
    private readonly TelescopicService _lift;
    private readonly CalibrationService _calib;
    private readonly PostureLimitsService _limits;
    private readonly ILogger<CobotInspectionMoveStep> _logger;

    public CobotInspectionMoveStep(CobotService cobot, AMRService amr, TelescopicService lift,
        CalibrationService calib, PostureLimitsService limits,
        ILogger<CobotInspectionMoveStep> logger)
    {
        _cobot = cobot;
        _amr = amr;
        _lift = lift;
        _calib = calib;
        _limits = limits;
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

    /// <summary>
    /// ACS 용접선(seamStartW, 맵 좌표)을 코봇 BASE 접근점으로 환산한다. 실패(측위 없음·장착 보정 없음 등)하면
    /// null 과 사유를 돌려주고, 호출측은 티칭 위치 폴백으로 간다 — 좌표 환산이 안 된다고 검사를 통째로
    /// 실패시키기보다, 예전 동작(벽 티칭 위치)으로라도 진행하고 로그에 남기는 편이 운영에 낫다.
    /// </summary>
    /// <returns>Blocker 가 null 이 아니면 <b>폴백 없이 즉시 실패</b>해야 한다 — 환산은 됐는데 그 자리가
    /// 팔로 닿지 않는 경우다. 환산 자체가 불가(측위·보정 없음)면 Base=null·Blocker=null 로 티칭 폴백.</returns>
    private async Task<(double[]? Base, string Note, string? Blocker)> TrySeamApproachAsync(
        SequenceContext context, CancellationToken ct)
    {
        if (context.SeamStartW is not { Length: 3 } seam)
            return (null, "seamStartW 없음(UI 단독 실행) — 티칭 위치로 이동", null);

        if (_amr.LatestStatus is not { } status)
            return (null, "AMR 측위 없음 — 용접선 좌표를 환산할 수 없어 티칭 위치로 이동", null);
        var pose = status.Pose;

        var mount = await _calib.GetMountAsync();
        if (mount.All(v => Math.Abs(v) < 1e-9))
            return (null, "장착 보정(T_A_B) 미수행 — 용접선 좌표를 환산할 수 없어 티칭 위치로 이동", null);

        var stroke = _lift.Latest is { HeightMm: >= 0 } lift ? lift.HeightMm : 0;

        var standoff = ResolveApproachDistanceMm(context);

        var target = SeamBaseTransform.Resolve(new SeamBaseInput(
            SeamStartW: seam,
            SeamEndW: context.SeamEndW is { Length: 3 } ? context.SeamEndW : null,
            AmrXm: pose.X,
            AmrYm: pose.Y,
            AmrYawRad: pose.Angle,
            MountAtHome: mount,
            TelescopicStrokeMm: stroke,
            ZDatumOffsetMm: context.ZDatumOffsetMm,
            StandoffMm: standoff,
            WallFacingThetaRad: context.WallFacingThetaRad,
            WallCode: context.WallCode));

        foreach (var note in target.Notes)
            _logger.LogWarning("② 용접선 환산 주의: {Note}", note);

        _logger.LogInformation(
            "② 용접선 접근점: seamStartW=[{Sx:0.###},{Sy:0.###},{Sz:0.###}]m → BASE [{Bx:0.0},{By:0.0},{Bz:0.0}]mm " +
            "(standoff {Standoff:0}mm, wall={Wall}, 면까지 법선거리 {Dist:0}mm, 스트로크 {Stroke:0}mm)",
            seam[0], seam[1], seam[2],
            target.ApproachBaseMm[0], target.ApproachBaseMm[1], target.ApproachBaseMm[2],
            standoff, context.WallCode ?? "(미지정)", target.NormalDistanceMm, stroke);

        // ── 리치 사전 점검 — IK 에 특이 해를 풀게 하기 전에 배치로 거른다 ──────────────
        double toolLen = 0;
        string? skipNote = null;
        try
        {
            var tc = context.Tool == 0 ? new double[6] : await _cobot.Rpc.GetToolCoordAsync(context.Tool, ct);
            toolLen = ApproachReach.ToolLengthMm(tc);
            if (toolLen < 1.0) skipNote = $"공구 #{context.Tool} 오프셋이 0(플랜지 기준)";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { skipNote = $"공구 좌표 조회 실패: {ex.Message}"; }

        if (skipNote is not null)
        {
            // 읽기 한 번 실패로 검사를 죽이지 않는다 — 도달성은 IK·자세 한계 판정에만 의존하게 된다.
            _logger.LogWarning("② 리치 사전 점검 생략 — {Note}.", skipNote);
        }
        else
        {
            var limits = await _limits.GetAsync();
            var reach = ApproachReach.Check(target.NormalDistanceMm, standoff, toolLen, limits.MinFlangeReachMm);
            _logger.LogInformation(
                "② 리치 점검: 면까지 {Normal:0}mm − 후퇴 {Approach:0} − 공구 {Tool:0} = 플랜지 뻗음 {Flange:0}mm (최소 {Min:0}mm)",
                target.NormalDistanceMm, standoff, toolLen, reach.FlangeReachMm, limits.MinFlangeReachMm);

            if (!reach.Ok)
                return (null, "",
                    $"정차 거리 부족 — 필요 ≥ {reach.RequiredNormalDistanceMm:0}mm, 현재 {target.NormalDistanceMm:0}mm " +
                    $"(후퇴 {standoff:0} + 공구 {toolLen:0} + 최소 뻗음 {limits.MinFlangeReachMm:0}). " +
                    $"플랜지 뻗음이 {reach.FlangeReachMm:0}mm 라 팔을 접은 자세가 되고 손목 특이점(rc=38)이 납니다 — " +
                    $"AMR 을 벽에서 {reach.ShortfallMm:0}mm 더 떨어뜨려 정차하세요. " +
                    $"(최소 뻗음 {limits.MinFlangeReachMm:0}mm 는 실측 전 잠정값 — 용접 위치 시험 화면에서 조정 가능)");
        }

        return (target.ApproachBaseMm, $"용접선 접근점(면 이격 {standoff:0}mm — ③ 카메라 목표거리)", null);
    }

    /// <summary>u(툴 X)/v(툴 Y) 오프셋 + 수직 모드 RZ−90° 를 담은 툴프레임 오프셋 벡터.</summary>
    internal static double[] UvOffset(SequenceContext c) => new[]
    {
        c.InspectionOffsetU, c.InspectionOffsetV, 0.0, 0.0, 0.0,
        c.InspectionDirection == InspectionMoveDirection.Vertical ? -90.0 : 0.0,
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
        target = NormalizeUvAnchor(target, _logger);
        where = $"[0x{context.InspectionSurfaceId:X2} {inspection.Name}] {where}";

        // 위치는 ACS 용접선에서, 자세는 티칭 값 그대로 — task 마다 달라지는 것은 위치뿐이다.
        var (seamBase, seamNote, blocker) = await TrySeamApproachAsync(context, ct);
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

        var rc = await _cobot.Rpc.MoveJByToolOffsetAsync(target, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);

        var offsetNote = hasOffset
            ? $" (오프셋 u={context.InspectionOffsetU:0.###}, v={context.InspectionOffsetV:0.###} mm)"
            : "";
        if (rz != 0)
            offsetNote += " [수직, RZ−90°]";

        if (rc != 0)
            return StepResult.Fail($"이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        // ③ 이 "② 목표에 와 있는가" 를 검사할 때 쓸 기준. 티칭 위치로 재계산하면 ACS 경로(seam 접근점)
        // 에서 항상 어긋나므로, 실제로 지령한 값을 남긴다. 합성식은 MoveJByToolOffsetAsync 와 동일.
        context.Bag[WeldSequenceSupport.InspectTargetPoseBagKey] = ComposeUvTarget(target, context);

        return StepResult.Ok($"{where} 관절 이동(MoveJ) 완료{offsetNote}.{entryNote}");
    }
}
