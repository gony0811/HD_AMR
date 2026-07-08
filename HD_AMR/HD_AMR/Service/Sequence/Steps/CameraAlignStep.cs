using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ③ 카메라 거리 정렬 (400mm) — 깊이 ROI 10회 샘플링 후 tool 1을 BASE -Y로 보정.
/// 선행조건: 현재 자세가 검사 준비 위치여야 함.
/// </summary>
public class CameraAlignStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly ParameterService _param;
    private readonly ILogger<CameraAlignStep> _logger;

    private const double TargetDistanceMm = 400;
    private const double MaxAlignTravelMm = 600;
    private const double HomeToleranceDeg = 0.5;

    // 깊이 ROI 파라미터 키 — CameraView 페이지와 공유.
    private const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    private const string RoiXKey = "Camera.Depth.Roi.X";
    private const string RoiYKey = "Camera.Depth.Roi.Y";
    private const string RoiWKey = "Camera.Depth.Roi.W";
    private const string RoiHKey = "Camera.Depth.Roi.H";

    public CameraAlignStep(CobotService cobot, CameraService camera,
        ParameterService param, ILogger<CameraAlignStep> logger)
    {
        _cobot = cobot;
        _camera = camera;
        _param = param;
        _logger = logger;
    }

    public string Key => "cameraAlign";
    public string DisplayName => "카메라 거리 정렬 (400mm)";
    public int DefaultOrder => 300;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        if (!_camera.IsConnected)
            return StepValidation.Fail("카메라 미연결 — depth 측정 불가");

        if (!context.Positions.TryGetValue("inspectionReady", out var pos) || !pos.IsTaught)
            return StepValidation.Fail("검사 준비 위치 미티칭 — Teaching에서 먼저 저장하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (_camera.LatestDepth is null)
            return StepResult.Fail("깊이 프레임이 없습니다.");

        var inspection = context.Positions["inspectionReady"];

        // 1) 현재 자세가 검사 준비 위치인지 검증
        var inspJoints = new[]
        {
            inspection.J1!.Value, inspection.J2!.Value, inspection.J3!.Value,
            inspection.J4!.Value, inspection.J5!.Value, inspection.J6!.Value,
        };
        var cur = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
        if (!IsWithinJointTolerance(cur, inspJoints))
            return StepResult.Fail("검사 준비 위치가 아닙니다 — 먼저 ② 단계를 실행하세요.");

        // 2) 깊이 ROI 10회 샘플링
        var (rx, ry, rw, rh, roiSrc) = await GetDepthRoiAsync();
        var samples = new List<int>(10);
        for (var i = 0; i < 10; i++)
        {
            var mm = _camera.ComputeDepthRoiStats(rx, ry, rw, rh)?.MinMm ?? 0;
            if (mm > 0) samples.Add(mm);
            if (i < 9) await Task.Delay(100, ct);
        }

        if (samples.Count == 0)
            return StepResult.Fail("유효한 깊이 측정값이 없습니다.");

        var d = samples.Average();
        var delta = d - TargetDistanceMm;

        // 3) 안전 가드
        if (Math.Abs(delta) > MaxAlignTravelMm)
            return StepResult.Fail(
                $"측정 {d:0.#}mm — 보정량 {Math.Abs(delta):0.#}mm가 한계({MaxAlignTravelMm:0}mm) 초과로 중단.");

        // 4) 이미 목표 범위이면 생략
        if (Math.Abs(delta) < 1.0)
            return StepResult.Ok($"측정 {d:0.#}mm — 이미 목표 {TargetDistanceMm:0}mm 범위(이동 생략).");

        // 5) 원샷 보정
        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(1, ct);
        var offset = new[] { 0.0, TargetDistanceMm - d, 0.0, 0.0, 0.0, 0.0 };

        _logger.LogInformation(
            "Sequence ③ 정렬 이동: ROI={Roi}({Rx:0.00},{Ry:0.00},{Rw:0.00},{Rh:0.00}) " +
            "측정 d={D:0.#}mm, BASE Y보정={OffY:+0.#;-0.#}mm, " +
            "anchor=[{Ax:0.#},{Ay:0.#},{Az:0.#},{Arx:0.#},{Ary:0.#},{Arz:0.#}]",
            roiSrc, rx, ry, rw, rh, d, offset[1],
            anchor[0], anchor[1], anchor[2], anchor[3], anchor[4], anchor[5]);

        var rc = await _cobot.Rpc.MoveByOffsetAsync(anchor, user: 0, offset,
            tool: 1, vel: context.Velocity, ct: ct);

        if (rc == 0)
            return StepResult.Ok(
                $"측정 {d:0.#}mm → -Y {Math.Abs(delta):0.#}mm 이동, 목표 {TargetDistanceMm:0}mm 정렬 완료.");

        if (rc == 112)
            return StepResult.Fail(
                $"정렬 이동 실패 (rc=112: 목표 자세 도달 불가). 측정 {d:0.#}mm, " +
                $"BASE Y {offset[1]:+0.#;-0.#}mm 보정 목표가 작업영역 밖입니다 — 측정값/방향 확인 필요.");

        return StepResult.Fail($"정렬 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
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

    private static bool IsWithinJointTolerance(double[] cur, double[] target)
    {
        for (var i = 0; i < 6; i++)
            if (Math.Abs(cur[i] - target[i]) > HomeToleranceDeg) return false;
        return true;
    }
}
