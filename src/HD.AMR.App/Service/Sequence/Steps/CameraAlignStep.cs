using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

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

    private const double MaxAlignTravelMm = 1000;
    private const double PoseTolMm = 3.0;
    private const double PoseTolDeg = 2.0;

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

        if (CobotInspectionMoveStep.FindBySurfaceId(context) is not { IsTaught: true })
            return StepValidation.Fail(
                $"Wall 0x{context.InspectionSurfaceId:X2} 검사 위치 미티칭/없음 — Teaching에서 먼저 저장하세요.");

        if (context.CameraTargetDistanceMm is < 100 or > 1000)
            return StepValidation.Fail("카메라 목표 거리 범위 초과 (100~1000mm).");

        return StepValidation.Ok();
    }

    /// <summary>
    /// "② 목표에 와 있는가" 판정의 기준을 고른다. 세 경우가 서로 다르다:
    /// <list type="number">
    /// <item>②가 남긴 지령 목표(Bag) — 유일하게 정확한 기준.</item>
    /// <item>Bag 없음 + seamStartW 없음 = UI 세미오토 단독 실행 — 종전대로 티칭 ⊕ u/v 로 판정한다
    ///   (②를 돌리지 않고 ③만 눌러 쓰는 기존 용법 유지).</item>
    /// <item>Bag 없음 + seamStartW 있음 = ACS 경로인데 ② 지령 기록이 없다 — 티칭 기준 재계산은
    ///   seam 접근점과 다르므로 비교할 수 없다. <b>하드 실패시키지 않고 생략</b>한다.</item>
    /// </list>
    /// ③이 티칭에서 기대값을 재계산하던 종전 코드는 ③번 경우에서 ACS 자동 실행을 통째로 막았다
    /// (2026-09-25 회귀).
    /// </summary>
    internal static (Task<double[]>? Expected, string Basis, string? Skip) ResolveGateReference(
        SequenceContext context, Func<Task<double[]>> teaching)
    {
        if (context.Bag.TryGetValue(WeldSequenceSupport.InspectTargetPoseBagKey, out var v)
            && v is double[] { Length: 6 } commanded)
            return (Task.FromResult(commanded), "② 지령 목표", null);

        if (context.SeamStartW is { Length: 3 })
            return (null, "", "② 지령 목표가 Bag 에 없는데 ACS 용접선(seamStartW)이 실려 있습니다 — " +
                              "티칭 기준 재계산은 접근점과 달라 비교하지 않습니다(② 실행 여부를 확인하세요).");

        return (teaching(), "검사 준비 티칭 ⊕ u/v 오프셋", null);
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (_camera.LatestDepth is null)
            return StepResult.Fail("깊이 프레임이 없습니다.");

        var inspection = CobotInspectionMoveStep.FindBySurfaceId(context)
            ?? throw new InvalidOperationException($"Wall 0x{context.InspectionSurfaceId:X2} 티칭 위치 없음");

        // 1) 현재 자세가 ②의 목표점(검사 준비 위치 ⊕ u/v 툴 오프셋)인지 TCP 포즈로 검증.
        //    티칭 관절 비교는 u/v 오프셋·작업물 추종 시 목표가 티칭 자세와 달라져 오판한다.
        //    기준은 ② 가 Bag 에 남긴 '실제로 지령한 목표' 다. 티칭 위치로 재계산하면 ACS 경로에서
        //    ② 가 seamStartW 환산 접근점으로 간 경우 두 좌표가 달라 항상 실패한다(액션마다 용접선이
        //    다르므로 티칭 위치와 3mm 안에 들 이유가 없다). 키가 없으면 ② 를 건너뛴 세미오토 단독
        //    실행이므로 종전대로 티칭 기반으로 계산한다.
        var (expected, basis, skip) = ResolveGateReference(context,
            async () =>
            {
                var (anchor, _) = await CobotInspectionMoveStep.ComputeTargetPoseAsync(_cobot, inspection, ct);
                anchor = CobotInspectionMoveStep.NormalizeUvAnchor(anchor);   // ② 와 동일한 앵커 정규화
                var composed = CobotInspectionMoveStep.ComposeUvTarget(anchor, context);
                var (pose, _) = await CobotInspectionMoveStep.AlignTwistToJ6Async(
                    _cobot, composed, context.Tool, CobotInspectionMoveStep.TargetJ6Deg(context), ct);
                return pose;
            });

        if (skip is not null)
        {
            // 검증 불가를 실패로 바꾸는 것이 바로 이번 회귀였다 — 기준을 못 만들면 건너뛴다.
            _logger.LogWarning("③ ② 목표 위치 검증 생략 — {Reason}", skip);
            context.Progress?.Invoke($"② 목표 검증 생략 — {skip}");
        }
        else
        {
            var target = await expected!;
            var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            if (!IsAtPose(cur, target))
            {
                var d = Math.Sqrt((cur[0] - target[0]) * (cur[0] - target[0]) +
                                  (cur[1] - target[1]) * (cur[1] - target[1]) +
                                  (cur[2] - target[2]) * (cur[2] - target[2]));
                return StepResult.Fail(
                    $"② 단계 목표 위치가 아닙니다 — 기준: {basis}, 위치 오차 {d:0.#}mm " +
                    $"(허용 {PoseTolMm:0.#}mm·{PoseTolDeg:0.#}°). 먼저 ② 단계를 실행하세요.");
            }
            _logger.LogInformation("③ 위치 확인 기준: {Basis}", basis);
        }

        // 2) 거리 측정 + 툴 광축 접근 보정 (공용 루틴 — 툴 프레임)
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
