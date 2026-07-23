using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ③ 카메라 거리 정렬 — 깊이 ROI 10회 샘플링 후 툴 광축(설정 가능, 기본 +Z)으로 접근 보정.
/// 목표 거리는 시퀀스 페이지 파라미터(SequenceContext.CameraTargetDistanceMm, 기본 400mm)로 설정.
/// 이동은 <see cref="FlatSurfaceCenteringService.MoveToDistanceAsync"/> 공용 루틴(툴 프레임) 사용 —
/// 과거 BASE -Y 하드코딩은 헤드 방향과 무관하게 BASE Y로 움직이는 좌표계 오류였음.
/// 선행조건: 현재 자세가 ② 단계의 목표점(검사 준비 위치 ⊕ u/v 툴 오프셋)이어야 함.
/// </summary>
public class CameraAlignStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly ParameterService _param;
    private readonly ILogger<CameraAlignStep> _logger;

    private const double MaxAlignTravelMm = 600;
    private const double PoseTolMm = 3.0;
    private const double PoseTolDeg = 2.0;

    // 깊이 ROI 파라미터 키 — CameraView 페이지와 공유.
    private const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    private const string RoiXKey = "Camera.Depth.Roi.X";
    private const string RoiYKey = "Camera.Depth.Roi.Y";
    private const string RoiWKey = "Camera.Depth.Roi.W";
    private const string RoiHKey = "Camera.Depth.Roi.H";

    // 광축(전방) → 툴축 매핑 키 — CameraView 에서 실측 확인 후 저장한 값을 공유.
    private const string AlignDepthAxisKey = "Camera.Align.DepthAxis";

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

        if (!context.Positions.TryGetValue("inspectionReady", out var pos) || !pos.IsTaught)
            return StepValidation.Fail("검사 준비 위치 미티칭 — Teaching에서 먼저 저장하세요.");

        if (context.CameraTargetDistanceMm is < 100 or > 1000)
            return StepValidation.Fail("카메라 목표 거리 범위 초과 (100~1000mm).");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (_camera.LatestDepth is null)
            return StepResult.Fail("깊이 프레임이 없습니다.");

        var inspection = context.Positions["inspectionReady"];

        // 1) 현재 자세가 ②의 목표점(검사 준비 위치 ⊕ u/v 툴 오프셋)인지 TCP 포즈로 검증.
        //    티칭 관절 비교는 u/v 오프셋·작업물 추종 시 목표가 티칭 자세와 달라져 오판한다.
        var (anchor, _) = await CobotInspectionMoveStep.ComputeTargetPoseAsync(_cobot, inspection, ct);
        var rz = context.InspectionDirection == InspectionMoveDirection.Vertical ? -90.0 : 0.0;
        var uvOffset = new[] { context.InspectionOffsetV, context.InspectionOffsetU, 0.0, 0.0, 0.0, rz };
        var expected = FrameMath.FromFrame(uvOffset, anchor);   // T_anchor · T_offset (툴프레임 합성)
        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        if (!IsAtPose(cur, expected))
            return StepResult.Fail("② 단계 목표 위치(검사 준비 ⊕ u/v 오프셋)가 아닙니다 — 먼저 ② 단계를 실행하세요.");

        // 2) 거리 측정 + 툴 광축 접근 보정 (공용 루틴 — 툴 프레임)
        var (rx, ry, rw, rh, roiSrc) = await GetDepthRoiAsync();
        var depthAxis = await GetDepthAxisAsync();
        _logger.LogInformation(
            "Sequence ③ 거리 정렬: 목표={Dist}mm, ROI={Roi}({Rx:0.00},{Ry:0.00},{Rw:0.00},{Rh:0.00}), 광축=툴{Axis}",
            context.CameraTargetDistanceMm, roiSrc, rx, ry, rw, rh, depthAxis);

        var r = await _centering.MoveToDistanceAsync(new DepthDistanceMoveOptions
        {
            RoiX = rx, RoiY = ry, RoiW = rw, RoiH = rh,
            TargetDistanceMm = context.CameraTargetDistanceMm,
            ToleranceMm = 1.0,
            MaxTravelMm = MaxAlignTravelMm,
            DepthAxis = depthAxis,
            Tool = 1,
            Velocity = context.Velocity,
        }, progress: null, ct);

        // 공용 루틴은 취소를 삼키고 실패 결과로 반환 — 스텝은 기존처럼 취소 예외로 전파한다.
        ct.ThrowIfCancellationRequested();
        return r.Success ? StepResult.Ok(r.Message) : StepResult.Fail(r.Message);
    }

    /// <summary>카메라 페이지에서 저장한 광축(전방)→툴축 매핑이 있으면 사용, 없으면 기본(+Z).
    /// 폴백은 침묵시키지 않고 경고 로그로 드러낸다 — 방향 오동작 원인 판별용.</summary>
    private async Task<ToolAxisDir> GetDepthAxisAsync()
    {
        try
        {
            var v = await _param.GetDoubleAsync(AlignDepthAxisKey);
            if (v is >= 0 and <= 5) return (ToolAxisDir)(int)v.Value;
            _logger.LogWarning(
                "광축 매핑 파라미터 없음/범위 밖 (값={V}) — 기본 +Z 사용. 카메라 페이지에서 광축을 설정·저장하세요.", v);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "광축 매핑 파라미터 읽기 실패 — 기본 +Z 사용.");
        }
        return ToolAxisDir.PlusZ;
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

    /// <summary>현재 TCP 포즈가 기대 포즈와 위치 ≤ <see cref="PoseTolMm"/>mm,
    /// 자세 축별 ≤ <see cref="PoseTolDeg"/>° (±180° 랩어라운드 처리) 이내인지.</summary>
    private static bool IsAtPose(double[] cur, double[] expected)
    {
        double dx = cur[0] - expected[0], dy = cur[1] - expected[1], dz = cur[2] - expected[2];
        if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > PoseTolMm) return false;

        for (var i = 3; i < 6; i++)
        {
            var d = Math.Abs(cur[i] - expected[i]) % 360.0;
            if (d > 180.0) d = 360.0 - d;
            if (d > PoseTolDeg) return false;
        }
        return true;
    }
}
