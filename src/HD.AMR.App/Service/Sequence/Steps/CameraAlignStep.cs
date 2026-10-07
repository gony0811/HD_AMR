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
        var (rx, ry, rw, rh, roiSrc) = await GetDepthRoiAsync();
        var (depthAxis, axisFromParam) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        if (!axisFromParam)
            _logger.LogWarning(
                "'{Key}' 광축 매핑 파라미터 없음/범위 밖 — 기본 +Z 사용. 카메라 페이지에서 광축을 설정·저장하세요.",
                WeldSequenceSupport.DepthAxisKey);
        _logger.LogInformation(
            "Sequence ③ 거리 정렬: 목표={Dist}mm, ROI={Roi}({Rx:0.00},{Ry:0.00},{Rw:0.00},{Rh:0.00}), 광축=툴{Axis}",
            context.CameraTargetDistanceMm, roiSrc, rx, ry, rw, rh, depthAxis);
        context.Progress?.Invoke(
            $"거리 정렬 시작 — 목표 {context.CameraTargetDistanceMm:0}mm (ROI={roiSrc}, 광축=툴{depthAxis})");

        var r = await _centering.MoveToDistanceAsync(new DepthDistanceMoveOptions
        {
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

    /// <summary>카메라 페이지에서 저장한 ROI가 있으면 사용, 없으면 중앙 30% 기본영역.</summary>
    private async Task<(double X, double Y, double W, double H, string Src)> GetDepthRoiAsync()
    {
        try
        {
            if (await _param.GetBoolAsync(RoiEnabledKey) == true)
            {
                var x = await _param.GetDoubleAsync(RoiXKey) ?? 0;
                var y = await _param.GetDoubleAsync(RoiYKey) ?? 0;
                var w = await _param.GetDoubleAsync(RoiWKey) ?? 0;
                var h = await _param.GetDoubleAsync(RoiHKey) ?? 0;
                if (w > 0 && h > 0 && x + w <= 1.0001 && y + h <= 1.0001)
                    return (x, y, w, h, "저장 ROI");
            }
        }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }
        return (0.35, 0.35, 0.30, 0.30, "중앙 기본 ROI");
    }
}
