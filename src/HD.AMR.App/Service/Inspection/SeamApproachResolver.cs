using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// <see cref="SeamApproachResolver.ResolveAsync"/> 결과.
/// </summary>
/// <param name="Base">접근점 위치(코봇 BASE, mm). null 이고 <paramref name="Blocker"/> 도 null 이면 티칭 폴백.</param>
/// <param name="ComputedPose">면 법선에 정렬된 <b>전체 목표 pose</b>(코봇 BASE, [x,y,z,rx,ry,rz]).
/// wall_code 가 유효할 때만 non-null — [x,y,z]는 <paramref name="Base"/>와 같고 [rx,ry,rz]가 법선 정렬 자세다.
/// 토글과 무관하게 항상 채운다(토글 OFF 에서도 티칭 자세와의 비교 로깅에 쓴다).</param>
/// <param name="UseComputedOrientation">하이브리드 토글(<see cref="SeamApproachResolver.UseComputedNormalOrientationKey"/>).
/// true 이고 <paramref name="ComputedPose"/>가 있으면 호출측은 자세를 계산 법선으로 쓴다.</param>
/// <param name="Note">진단 문자열.</param>
/// <param name="Blocker">null 이 아니면 호출측은 <b>폴백 없이 즉시 실패</b>(리치 부족 등).</param>
/// <param name="OpticalAxis"><paramref name="ComputedPose"/> 계산에 쓴 광축 툴축(<see cref="WeldSequenceSupport.DepthAxisKey"/>).</param>
public sealed record SeamApproachResult(
    double[]? Base,
    double[]? ComputedPose,
    bool UseComputedOrientation,
    string Note,
    string? Blocker,
    ToolAxisDir OpticalAxis = ToolAxisDir.PlusZ);

/// <summary>
/// ACS 용접선 점(맵 좌표)을 코봇 BASE 접근점으로 환산한다 — ② 검사위치 이동(시작점)과
/// 드라이런 끝점 이동(<see cref="Steps.CobotSeamEndMoveStep"/>)이 공유한다.
/// 측위·장착보정(T_A_B)·텔레스코픽 스트로크·면 이격(standoff)·리치 사전점검을 한 곳에서 수행한다.
///
/// 환산 자체가 불가(측위 없음·장착 보정 없음·좌표 없음)면 <c>Base=null, Blocker=null</c> 로
/// 돌려주고 호출측은 티칭 위치 폴백으로 간다 — 환산이 안 된다고 검사를 통째로 실패시키기보다
/// 예전 동작(벽 티칭 위치)으로라도 진행하는 편이 운영에 낫다.
/// 환산은 됐지만 그 자리가 팔로 닿지 않으면 <c>Blocker != null</c> — 호출측은 <b>폴백 없이 즉시 실패</b>해야 한다.
/// (CobotInspectionMoveStep 의 옛 TrySeamApproachAsync 에서 추출 — 2026-10 드라이런 2점 교시 공유.)
/// </summary>
public sealed class SeamApproachResolver
{
    /// <summary>하이브리드 토글 — true 면 ②/끝점 이동이 자세를 (티칭 포즈가 아니라) 계산된 면 법선에 맞춘다.
    /// 범용 key/value(<see cref="ParameterService"/>)에 보관, 기본값 false(현재 동작 보존).</summary>
    public const string UseComputedNormalOrientationKey = "inspection.useComputedNormalOrientation";

    private readonly CobotService _cobot;
    private readonly AMRService _amr;
    private readonly TelescopicService _lift;
    private readonly CalibrationService _calib;
    private readonly PostureLimitsService _limits;
    private readonly ParameterService _param;
    private readonly ILogger<SeamApproachResolver> _logger;

    public SeamApproachResolver(CobotService cobot, AMRService amr, TelescopicService lift,
        CalibrationService calib, PostureLimitsService limits, ParameterService param,
        ILogger<SeamApproachResolver> logger)
    {
        _cobot = cobot;
        _amr = amr;
        _lift = lift;
        _calib = calib;
        _limits = limits;
        _param = param;
        _logger = logger;
    }

