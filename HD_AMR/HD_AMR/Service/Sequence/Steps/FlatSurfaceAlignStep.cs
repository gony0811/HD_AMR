using HD_AMR.Communication;
using HD_AMR.Models;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ④ 평탄면 센터링 — 레이저 변위센서 3점 측량을 위해 가장 평평한 면을 센서 중앙에 위치시킨다.
///
/// 3-Phase 알고리즘:
///   A) 뎁스 카메라 깊이 프레임을 그리드로 분할, depth σ 최소 셀(= 최평탄 영역) 탐색.
///   B) 평탄 셀 중심과 현재 센서 중심의 오프셋을 mm로 환산, 코봇 횡이동(툴 프레임 MoveByToolOffset —
///      이미지 평면과 평행, 대상면 거리 유지. FlatSurfaceCenteringService 공용 루틴).
///      이동 후 레이저 측정 중심 보정 횡이동(툴 −Y 65mm — 레이저 중심이 카메라보다 좌측 장착).
///   C) 레이저 변위센서 3점 측정으로 평면 틸트(rx, ry) 검증. 임계값 초과 시 측정 틸트만큼
///      툴 헤드를 회전 보정(위치 고정, /laser '보정 적용'과 동일 부호 규약) 후 재검증.
///
/// 시작 시점 TCP 포즈를 <see cref="WeldSequenceSupport.InspectAnchorPoseBagKey"/> 로 저장한다 —
/// ④⁺(레이저 WD)가 초점거리 조정 후 이 위치로 툴 X/Y 횡복귀한다(자세·초점거리는 유지).
/// </summary>
public class FlatSurfaceAlignStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ParameterService _param;
    private readonly ILogger<FlatSurfaceAlignStep> _logger;

    // ── 설정 상수 ──────────────────────────────────────────────────────
    /// <summary>평탄 판정 틸트 임계값(도). |rx|, |ry| 모두 이 값 이내이면 정렬 완료.</summary>
    private const double TiltThresholdDeg = 1.0;

    /// <summary>카메라→레이저 중심 보정 이동량 절대 상한(mm). 파라미터 오입력 가드.
    /// 실제 이동량은 시퀀스 페이지 파라미터(<see cref="SequenceContext.CameraToLaserShiftYmm"/>, 기본 −65mm)로 설정 —
    /// 레이저 중심이 카메라 대비 얼마나 좌측(툴 +Y)에 장착됐는지에 따른 장착 오프셋이라 DB로 외부화한다.</summary>
    private const double MaxCameraToLaserShiftMm = 200.0;

    /// <summary>틸트 회전 보정 최대 시도 횟수 (측정 노이즈 대비 재시도).</summary>
    private const int MaxTiltCorrections = 3;

    /// <summary>1회 회전 보정 클램프(°/축). 폭주 방지.</summary>
    private const double MaxTiltCorrectionDeg = 10.0;

    /// <summary>그리드 분할 수 (gridSize × gridSize 셀).</summary>
    private const int GridSize = 5;

    /// <summary>Phase B 횡이동 절대 상한(mm). 0 = 비활성 — ROI 물리 크기 기반 동적 한계만 적용
    /// (<see cref="FlatCenterAlignOptions.MaxLateralMoveMm"/> 참조).</summary>
    private const double MaxLateralMoveMm = 0.0;

    // 깊이 ROI 파라미터 키 — CameraView/CameraAlignStep 과 공유.
    private const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    private const string RoiXKey = "Camera.Depth.Roi.X";
    private const string RoiYKey = "Camera.Depth.Roi.Y";
    private const string RoiWKey = "Camera.Depth.Roi.W";
    private const string RoiHKey = "Camera.Depth.Roi.H";

    // 이미지 축 → 툴축 매핑 키 — CameraView 에서 실측 확인 후 저장한 값을 공유.
    private const string AlignImageXAxisKey = "Camera.Align.ImageXAxis";
    private const string AlignImageYAxisKey = "Camera.Align.ImageYAxis";

    public FlatSurfaceAlignStep(
        CobotService cobot, CameraService camera, FlatSurfaceCenteringService centering,
        LaserDisplacementSensorService laser, ParameterService param,
        ILogger<FlatSurfaceAlignStep> logger)
    {
        _cobot = cobot;
        _camera = camera;
        _centering = centering;
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
        // 시작 포즈(= ② 목표 ⊕ ③ 거리 정렬 지점)를 앵커로 저장 — ④⁺가 WD 조정 후
        // 이 위치로 툴 X/Y 횡복귀한다(⑤가 검사 준비 위치 정면에서 시작하도록).
        var startPose = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        context.Bag[WeldSequenceSupport.InspectAnchorPoseBagKey] = startPose;

        // ── Phase A+B: 평탄영역 탐색 + 코봇 횡이동 (공용 루틴) ─────────
        var (roiX, roiY, roiW, roiH, roiSrc) = await GetDepthRoiAsync();
        _logger.LogInformation("④ Phase A/B: 평탄영역 탐색+횡이동 시작 (grid={Grid}×{Grid}, ROI={Roi})",
            GridSize, GridSize, roiSrc);

        var (axisX, axisY) = await GetAxisMapAsync();
        var align = await _centering.RunAsync(new FlatCenterAlignOptions
        {
            RoiX = roiX, RoiY = roiY, RoiW = roiW, RoiH = roiH,
            GridSize = GridSize,
            SamplesPerDetect = 10,
            DeadbandUv = 0.02, ToleranceMm = 0.0,          // 기존과 동일: 정규화 데드밴드만 사용
            MaxIterations = 1, VerifyAfterMove = false,    // 기존과 동일: 1회 개루프 이동
            MaxLateralMoveMm = MaxLateralMoveMm,
            ImageXAxis = axisX, ImageYAxis = axisY,
            Tool = context.Tool, Velocity = context.Velocity,
        }, progress: null, ct);

        // 공용 루틴은 취소를 삼키고 실패 결과로 반환 — 스텝은 기존처럼 취소 예외로 전파한다.
        ct.ThrowIfCancellationRequested();
        if (!align.Success)
            return StepResult.Fail(align.Message);

        // ── 카메라 중심 → 레이저 측정 중심 횡이동 ─────────────────────
        // Phase B는 평탄영역을 카메라 중심에 맞추므로, 레이저 3점 중심이 그 지점 위에
        // 오도록 장착 오프셋만큼 이동한 뒤 측정한다. 이동량은 시퀀스 페이지 파라미터
        // (SequenceContext.CameraToLaserShiftYmm, 기본 −65mm)로 설정 — 장착 위치 종속이라 DB 영속.
        var shiftY = context.CameraToLaserShiftYmm;
        if (Math.Abs(shiftY) > MaxCameraToLaserShiftMm)
            return StepResult.Fail(
                $"카메라→레이저 중심 보정 이동량 {shiftY:+0.#;-0.#}mm 가 한계 ±{MaxCameraToLaserShiftMm:0}mm 초과 — " +
                "시퀀스 페이지 ④ '레이저중심 Y' 파라미터를 확인하세요.");
        _logger.LogInformation("④ 레이저 중심 보정 횡이동: 툴 Y {Shift}mm", shiftY);
        var shiftAnchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var shiftOffset = new[] { 0.0, shiftY, 0.0, 0.0, 0.0, 0.0 };
        var shiftRc = await _cobot.Rpc.MoveByToolOffsetAsync(shiftAnchor, user: 0, shiftOffset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (shiftRc != 0)
            return StepResult.Fail($"레이저 중심 보정 이동 실패 (rc={shiftRc}){FairinoErrorCodes.Suffix(shiftRc)}.");
        await Task.Delay(300, ct);

        // ── Phase C: 레이저 3점 측정 → 헤드 틸트(회전) 보정 ────────────
        _logger.LogInformation("④ Phase C: 레이저 3점 평면 측정 시작");

        for (var iter = 0; ; iter++)
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
                    $"z={pose.Z:0.#}mm, 틸트 보정={iter}회, σ={align.SigmaMm:0.##}mm).");
            }

            if (iter >= MaxTiltCorrections)
            {
                return StepResult.Fail(
                    $"틸트 보정 {MaxTiltCorrections}회 후에도 평탄 기준 미달 " +
                    $"(rx={pose.Rx:0.###}°, ry={pose.Ry:0.###}°, 기준={TiltThresholdDeg}°) — " +
                    "헤드 위치 캘리브레이션(/laser)으로 헤드 기하 확인이 필요합니다.");
            }

            // 측정 틸트만큼 툴 헤드를 회전 보정 (위치 고정, Rz=0 — 3점 거리로 yaw 미결정).
            // 부호는 /laser '보정 적용'과 동일한 실장비 검증 규약: 적용 Rx=+측정Rx, 적용 Ry=−측정Ry.
            var applyRx = Math.Clamp(pose.Rx, -MaxTiltCorrectionDeg, MaxTiltCorrectionDeg);
            var applyRy = Math.Clamp(-pose.Ry, -MaxTiltCorrectionDeg, MaxTiltCorrectionDeg);
            var corrOffset = new[] { 0.0, 0.0, 0.0, applyRx, applyRy, 0.0 };
            var corrAnchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);

            _logger.LogInformation(
                "④ Phase C 틸트 보정 iter={Iter}: 적용 Rx={Ax:0.###}°, Ry={Ay:0.###}°",
                iter, applyRx, applyRy);

            var corrRc = await _cobot.Rpc.MoveByToolOffsetAsync(corrAnchor, user: 0, corrOffset,
                tool: context.Tool, vel: Math.Min(context.Velocity, 10), ct: ct);
            if (corrRc != 0)
                return StepResult.Fail($"틸트 보정 이동 실패 (rc={corrRc}){FairinoErrorCodes.Suffix(corrRc)}.");

            await Task.Delay(300, ct);
        }
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

    /// <summary>카메라 페이지에서 저장한 이미지→툴축 매핑이 있으면 사용, 없으면 기본(+X/+Y).
    /// 폴백은 침묵시키지 않고 경고 로그로 드러낸다 — 방향 오동작 원인 판별용.</summary>
    private async Task<(ToolAxisDir X, ToolAxisDir Y)> GetAxisMapAsync()
    {
        try
        {
            var x = await _param.GetDoubleAsync(AlignImageXAxisKey);
            var y = await _param.GetDoubleAsync(AlignImageYAxisKey);
            if (x is >= 0 and <= 5 && y is >= 0 and <= 5)
                return ((ToolAxisDir)(int)x.Value, (ToolAxisDir)(int)y.Value);
            _logger.LogWarning(
                "이미지→툴축 매핑 파라미터 없음/범위 밖 (X={X}, Y={Y}) — 기본 +X/+Y 사용. 카메라 페이지에서 매핑을 설정·저장하세요.",
                x, y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "이미지→툴축 매핑 파라미터 읽기 실패 — 기본 +X/+Y 사용.");
        }
        return (ToolAxisDir.PlusX, ToolAxisDir.PlusY);
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
