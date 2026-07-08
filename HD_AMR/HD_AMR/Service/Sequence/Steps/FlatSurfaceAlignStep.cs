using HD_AMR.Communication;
using HD_AMR.Models;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ④ 평탄면 센터링 — 레이저 변위센서 3점 측량을 위해 가장 평평한 면을 센서 중앙에 위치시킨다.
///
/// 3-Phase 알고리즘:
///   A) 뎁스 카메라 깊이 프레임을 그리드로 분할, depth σ 최소 셀(= 최평탄 영역) 탐색.
///   B) 평탄 셀 중심과 현재 센서 중심의 오프셋을 mm로 환산, 코봇 횡이동(MoveByOffset).
///   C) 레이저 변위센서 3점 측정으로 평면 틸트(rx, ry) 검증. 임계값 초과 시 미세보정 반복.
/// </summary>
public class FlatSurfaceAlignStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ParameterService _param;
    private readonly ILogger<FlatSurfaceAlignStep> _logger;

    // ── 설정 상수 ──────────────────────────────────────────────────────
    /// <summary>평탄 판정 틸트 임계값(도). |rx|, |ry| 모두 이 값 이내이면 정렬 완료.</summary>
    private const double TiltThresholdDeg = 0.5;

    /// <summary>미세보정 최대 반복 횟수 (발산 방지).</summary>
    private const int MaxCorrectionIterations = 5;

    /// <summary>미세보정 1회 최대 이동량(mm). 폭주 방지 가드.</summary>
    private const double MaxCorrectionMoveMm = 20.0;

    /// <summary>그리드 분할 수 (gridSize × gridSize 셀).</summary>
    private const int GridSize = 5;

    /// <summary>Phase B 횡이동 최대 허용량(mm). 가드.</summary>
    private const double MaxLateralMoveMm = 100.0;

    // 깊이 ROI 파라미터 키 — CameraView/CameraAlignStep 과 공유.
    private const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    private const string RoiXKey = "Camera.Depth.Roi.X";
    private const string RoiYKey = "Camera.Depth.Roi.Y";
    private const string RoiWKey = "Camera.Depth.Roi.W";
    private const string RoiHKey = "Camera.Depth.Roi.H";

    public FlatSurfaceAlignStep(
        CobotService cobot, CameraService camera,
        LaserDisplacementSensorService laser, ParameterService param,
        ILogger<FlatSurfaceAlignStep> logger)
    {
        _cobot = cobot;
        _camera = camera;
        _laser = laser;
        _param = param;
        _logger = logger;
    }

    public string Key => "flatSurfaceAlign";
    public string DisplayName => "평탄면 센터링 (레이저 정렬)";
    public int DefaultOrder => 400;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");
        if (!_camera.IsConnected)
            return StepValidation.Fail("카메라 미연결 — 평탄영역 탐색 불가");
        if (!_laser.IsConnected)
            return StepValidation.Fail("레이저 변위센서 미연결 — 3점 측량 불가");
        if (!context.Positions.TryGetValue("inspectionReady", out var pos) || !pos.IsTaught)
            return StepValidation.Fail("검사 준비 위치 미티칭");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        // ── Phase A: 뎁스 카메라 평탄영역 탐색 ─────────────────────────
        _logger.LogInformation("④ Phase A: 뎁스 카메라 평탄영역 탐색 시작 (grid={Grid}×{Grid})", GridSize, GridSize);

        var (roiX, roiY, roiW, roiH, roiSrc) = await GetDepthRoiAsync();

        // 10회 샘플 중 최적 결과 채택 (뎁스 노이즈 평활화)
        CameraService.DepthFlatnessResult? bestFlat = null;
        for (var i = 0; i < 10; i++)
        {
            var flat = _camera.FindFlattest(roiX, roiY, roiW, roiH, GridSize);
            if (flat is not null && (bestFlat is null || flat.SigmaMm < bestFlat.SigmaMm))
                bestFlat = flat;
            if (i < 9) await Task.Delay(100, ct);
        }

        if (bestFlat is null)
            return StepResult.Fail("깊이 프레임에서 유효한 평탄영역을 찾을 수 없습니다.");

        _logger.LogInformation(
            "④ Phase A 완료: 최평탄 셀 u={U:0.###}, v={V:0.###}, σ={Sigma:0.##}mm (ROI={Roi})",
            bestFlat.U, bestFlat.V, bestFlat.SigmaMm, roiSrc);

        // ── Phase B: 코봇 횡이동 ──────────────────────────────────────
        // 현재 센서 중심 = ROI 중심(정규화). 평탄 셀 중심과의 Δ를 mm로 환산.
        double centerU = roiX + roiW / 2.0;
        double centerV = roiY + roiH / 2.0;
        double deltaU = bestFlat.U - centerU;   // 양수 = 오른쪽
        double deltaV = bestFlat.V - centerV;   // 양수 = 아래쪽

        // 평탄 셀이 이미 중심 근방이면 Phase B 생략
        if (Math.Abs(deltaU) < 0.02 && Math.Abs(deltaV) < 0.02)
        {
            _logger.LogInformation("④ Phase B 생략: 평탄 셀이 이미 센서 중심 근방");
        }
        else
        {
            // 픽셀 오프셋 → mm 변환: depth intrinsics 사용 (가용 시) 또는 FOV 근사
            var (deltaXmm, deltaYmm) = PixelOffsetToMm(deltaU, deltaV);

            if (Math.Abs(deltaXmm) > MaxLateralMoveMm || Math.Abs(deltaYmm) > MaxLateralMoveMm)
                return StepResult.Fail(
                    $"횡이동량 ({deltaXmm:0.#}, {deltaYmm:0.#})mm가 한계({MaxLateralMoveMm}mm) 초과.");

            var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            // 뎁스 이미지 X → BASE X, 뎁스 이미지 Y → BASE Z (카메라가 전방을 향할 때)
            // 실제 매핑은 카메라 장착 방향에 따라 다르므로 주석으로 설명.
            // 기본 가정: 카메라 X축 = 코봇 BASE X, 카메라 Y축 = 코봇 BASE -Z
            var offset = new[] { deltaXmm, 0.0, -deltaYmm, 0.0, 0.0, 0.0 };

            _logger.LogInformation(
                "④ Phase B: 횡이동 Δu={Du:0.###}, Δv={Dv:0.###} → offset=[{Ox:0.#},{Oy:0.#},{Oz:0.#}]mm",
                deltaU, deltaV, offset[0], offset[1], offset[2]);

            var rc = await _cobot.Rpc.MoveByOffsetAsync(anchor, user: 0, offset,
                tool: context.Tool, vel: context.Velocity, ct: ct);
            if (rc != 0)
                return StepResult.Fail($"횡이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

            // 이동 후 안정화 대기
            await Task.Delay(500, ct);
        }

        // ── Phase C: 레이저 3점 검증/미세보정 ──────────────────────────
        _logger.LogInformation("④ Phase C: 레이저 3점 평면 측정 시작");

        for (var iter = 0; iter < MaxCorrectionIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            // 안정화 후 측정 (3회 샘플 평균)
            var pose = await SamplePlanePoseAsync(ct);
            if (!pose.Valid)
                return StepResult.Fail($"레이저 평면 측정 실패: {pose.Note}");

            _logger.LogInformation(
                "④ Phase C iter={Iter}: rx={Rx:0.###}°, ry={Ry:0.###}°, z={Z:0.#}mm",
                iter, pose.Rx, pose.Ry, pose.Z);

            // 판정: 두 축 모두 임계값 이내면 완료
            if (Math.Abs(pose.Rx) < TiltThresholdDeg && Math.Abs(pose.Ry) < TiltThresholdDeg)
            {
                return StepResult.Ok(
                    $"평탄면 정렬 완료 (rx={pose.Rx:0.###}°, ry={pose.Ry:0.###}°, " +
                    $"z={pose.Z:0.#}mm, 반복={iter}회, σ={bestFlat.SigmaMm:0.##}mm).");
            }

            // 미세보정: 틸트 방향으로 소량 이동
            // ry(Y축 틸트) → X방향 이동, rx(X축 틸트) → Y방향 이동
            // 보정량 = tan(틸트) × 현재 거리 (근사)
            double distMm = pose.Z > 0 ? pose.Z : 400.0;
            double corrX = Math.Tan(pose.Ry * Math.PI / 180.0) * distMm;
            double corrY = Math.Tan(pose.Rx * Math.PI / 180.0) * distMm;

            // 가드: 보정량 클램프
            corrX = Math.Clamp(corrX, -MaxCorrectionMoveMm, MaxCorrectionMoveMm);
            corrY = Math.Clamp(corrY, -MaxCorrectionMoveMm, MaxCorrectionMoveMm);

            if (Math.Abs(corrX) < 0.1 && Math.Abs(corrY) < 0.1)
            {
                return StepResult.Ok(
                    $"평탄면 정렬 완료 (보정량 미미, rx={pose.Rx:0.###}°, ry={pose.Ry:0.###}°).");
            }

            var corrAnchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            var corrOffset = new[] { corrX, corrY, 0.0, 0.0, 0.0, 0.0 };

            _logger.LogInformation("④ Phase C 보정 iter={Iter}: offset=[{Cx:0.##},{Cy:0.##}]mm",
                iter, corrX, corrY);

            var corrRc = await _cobot.Rpc.MoveByOffsetAsync(corrAnchor, user: 0, corrOffset,
                tool: context.Tool, vel: Math.Min(context.Velocity, 10), ct: ct);
            if (corrRc != 0)
                return StepResult.Fail($"미세보정 이동 실패 (rc={corrRc}){FairinoErrorCodes.Suffix(corrRc)}.");

            await Task.Delay(300, ct);
        }

        // 최대 반복 도달
        var finalPose = await SamplePlanePoseAsync(ct);
        return StepResult.Fail(
            $"미세보정 {MaxCorrectionIterations}회 반복 후에도 평탄 기준 미달 " +
            $"(rx={finalPose.Rx:0.###}°, ry={finalPose.Ry:0.###}°, 기준={TiltThresholdDeg}°).");
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────

    /// <summary>레이저 3점 측정을 3회 샘플링해 평균 pose 반환.</summary>
    private async Task<PlanePose> SamplePlanePoseAsync(CancellationToken ct)
    {
        double rxSum = 0, rySum = 0, rzSum = 0, zSum = 0;
        int validCount = 0;
        string? lastNote = null;

        for (var i = 0; i < 3; i++)
        {
            var p = _laser.GetPlanePose();
            if (p.Valid)
            {
                rxSum += p.Rx;
                rySum += p.Ry;
                rzSum += p.Rz;
                zSum += p.Z;
                validCount++;
            }
            else
            {
                lastNote = p.Note;
            }
            if (i < 2) await Task.Delay(100, ct);
        }

        if (validCount == 0)
            return PlanePose.Invalid(lastNote ?? "3회 측정 모두 무효");

        return new PlanePose(
            0, 0, zSum / validCount,
            rxSum / validCount, rySum / validCount, rzSum / validCount,
            new double[] { 0, 0, 1 }, true, null);
    }

    /// <summary>
    /// 정규화 픽셀 오프셋(Δu, Δv)을 현재 거리 기준 mm로 변환.
    /// 변환 자체는 <see cref="CameraService.PixelDeltaToMm"/>(intrinsics 우선, FOV 근사 폴백)에 위임.
    /// </summary>
    private (double DxMm, double DyMm) PixelOffsetToMm(double deltaU, double deltaV)
        => _camera.PixelDeltaToMm(deltaU, deltaV, EstimateAvgDepth());

    /// <summary>현재 깊이 ROI 평균 거리 추정. 실패 시 400mm 기본값.</summary>
    private double EstimateAvgDepth()
    {
        var stats = _camera.ComputeDepthRoiStats(0.3, 0.3, 0.4, 0.4);
        return stats is { AvgMm: > 0 } ? stats.AvgMm : 400.0;
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