    /// <summary>
    /// <paramref name="approachW"/>(접근할 용접선 점, 맵 좌표 m)를 코봇 BASE 접근점으로 환산한다.
    /// </summary>
    /// <param name="approachW">접근할 용접선 점 [x,y,z] m(맵). null/형식 오류면 환산 불가(폴백).</param>
    /// <param name="directionW">방향 유도·TOOL 회전 기준이 되는 반대쪽 점 [x,y,z] m(맵, 선택).</param>
    /// <param name="tool">리치 사전점검에 쓸 공구 번호.</param>
    /// <param name="label">로그 접두사(예 "②", "끝점").</param>
    /// <param name="standoffMm">면 이격 [mm]. null 이면 ③ 카메라 목표거리(<see cref="CobotInspectionMoveStep.ResolveApproachDistanceMm"/>).</param>
    /// <returns>Blocker 가 null 이 아니면 호출측은 즉시 실패해야 한다. Base 가 null 이고 Blocker 도 null 이면 티칭 폴백.</returns>
    public async Task<SeamApproachResult> ResolveAsync(
        SequenceContext context, double[]? approachW, double[]? directionW, int tool, string label, CancellationToken ct,
        double? standoffMm = null)
    {
        var useComputed = await _param.GetBoolAsync(UseComputedNormalOrientationKey) ?? false;
        // 법선 자세는 '광축 툴축이 면을 향하게' 만든다 — 카메라 페이지의 광축 설정(현장 −Z)을 따라야 한다.
        // 기본 +Z 로 계산하면 툴이 180° 뒤집힌 목표가 나와 손목(J4)이 크게 재구성된다(2026-10-07 현장).
        var (opticalAxis, _) = await WeldSequenceSupport.GetDepthAxisAsync(_param);

        if (approachW is not { Length: 3 } seam)
            return new SeamApproachResult(null, null, useComputed, $"{label} 용접선 좌표 없음 — 티칭 위치로 이동", null);

        if (_amr.LatestStatus is not { } status)
            return new SeamApproachResult(null, null, useComputed,
                "AMR 측위 없음 — 용접선 좌표를 환산할 수 없어 티칭 위치로 이동", null);
        var pose = status.Pose;

        var mount = await _calib.GetMountAsync();
        if (mount.All(v => Math.Abs(v) < 1e-9))
            return new SeamApproachResult(null, null, useComputed,
                "장착 보정(T_A_B) 미수행 — 용접선 좌표를 환산할 수 없어 티칭 위치로 이동", null);

        var stroke = _lift.Latest is { HeightMm: >= 0 } lift ? lift.HeightMm : 0;

        var standoff = standoffMm ?? CobotInspectionMoveStep.ResolveApproachDistanceMm(context);

        var target = SeamBaseTransform.Resolve(new SeamBaseInput(
            SeamStartW: seam,
            SeamEndW: directionW is { Length: 3 } ? directionW : null,
            AmrXm: pose.X,
            AmrYm: pose.Y,
            AmrYawRad: pose.Angle,
            MountAtHome: mount,
            TelescopicStrokeMm: stroke,
            ZDatumOffsetMm: context.ZDatumOffsetMm,
            StandoffMm: standoff,
            WallFacingThetaRad: context.WallFacingThetaRad,
            WallCode: context.WallCode,
            OpticalAxis: opticalAxis));

        foreach (var note in target.Notes)
            _logger.LogWarning("{Label} 용접선 환산 주의: {Note}", label, note);

        _logger.LogInformation(
            "{Label} 용접선 접근점: W=[{Sx:0.###},{Sy:0.###},{Sz:0.###}]m → BASE [{Bx:0.0},{By:0.0},{Bz:0.0}]mm " +
            "(standoff {Standoff:0}mm, wall={Wall}, 면까지 법선거리 {Dist:0}mm, 스트로크 {Stroke:0}mm)",
            label, seam[0], seam[1], seam[2],
            target.ApproachBaseMm[0], target.ApproachBaseMm[1], target.ApproachBaseMm[2],
            standoff, context.WallCode ?? "(미지정)", target.NormalDistanceMm, stroke);

        // ── 리치 사전 점검 — IK 에 특이 해를 풀게 하기 전에 배치로 거른다 ──────────────
        double toolLen = 0;
        string? skipNote = null;
        string reachNote = "";
        try
        {
            var tc = tool == 0 ? new double[6] : await _cobot.Rpc.GetToolCoordAsync(tool, ct);
            toolLen = ApproachReach.ToolLengthMm(tc);
            if (toolLen < 1.0) skipNote = $"공구 #{tool} 오프셋이 0(플랜지 기준)";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { skipNote = $"공구 좌표 조회 실패: {ex.Message}"; }

        if (skipNote is not null)
        {
            // 읽기 한 번 실패로 검사를 죽이지 않는다 — 도달성은 IK·자세 한계 판정에만 의존하게 된다.
            _logger.LogWarning("{Label} 리치 사전 점검 생략 — {Note}.", label, skipNote);
        }
        else
        {
            var limits = await _limits.GetAsync();
            var reach = ApproachReach.Check(target.NormalDistanceMm, standoff, toolLen, limits.MinFlangeReachMm);
            _logger.LogInformation(
                "{Label} 리치 점검: 면까지 {Normal:0}mm − 후퇴 {Approach:0} − 공구 {Tool:0} = 플랜지 뻗음 {Flange:0}mm (최소 {Min:0}mm)",
                label, target.NormalDistanceMm, standoff, toolLen, reach.FlangeReachMm, limits.MinFlangeReachMm);

            reachNote = $" · 플랜지 뻗음 {reach.FlangeReachMm:0}mm(최소 {limits.MinFlangeReachMm:0}, 면까지 {target.NormalDistanceMm:0}mm)";
            if (!reach.Ok)
                return new SeamApproachResult(null, null, useComputed, "",
                    $"정차 거리 부족 — 필요 ≥ {reach.RequiredNormalDistanceMm:0}mm, 현재 {target.NormalDistanceMm:0}mm " +
                    $"(후퇴 {standoff:0} + 공구 {toolLen:0} + 최소 뻗음 {limits.MinFlangeReachMm:0}). " +
                    $"플랜지 뻗음이 {reach.FlangeReachMm:0}mm 라 팔을 접은 자세가 되고 손목 특이점(rc=38)이 납니다 — " +
                    $"AMR 을 벽에서 {reach.ShortfallMm:0}mm 더 떨어뜨려 정차하세요. " +
                    $"(최소 뻗음 {limits.MinFlangeReachMm:0}mm 는 실측 전 잠정값 — 용접 위치 시험 화면에서 조정 가능)");
        }

        // target.TargetPoseBase 는 wall_code 가 유효할 때만 non-null(광축이 면 법선을 향하는 전체 pose).
        // 토글과 무관하게 돌려주고, 사용 여부는 호출측이 UseComputedOrientation 으로 판단한다.
        return new SeamApproachResult(
            target.ApproachBaseMm, target.TargetPoseBase, useComputed,
            (standoffMm is null
                ? $"용접선 접근점(면 이격 {standoff:0}mm — ③ 카메라 목표거리)"
                : $"용접선 접근점(면 이격 {standoff:0}mm)") + reachNote, null, opticalAxis);
    }
}
