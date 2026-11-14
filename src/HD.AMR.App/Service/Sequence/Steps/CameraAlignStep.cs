using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ③ 카메라 거리 정렬 — 깊이 ROI 10회 샘플링 후 툴 광축(설정 가능, 기본 +Z)으로 접근 보정.
/// 목표 거리는 시퀀스 페이지 파라미터(SequenceContext.CameraTargetDistanceMm, 기본 400mm)로 설정.
/// 이동은 <see cref="FlatSurfaceCenteringService.MoveToDistanceAsync"/> 공용 루틴(툴 프레임) 사용 —
/// 과거 BASE -Y 하드코딩은 헤드 방향과 무관하게 BASE Y로 움직이는 좌표계 오류였음.
/// </summary>
public class CameraAlignStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly ParameterService _param;
    private readonly ILogger<CameraAlignStep> _logger;

    private const double MaxAlignTravelMm = 1000;

    /// <summary>③ 시작 거리가 기대 거리(<see cref="ExpectedStartDistanceMm"/>)에서 이만큼 넘게 다르면 움직이지 않는다(mm, 0=검사 안 함).</summary>
    public const string ExpectedToleranceKey = "Sequence.CameraAlign.ExpectedTolMm";
    public const double DefaultExpectedToleranceMm = 200;

    /// <summary>
    /// 기대보다 가깝게 측정돼도(목표 거리 이상이면) 정렬을 계속할지 — 파라미터 키. 미설정 = 켬.
    /// 끄면 가까운 쪽도 기대 − 허용폭까지만 받는다(종전 대칭 동작).
    /// </summary>
    public const string AllowCloserKey = "Sequence.CameraAlign.AllowCloserThanExpected";

    /// <summary>
    /// depth 카메라 측정 원점이 툴 TCP 보다 광축 방향으로 뒤에 있는 거리(mm). ② 접근 거리(<c>inspection.approachStandoffMm</c>)는
    /// <b>TCP–면</b> 거리이고 ③ 이 재는 값은 <b>카메라–면</b> 거리라, ② 직후 측정은 접근 거리보다 이만큼 길다.
    /// 2026-11-09 실측: 접근 400mm 에서 천장 T 680~691mm·경사면 PU 645mm(≈ +245~291) — 기본값은 그 가운데.
    /// 툴·카메라 장착이 바뀌면 다시 재서 맞출 것(0~600, 범위 밖이면 기본값).
    /// </summary>
    public const string DepthOffsetKey = "Sequence.CameraAlign.DepthOffsetMm";
    public const double DefaultDepthOffsetMm = 280;
    public const double MaxDepthOffsetMm = 600;

    /// <summary>② 직후 ③ 시작 측정의 기대 거리 = ② 접근 거리(TCP–면) + 카메라–TCP 광축 거리.</summary>
    public static double ExpectedStartDistanceMm(double approachStandoffMm, double depthOffsetMm) =>
        approachStandoffMm + depthOffsetMm;

    /// <summary>파라미터 값 정리 — 없거나 0~<see cref="MaxDepthOffsetMm"/> 밖이면 <see cref="DefaultDepthOffsetMm"/>.</summary>
    public static double ResolveDepthOffsetMm(double? raw) =>
        raw is { } v && double.IsFinite(v) && v >= 0 && v <= MaxDepthOffsetMm ? v : DefaultDepthOffsetMm;

    // 깊이 ROI 파라미터 키 — CameraView 페이지와 공유.
    private const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    private const string RoiXKey = "Camera.Depth.Roi.X";
    private const string RoiYKey = "Camera.Depth.Roi.Y";
    private const string RoiWKey = "Camera.Depth.Roi.W";
    private const string RoiHKey = "Camera.Depth.Roi.H";

    public CameraAlignStep(CobotService cobot, CameraService camera,
        FlatSurfaceCenteringService centering,
        ParameterService param, ILogger<CameraAlignStep> logger)
    {
        _cobot = cobot;
        _camera = camera;
        _centering = centering;
        _param = param;
        _logger = logger;
    }

    public string Key => "cameraAlign";
    public string DisplayName => "카메라 거리 정렬";
    public int DefaultOrder => 300;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        if (!_camera.IsConnected)
            return StepValidation.Fail("카메라 미연결 — depth 측정 불가");

        if (context.CameraTargetDistanceMm is < 100 or > 1000)
            return StepValidation.Fail("카메라 목표 거리 범위 초과 (100~1000mm).");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (_camera.LatestDepth is null)
            return StepResult.Fail("깊이 프레임이 없습니다.");

        // 거리 측정 + 툴 광축 접근 보정 (공용 루틴 — 툴 프레임). 현재 위치에서 바로 측정한다.
        var (rx, ry, rw, rh, roiSrc) = await GetDepthRoiAsync(_param);
        var (depthAxis, axisFromParam) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        if (!axisFromParam)
            return StepResult.Fail(WeldSequenceSupport.OpticalAxisMissingMessage(DisplayName));
        _logger.LogInformation(
            "Sequence [카메라 거리 정렬] 목표={Dist}mm, ROI={Roi}({Rx:0.00},{Ry:0.00},{Rw:0.00},{Rh:0.00}), 광축=툴{Axis}",
            context.CameraTargetDistanceMm, roiSrc, rx, ry, rw, rh, depthAxis);
        context.Progress?.Invoke(
            $"거리 정렬 시작 — 목표 {context.CameraTargetDistanceMm:0}mm (ROI={roiSrc}, 광축=툴{depthAxis})");

        // ②가 이번 사이클에 용접선 접근점으로 왔으면(용접선 점 S 기록) 시작 거리는 대략 접근 거리여야 한다.
        double? expected = null;
        var expectedTol = await _param.GetDoubleAsync(ExpectedToleranceKey) ?? DefaultExpectedToleranceMm;
        if (expectedTol > 0 && context.Bag.ContainsKey(WeldSequenceSupport.InspectAnchorSeamPointBagKey))
        {
            // ② 접근 거리는 TCP–면, ③ 측정은 카메라–면 — 카메라가 TCP 뒤에 있는 만큼 더한다(2026-11-09 현장: 688mm 를 400±200 으로 거부하던 결함).
            var approach = await CobotInspectionMoveStep.GetApproachStandoffMmAsync(_param, _logger);
            double? rawOffset = null;
            try { rawOffset = await _param.GetDoubleAsync(DepthOffsetKey); }
            catch { /* DB 미준비 등 — 기본값 */ }
            expected = ExpectedStartDistanceMm(approach, ResolveDepthOffsetMm(rawOffset));
            _logger.LogInformation("Sequence [카메라 거리 정렬] 시작 거리 기대 {Exp:0}±{Tol:0}mm (② 접근 {App:0} + 카메라–TCP {Off:0})",
                expected, expectedTol, approach, ResolveDepthOffsetMm(rawOffset));
        }

        // 가까운 쪽은 목표 거리까지 받는다 — 벽이 모델보다 가까우면 덜 전진할 뿐이다(먼 쪽은 그대로 막는다).
        bool allowCloser;
        try { allowCloser = await _param.GetBoolAsync(AllowCloserKey) ?? true; }
        catch { allowCloser = true; }

        var r = await _centering.MoveToDistanceAsync(new DepthDistanceMoveOptions
        {
            ExpectedDistanceMm = expected,
            ExpectedToleranceMm = expectedTol,
            ExpectedNearFloorMm = allowCloser ? context.CameraTargetDistanceMm : null,
            RoiX = rx, RoiY = ry, RoiW = rw, RoiH = rh,
            TargetDistanceMm = context.CameraTargetDistanceMm,
            ToleranceMm = 1.0,
            MaxTravelMm = MaxAlignTravelMm,
            DepthAxis = depthAxis,
            Tool = context.Tool,
            Velocity = context.Velocity,
        }, progress: context.Progress, ct);

        // 공용 루틴은 취소를 삼키고 실패 결과로 반환 — 스텝은 기존처럼 취소 예외로 전파한다.
        ct.ThrowIfCancellationRequested();
        return r.Success ? StepResult.Ok(r.Message) : StepResult.Fail(r.Message);
    }

    /// <summary>카메라 페이지에서 저장한 ROI가 있으면 사용, 없으면 중앙 30% 기본영역. ③-1 재정렬과 공유.</summary>
    internal static async Task<(double X, double Y, double W, double H, string Src)> GetDepthRoiAsync(ParameterService param)
    {
        try
        {
            if (await param.GetBoolAsync(RoiEnabledKey) == true)
            {
                var x = await param.GetDoubleAsync(RoiXKey) ?? 0;
                var y = await param.GetDoubleAsync(RoiYKey) ?? 0;
                var w = await param.GetDoubleAsync(RoiWKey) ?? 0;
                var h = await param.GetDoubleAsync(RoiHKey) ?? 0;
                if (w > 0 && h > 0 && x + w <= 1.0001 && y + h <= 1.0001)
                    return (x, y, w, h, "저장 ROI");
            }
        }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }
        return (0.35, 0.35, 0.30, 0.30, "중앙 기본 ROI");
    }
}
