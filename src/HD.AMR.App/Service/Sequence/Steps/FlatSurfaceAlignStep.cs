using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ④ 평탄면 센터링 — 레이저 변위센서 3점 측량을 위해 가장 평평한 면을 센서 중앙에 위치시킨다.
///
/// 3-Phase 알고리즘:
///   A) 뎁스 카메라 깊이 프레임을 그리드로 분할, depth σ 최소 셀(= 최평탄 영역) 탐색.
///   B) 평탄 셀 중심과 현재 센서 중심의 오프셋을 mm로 환산, 코봇 횡이동(툴 프레임 MoveByToolOffset —
///      이미지 평면과 평행, 대상면 거리 유지. FlatSurfaceCenteringService 공용 루틴).
///      이동 후 레이저 측정 중심 보정 횡이동(툴 −Y 75mm — 레이저 3점 중심이 카메라보다 좌측(툴 +Y) 75mm 장착).
///   C) 레이저 변위센서 3점 측정으로 평면 틸트(rx, ry) 검증. 임계값 초과 시 툴 헤드를 회전 보정(위치 고정) 후 재검증.
///      보정량은 고정 부호 규약이 아니라 <b>실측 응답</b>으로 정한다 — 툴 Rx/Ry 를 각각 소량 회전시켜
///      측정 틸트가 어떻게 변하는지(2×2 응답 행렬 J)를 잰 뒤 u = −J⁻¹·tilt 를 적용한다.
///      (2026-10-07/08 현장 로그: 종전 [+Rx, −Ry] 규약에서 툴 Rx +a → 측정 rx ≈ +a 로 오차가 매회 누적,
///      툴 Rx 가 측정 ry 도 크게 바꾸는 축 결합까지 있어 고정 부호로는 수렴하지 않았다.)
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
    private readonly LaserTiltCorrector _tilt;
    private readonly ParameterService _param;
    private readonly FlatDetectionMonitor _monitor;
    private readonly ILogger<FlatSurfaceAlignStep> _logger;

    // ── 설정 상수 ──────────────────────────────────────────────────────
    /// <summary>평탄 판정 틸트 임계값(도). |rx|, |ry| 모두 이 값 이내이면 정렬 완료.</summary>
    private const double TiltThresholdDeg = 1.0;

    /// <summary>카메라→레이저 중심 보정 이동량 절대 상한(mm). 파라미터 오입력 가드.
    /// 실제 이동량은 시퀀스 페이지 파라미터(<see cref="SequenceContext.CameraToLaserShiftYmm"/>, 기본 −75mm)로 설정 —
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
        LaserDisplacementSensorService laser, LaserTiltCorrector tilt, ParameterService param,
        FlatDetectionMonitor monitor, ILogger<FlatSurfaceAlignStep> logger)
    {
        _tilt = tilt;
        _cobot = cobot;
        _camera = camera;
        _centering = centering;
        _laser = laser;
        _param = param;
        _monitor = monitor;
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

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var startedAt = DateTime.Now;
        try
        {
            return await ExecuteCoreAsync(context, ct);
        }
        catch (OperationCanceledException)
        {
            // 취소가 검출 스냅샷에 '레이저 측정 중' 등으로 남지 않게 표시만 바꾸고 그대로 전파한다.
            // 이번 실행이 발행한 스냅샷일 때만 — 이전 결과를 '취소'로 덮어쓰지 않는다.
            if (_monitor.Latest is { } snap && snap.DetectedAt >= startedAt)
                _monitor.UpdateStage(FlatDetectionStage.Failed, "사용자 취소");
            throw;
        }
    }

    private async Task<StepResult> ExecuteCoreAsync(SequenceContext context, CancellationToken ct)
    {
        // 시작 포즈(= ② 목표 ⊕ ③ 거리 정렬 지점)를 앵커로 저장 — ④⁺가 WD 조정 후
        // 이 위치로 툴 X/Y 횡복귀한다(⑤가 검사 준비 위치 정면에서 시작하도록).
        var startPose = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        context.Bag[WeldSequenceSupport.InspectAnchorPoseBagKey] = startPose;

        // ── Phase A+B: 평탄영역 탐색 + 코봇 횡이동 (공용 루틴) ─────────
        var (roiX, roiY, roiW, roiH, roiSrc) = await GetDepthRoiAsync();
        _logger.LogInformation("④ Phase A/B: 평탄영역 탐색+횡이동 시작 (grid={Grid}×{Grid}, ROI={Roi})",
            GridSize, GridSize, roiSrc);

        context.Progress?.Invoke($"평탄영역 탐색 시작 (grid={GridSize}×{GridSize}, ROI={roiSrc})");

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
            Source = "시퀀스",
        }, progress: context.Progress, ct);

        // 공용 루틴은 취소를 삼키고 실패 결과로 반환 — 스텝은 기존처럼 취소 예외로 전파한다.
        ct.ThrowIfCancellationRequested();
        if (!align.Success)
            return StepResult.Fail(align.Message);

        // ── 카메라 중심 → 레이저 측정 중심 횡이동 ─────────────────────
        // Phase B는 평탄영역을 카메라 중심에 맞추므로, 레이저 3점 중심이 그 지점 위에
        // 오도록 장착 오프셋만큼 이동한 뒤 측정한다. 이동량은 시퀀스 페이지 파라미터
        // (SequenceContext.CameraToLaserShiftYmm, 기본 −75mm)로 설정 — 장착 위치 종속이라 DB 영속.
        var shiftY = context.CameraToLaserShiftYmm;
        if (Math.Abs(shiftY) > MaxCameraToLaserShiftMm)
            return FailStage(
                $"카메라→레이저 중심 보정 이동량 {shiftY:+0.#;-0.#}mm 가 한계 ±{MaxCameraToLaserShiftMm:0}mm 초과 — " +
                "시퀀스 페이지 ④ '레이저중심 Y' 파라미터를 확인하세요.");
        _logger.LogInformation("④ 레이저 중심 보정 횡이동: 툴 Y {Shift}mm", shiftY);
        context.Progress?.Invoke($"레이저 중심 보정 횡이동: 툴 Y {shiftY:+0.#;-0.#}mm");
        var shiftAnchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var shiftOffset = new[] { 0.0, shiftY, 0.0, 0.0, 0.0, 0.0 };
        var shiftRc = await _cobot.Rpc.MoveByToolOffsetAsync(shiftAnchor, user: 0, shiftOffset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (shiftRc != 0)
            return FailStage($"레이저 중심 보정 이동 실패 (rc={shiftRc}){FairinoErrorCodes.Suffix(shiftRc)}.");
        await Task.Delay(300, ct);

        // ── Phase C: 레이저 3점 측정 → 헤드 틸트(회전) 보정 ────────────
        _logger.LogInformation("④ Phase C: 레이저 3점 평면 측정 시작");
        _monitor.UpdateStage(FlatDetectionStage.LaserMeasuring, "레이저 3점 측정");

        // 보정량은 실측 응답(J)으로 푼다 — LaserTiltCorrector(/laser '보정 적용'과 공용).
        var level = await _tilt.LevelAsync(new TiltLevelOptions
        {
            Tool = context.Tool,
            Velocity = Math.Min(context.Velocity, 10),
            ThresholdDeg = TiltThresholdDeg,
            MaxCorrections = MaxTiltCorrections,
            MaxCorrectionDeg = MaxTiltCorrectionDeg,
        },
        context.Progress,
        (iter, pose) =>
        {
            _monitor.SetLaser(pose.Rx, pose.Ry, pose.Z);
            if (iter > 0) _monitor.UpdateStage(FlatDetectionStage.LaserMeasuring, $"틸트 보정 {iter}회차");
        }, ct);

        if (!level.Success)
            return FailStage(level.Message);

        var final = level.FinalPose!;
        _monitor.UpdateStage(FlatDetectionStage.Done, $"틸트 보정 {level.Corrections}회");
        return StepResult.Ok(
            $"평탄면 정렬 완료 (rx={final.Rx:0.###}°, ry={final.Ry:0.###}°, " +
            $"z={final.Z:0.#}mm, 틸트 보정={level.Corrections}회, σ={align.SigmaMm:0.##}mm).");
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────

    /// <summary>평탄 셀 검출 이후 단계의 실패 — 카메라 페이지 검출 스냅샷에도 실패로 표시한다.</summary>
    private StepResult FailStage(string message)
    {
        _monitor.UpdateStage(FlatDetectionStage.Failed, message);
        return StepResult.Fail(message);
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
