using HD.AMR.App.Models;
using HD.AMR.App.Service.Motion;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ④ 평탄면 센터링 — 레이저 변위센서 3점 측량을 위해 가장 평평한 면을 센서 중앙에 위치시킨다.
///
/// 3-Phase 알고리즘:
///   A) 뎁스 카메라 깊이 프레임을 그리드로 분할, depth σ 최소 셀(= 최평탄 영역) 탐색.
///   B) 평탄 셀 중심과 현재 센서 중심의 오프셋을 mm로 환산, 코봇 횡이동(툴 프레임 오프셋 —
///      이미지 평면과 평행, 대상면 거리 유지. FlatSurfaceCenteringService 공용 루틴).
///      이동 후 레이저 측정 중심 보정 횡이동(툴 −Y 75mm — 레이저 3점 중심이 카메라보다 좌측(툴 +Y) 75mm 장착).
///      셀은 σ 최소 하나가 아니라 후보 랭킹(잔차·σ 상한, σ + 거리 패널티)으로 고르고, 이동 전에 후보마다
///      <b>최종 위치</b>(셀 이동 ⊕ 레이저 보정 ⊕ 틸트 여유)의 도달성을 판정해 첫 도달 가능 셀로 간다 —
///      최외곽 라인에서 σ 최소 셀이 팔 밖에 있어 도달 불가 알람이 나던 문제(2026-10-26 현장).
///      이동은 두 단계 그대로(카메라 페이지 '카메라 이동 → 레이저 이동'과 대응), 관절 이동(MoveJ)으로 한다.
///   C) 레이저 변위센서 3점 측정으로 평면 틸트(rx, ry) 검증. 임계값 초과 시 툴 헤드를 회전 보정(위치 고정) 후 재검증.
///      보정량은 고정 부호 규약이 아니라 <b>실측 응답</b>으로 정한다 — 툴 Rx/Ry 를 각각 소량 회전시켜
///      측정 틸트가 어떻게 변하는지(2×2 응답 행렬 J)를 잰 뒤 u = −J⁻¹·tilt 를 적용한다.
///      (2026-10-07/08 현장 로그: 종전 [+Rx, −Ry] 규약에서 툴 Rx +a → 측정 rx ≈ +a 로 오차가 매회 누적,
///      툴 Rx 가 측정 ry 도 크게 바꾸는 축 결합까지 있어 고정 부호로는 수렴하지 않았다.)
///
/// 레이저 풋프린트(2026-11-10): 셀은 약 46×27mm 인데 레이저 3점은 한 변 38mm 삼각형이라 셀이 평평해도 스팟 하나가
/// 이웃 비드·코로게이션에 걸렸다(한 점 0.58mm ≈ 1°). 그래서 후보마다 세 스팟이 떨어질 자리를 깊이로 평가해
/// 위치를 고르고(<see cref="LaserFootprintEvaluator"/>), Phase C 가 측정면 문제로 실패하면 시작 자세로 돌아가
/// 그 자리를 제외하고 다른 자리로 재시도한다(<see cref="LaserFootprintOptions.MaxSiteRetries"/>회).
///
/// ②가 저장한 최초 접근 기준점을 유지한다. ② 없이 단독 실행하면 최초 실행 위치만 저장한다.
/// ④⁺(레이저 WD)는 이 기준점을 보정 후 면 평행 평면으로 투영해 복귀한다(자세·WD 유지).
/// </summary>
public class FlatSurfaceAlignStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly LaserDisplacementSensorService _laser;
    private readonly LaserTiltCorrector _tilt;
    private readonly ParameterService _param;
    private readonly PostureLimitsService _limits;
    private readonly FlatDetectionMonitor _monitor;
    private readonly ILogger<FlatSurfaceAlignStep> _logger;

    /// <summary>false 면 Phase C(레이저 틸트 수평 얼라인)를 하지 않는다. 검사 시작점 재탐색용.</summary>
    private readonly bool _levelSurface;

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

    /// <param name="levelSurface">true(④)면 탐색 뒤 틸트 수평 얼라인. false(검사 시작 재탐색)면 자리만 맞추고 수평 얼라인은 하지 않는다.
    /// <c>ActivatorUtilities.CreateInstance</c> 가 명시 인자를 앞에서부터 매칭하므로 첫 인자다.</param>
    public FlatSurfaceAlignStep(
        bool levelSurface,
        CobotService cobot, CameraService camera, FlatSurfaceCenteringService centering,
        LaserDisplacementSensorService laser, LaserTiltCorrector tilt, ParameterService param,
        PostureLimitsService limits, FlatDetectionMonitor monitor, ILogger<FlatSurfaceAlignStep> logger)
    {
        _tilt = tilt;
        _limits = limits;
        _cobot = cobot;
        _camera = camera;
        _centering = centering;
        _laser = laser;
        _param = param;
        _monitor = monitor;
        _logger = logger;
        _levelSurface = levelSurface;
    }

    /// <summary>검사 시작점 재탐색 — 평탄 자리만, 수평 얼라인 없음.</summary>
    public const string TaskStartKey = "flatSurfaceSearchTaskStart";

    public string Key => _levelSurface ? "flatSurfaceAlign" : TaskStartKey;
    public string DisplayName => _levelSurface ? "평탄면 센터링 (레이저 정렬)" : "검사 시작 평탄면 탐색";
    public int DefaultOrder => _levelSurface ? 400 : 1180;

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
        // ②의 최초 접근점을 고정한다. 세미오토로 ④만 시작한 경우에도 첫 실행에서만 저장해
        // 여러 번 탐색·정렬하더라도 마지막 시작점으로 기준이 밀리지 않게 한다.
        if (!context.Bag.ContainsKey(WeldSequenceSupport.InspectAnchorPoseBagKey))
            InspectionAlignmentAnchor.CaptureIfMissing(context,
                await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct));

        // ── Phase A+B: 평탄영역 탐색 + 코봇 횡이동 (공용 루틴) ─────────
        var (roiX, roiY, roiW, roiH, roiSrc) = await GetDepthRoiAsync();
        _logger.LogInformation("[평탄면 센터링 (레이저 정렬)] Phase A/B: 평탄영역 탐색+횡이동 시작 (grid={Grid}×{Grid}, ROI={Roi})",
            GridSize, GridSize, roiSrc);

        context.Progress?.Invoke($"평탄영역 탐색 시작 (grid={GridSize}×{GridSize}, ROI={roiSrc})");

        // 레이저 보정량은 후보 도달성 판정(최종 위치)에도 쓰므로 이동 전에 검사한다.
        var shiftY = context.CameraToLaserShiftYmm;
        if (Math.Abs(shiftY) > MaxCameraToLaserShiftMm)
            return FailStage(
                $"카메라→레이저 중심 보정 이동량 {shiftY:+0.#;-0.#}mm 가 한계 ±{MaxCameraToLaserShiftMm:0}mm 초과 — " +
                "시퀀스 페이지 '레이저중심 Y' 파라미터를 확인하세요.");

        var (axisX, axisY) = await GetAxisMapAsync();
        var jointVelScale = await FlatMoveSettings.LoadJointVelocityScaleAsync(_param);
        var candidates = await FlatCandidateOptions.LoadAsync(_param);
        var footprint = await LaserFootprintOptions.LoadAsync(_param);
        var postureLimits = await _limits.GetAsync();
        var tiltMargin = await _param.GetDoubleAsync(FlatReachOptions.TiltMarginKey) ?? FlatReachOptions.DefaultTiltMarginDeg;

        // 재시도 기준 자세 — Phase C 가 실패하면 여기로 돌아와 같은 화면에서 실패 자리를 빼고 다시 고른다.
        var startPose = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var excluded = new List<(double U, double V)>();
        int maxAttempts = 1 + (footprint.Enabled ? footprint.MaxSiteRetries : 0);
        var failures = new List<string>();

        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                context.Progress?.Invoke($"다른 자리로 재시도 {attempt}/{maxAttempts - 1}: 시작 자세로 복귀");
                _logger.LogInformation("[평탄면 센터링 (레이저 정렬)] 재시도 {Attempt}: 시작 자세 복귀, 제외 자리 {Count}곳",
                    attempt, excluded.Count);
                var back = await _centering.MoveToolOffsetAsync(startPose, new double[6], context.Tool, context.Velocity,
                    joint: true, ct, jointVelScale);
                if (!back.Ok)
                    return FailStage($"재시도용 시작 자세 복귀 실패 {back.Describe()}. 이전 실패: {failures[^1]}");
                await Task.Delay(800, ct);   // 진동·깊이 프레임 갱신 대기
            }

            var align = await _centering.RunAsync(new FlatCenterAlignOptions
            {
                RoiX = roiX, RoiY = roiY, RoiW = roiW, RoiH = roiH,
                GridSize = GridSize,
                SamplesPerDetect = 10,
                // 풋프린트 판정은 셀 중심이 아니라 mm 단위 자리를 고르므로 정규화 데드밴드(0.02 ≈ 수~십수 mm)로
                // 이동을 생략하면 그 자리를 놓친다 — 그때는 mm 기준(1.5mm)만 쓴다.
                DeadbandUv = footprint.Enabled ? 0.0 : 0.02,
                ToleranceMm = footprint.Enabled ? 1.5 : 0.0,
                MaxIterations = 1, VerifyAfterMove = false,    // 기존과 동일: 1회 개루프 이동
                MaxLateralMoveMm = MaxLateralMoveMm,
                ImageXAxis = axisX, ImageYAxis = axisY,
                Tool = context.Tool, Velocity = context.Velocity,
                Source = attempt == 0 ? "시퀀스" : $"시퀀스 재시도 {attempt}",
                Candidates = candidates,
                Reach = new FlatReachOptions
                {
                    Limits = postureLimits,
                    ImageXAxis = axisX, ImageYAxis = axisY,
                    CameraToLaserShiftYmm = shiftY,
                    // 수평 얼라인을 하지 않으면 틸트 여유분까지 도달해야 할 이유가 없다.
                    TiltMarginDeg = _levelSurface ? tiltMargin : 0,
                    Tool = context.Tool,
                },
                Footprint = new FlatFootprintContext
                {
                    Options = footprint,
                    ImageXAxis = axisX, ImageYAxis = axisY,
                    Excluded = excluded.ToArray(),
                },
                UseJointMove = true,
                JointVelocityScale = jointVelScale,
            }, progress: context.Progress, ct);

            // 공용 루틴은 취소를 삼키고 실패 결과로 반환 — 스텝은 기존처럼 취소 예외로 전파한다.
            ct.ThrowIfCancellationRequested();
            if (!align.Success)
                return StepResult.Fail(failures.Count == 0
                    ? align.Message
                    : $"{align.Message} (재시도 {attempt}회차 — 이전 실패: {string.Join(" / ", failures)})");

            // 센터링 직후 화면 — 평탄 셀이 ROI 중심에 와 있고, 다음 이동으로 레이저가 바로 그 자리를 측정한다.
            // 시퀀스 모니터가 ROI 중심에 예상 측정 범위를 그려 비평탄 실패 원인(측정 자리)을 확인하게 한다.
            _monitor.SetCenteredFrame(_camera.LatestDepth);

            // ── 카메라 중심 → 레이저 측정 중심 횡이동 ─────────────────────
            // Phase B는 평탄영역을 카메라 중심에 맞추므로, 레이저 3점 중심이 그 지점 위에
            // 오도록 장착 오프셋만큼 이동한 뒤 측정한다. 이동량은 시퀀스 페이지 파라미터
            // (SequenceContext.CameraToLaserShiftYmm, 기본 −75mm)로 설정 — 장착 위치 종속이라 DB 영속.
            _logger.LogInformation("[평탄면 센터링 (레이저 정렬)] 레이저 중심 보정 횡이동: 툴 Y {Shift}mm", shiftY);
            context.Progress?.Invoke($"레이저 중심 보정 횡이동: 툴 Y {shiftY:+0.#;-0.#}mm");
            var shiftAnchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            var shiftOffset = new[] { 0.0, shiftY, 0.0, 0.0, 0.0, 0.0 };
            var shift = await _centering.MoveToolOffsetAsync(shiftAnchor, shiftOffset, context.Tool, context.Velocity,
                joint: true, ct, jointVelScale);
            if (!shift.Ok)
                return FailStage($"레이저 중심 보정 이동 실패 {shift.Describe()}.");
            await Task.Delay(300, ct);

            // 검사 시작점 재탐색은 자리만 맞춘다. 틸트 수평 얼라인(Phase C)은 ④에서 이미 했고,
            // 여기서 다시 돌리면 교시해 둔 자세가 돌아간다.
            if (!_levelSurface)
            {
                _monitor.UpdateStage(FlatDetectionStage.Done,
                    "수평 얼라인 생략" + (attempt > 0 ? $" (자리 재시도 {attempt}회)" : ""));
                return StepResult.Ok(
                    $"평탄면 탐색 완료 — 수평 얼라인 생략 (σ={align.SigmaMm:0.##}mm" +
                    (attempt > 0 ? $", 자리 재시도={attempt}회" : "") + ").");
            }

            // ── Phase C: 레이저 3점 측정 → 헤드 틸트(회전) 보정 ────────────
            _logger.LogInformation("[평탄면 센터링 (레이저 정렬)] Phase C: 레이저 3점 평면 측정 시작");
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

            if (level.Success)
            {
                var final = level.FinalPose!;
                _monitor.UpdateStage(FlatDetectionStage.Done, $"틸트 보정 {level.Corrections}회" +
                    (attempt > 0 ? $" (자리 재시도 {attempt}회)" : ""));
                return StepResult.Ok(
                    $"평탄면 정렬 완료 (rx={final.Rx:0.###}°, ry={final.Ry:0.###}°, " +
                    $"z={final.Z:0.#}mm, 틸트 보정={level.Corrections}회, σ={align.SigmaMm:0.##}mm" +
                    (attempt > 0 ? $", 자리 재시도={attempt}회" : "") +
                    (level.Noise is { } n ? $", 레이저 {n.ShortText()}" : "") + ").");
            }

            // 측정면 문제(비평탄·발산·미달·튐·측정 실패)는 다른 자리로 재시도, 코봇 이동 실패는 그대로 중단.
            failures.Add(level.Message);
            if (level.MotionFailure || attempt + 1 >= maxAttempts || align.FlatU is not { } fu || align.FlatV is not { } fv)
                return FailStage(failures.Count == 1
                    ? level.Message
                    : $"{level.Message} (자리 {failures.Count}곳 모두 실패 — 이전: {string.Join(" / ", failures.SkipLast(1))})");

            excluded.Add((fu, fv));
            _logger.LogWarning("[평탄면 센터링 (레이저 정렬)] Phase C 실패 — 자리 (u={U:0.###}, v={V:0.###}) 제외 후 재시도: {Msg}",
                fu, fv, level.Message);
            context.Progress?.Invoke($"⚠ 레이저 틸트 실패 — 이 자리를 제외하고 다른 자리로 재시도합니다: {level.Message}");
        }
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
