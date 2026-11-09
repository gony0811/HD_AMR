using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑦⁺/⑪⁺ Bead 센터링 — ⑦⑪이 잰 비드 위치오차 d(진행축과 수직 = cross 축, mm)만큼
/// cross 툴축(진행축이 영상 가로면 ImageY, 세로면 ImageX 매핑)으로 <b>1회</b> 이동해
/// 비드 라인을 depth 영상 중심에 맞춘 뒤, <b>검증까지 끝나면</b> 검사 카메라 설치 오프셋
/// (<see cref="WeldSequenceSupport.InspectCamOffsetXKey"/>)만큼 2D 개루프 시프트해 타깃을
/// 검사 카메라 아래로 옮긴다. 시프트를 센터링 수식에 합성하면 오프셋이 클 때 타깃이 depth 시야를
/// 벗어나 재측정 검증이 불가능하므로, ④의 레이저 중심 시프트와 동일하게 "검증 후 시프트"로 분리한다.
/// 시프트 상태는 <see cref="WeldSequenceSupport.InspectShiftedBagKey"/> 로 ⑧에 전달되어 역보정된다.
///
/// ⑫ 각도 산출과의 정합(비대칭 정책의 이유):
///   - <b>peakId=1</b>: 이동 후(시프트 전) 재측정해 M1 을 잔차(≈0)로 갱신한다. 센터링 이동으로 FOV
///     기준선이 d1 만큼 옮겨지므로, 갱신하지 않으면 ⑫의 (d2−d1)/pitch 에서 d1 이 이중 반영된다.
///     단 ⑦에서 사람이 비드선을 그었으면 재측정(팝업 재등장)하지 않고 그 선을 이동량만큼 영상에서 옮겨
///     잔차(d1 − 이동량)로 M1 을 갱신한다 — 이 경우 이동 방향 검증은 생략된다.
///   - <b>peakId=2</b>: 이동만 하고 재측정하지 않는다. CapturePeak 재측정은 M2 를 잔차≈0 으로 덮고
///     각도를 자동 재계산해 θ≈0 으로 오염시키기 때문 — ⑫(order 1200)는 이동 전 저장된 M2 를 쓴다.
///   - 검사캠 시프트는 측정이 모두 끝난 뒤(⑦⁺는 재측정 후, ⑧이 역보정) 수행되므로 각도에 개입하지 않는다.
/// </summary>
public class BeadCenteringStep : ISequenceStep
{
    private readonly int _peakId;
    private readonly WeldTrackingService _weld;
    private readonly CameraService _camera;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly BeadManualAssist _manual;
    private readonly ILogger<BeadCenteringStep> _logger;

    /// <summary>1회 이동 클램프(mm). 매핑 오설정·측정 폭주 가드 — ⑥과 동일 관례.</summary>
    private const double MaxCenteringMoveMm = 100.0;

    /// <summary>이 값 미만이면 이동을 생략한다(mm).</summary>
    private const double SkipMoveMm = 0.5;

    /// <summary>모션 후 안정화 대기(ms).</summary>
    private const int SettleMs = 500;

    /// <summary>센터링 후 남아도 되는 cross 축 잔차(mm). 넘어도 검사는 계속하고 경고만 남긴다 — 잔차가 그대로 점1/점2 의
    /// cross 오차가 되어 작업물 X축이 돌아갈 수 있으므로(예: 30mm/360mm ≈ 4.8°) 로그·결과 메시지로 알린다.</summary>
    public const string ResidualMaxKey = "Sequence.Bead.CenterResidualMaxMm";
    public const double DefaultResidualMaxMm = 10.0;

    /// <param name="peakId">1 또는 2. DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public BeadCenteringStep(
        int peakId,
        WeldTrackingService weld,
        CameraService camera,
        CobotService cobot,
        ParameterService param,
        BeadManualAssist manual,
        ILogger<BeadCenteringStep> logger)
    {
        _peakId = peakId;
        _weld = weld;
        _camera = camera;
        _cobot = cobot;
        _param = param;
        _manual = manual;
        _logger = logger;
    }

    public string Key => $"bead{_peakId}Center";
    public string DisplayName => $"Bead{_peakId} 센터링";
    public int DefaultOrder => _peakId == 1 ? 750 : 1150;

    public StepValidation Validate(SequenceContext context)
    {
        // 재측정(peakId=1)에만 검출기가 필요하다.
        var common = WeldSequenceSupport.ValidateCommon(_cobot, _camera, _weld, requireDetector: _peakId == 1);
        if (!common.IsValid) return common;

        if (!context.Bag.ContainsKey(WeldSequenceSupport.BeadDMmBagKey(_peakId)))
            return StepValidation.Fail(
                $"Bead{_peakId} 측정값 없음 — 'Bead{_peakId} 찾기' 단계를 먼저 실행하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (context.Bag[WeldSequenceSupport.BeadDMmBagKey(_peakId)] is not double initialDMm)
            return StepResult.Fail($"Bead{_peakId} 측정값을 읽을 수 없습니다 — 앞 단계를 다시 실행하세요.");

        // 이미 검사캠 시프트가 적용된 자리에서 다시 돌면 시프트·센터링 이동이 이중으로 들어가고(⑦⁺는 앵커도 시프트된
        // 포즈로 덮인다) 점1/점2 가 어긋난다 — 세미오토 재실행을 막는다.
        if (context.Bag.TryGetValue(WeldSequenceSupport.InspectShiftedBagKey, out var shiftedFlag) && shiftedFlag is true)
            return StepResult.Fail(
                $"검사캠 시프트가 이미 적용된 상태입니다 — Bead{_peakId} 센터링을 다시 실행하면 이동이 이중으로 들어갑니다. " +
                "'상태 초기화' 후 시퀀스를 처음부터(또는 Peak/Bead 찾기부터) 다시 실행하세요.");

        // 이전 실행의 잔차 보정이 이번 점 기록에 섞이지 않게 먼저 지운다.
        context.Bag.Remove(WObjPointStep.PointCrossCorrectionBagKey(_peakId));

        var (progressAxis, _) = await WeldSequenceSupport.ApplyProgressAxisAsync(_weld, context, _cobot, _param, ct);
        var axisKey = WeldSequenceSupport.CrossToolAxisKey(progressAxis);
        var (toolAxis, axisFromParam) = await WeldSequenceSupport.GetCrossToolAxisAsync(_param, progressAxis);

        // ④와 동일 규약: d = 타깃 − 센터(양수 = cross 축 + 방향)를 부호 반전 없이 그대로 싣는다.
        // 검사캠 오프셋은 여기 합성하지 않는다 — depth 시야 중심에서 검증을 끝낸 뒤 별도 시프트.
        var moveMm = initialDMm;

        if (Math.Abs(moveMm) < SkipMoveMm)
        {
            // 이동하지 않았으므로 측정 d 가 곧 잔차다.
            var skipCorr = RecordPointCorrection(context, toolAxis, initialDMm, "센터링 생략 — 측정 d");
            var (shiftOk, shiftNote) = await ShiftToInspectCamAsync(context, ct);
            return shiftOk
                ? StepResult.Ok($"Bead{_peakId} 이미 센터 근방 (d={initialDMm:+0.0;-0.0}mm) — 이동 생략.{shiftNote}{skipCorr}")
                : StepResult.Fail(shiftNote);
        }

        var residualMax = await _param.GetDoubleAsync(ResidualMaxKey) ?? DefaultResidualMaxMm;
        string? residualWarn = null;
        if (residualMax > 0 && Math.Abs(moveMm) - MaxCenteringMoveMm > residualMax)
            residualWarn = $"이격 {initialDMm:+0.0;-0.0}mm 가 1회 이동 한계 ±{MaxCenteringMoveMm:0}mm 를 넘어 " +
                           $"잔차 약 {Math.Abs(moveMm) - MaxCenteringMoveMm:0.0}mm 가 남음(허용 {residualMax:0.#}mm)";

        var clamped = Math.Clamp(moveMm, -MaxCenteringMoveMm, MaxCenteringMoveMm);
        if (Math.Abs(clamped - moveMm) > 1e-6)
            _logger.LogWarning(
                "Bead{Id} 센터링 이동량 {Raw:0.0}mm 가 한계 ±{Max:0}mm 로 클램프됨.",
                _peakId, moveMm, MaxCenteringMoveMm);
        if (!axisFromParam)
            _logger.LogWarning(
                "'{Key}' 매핑 파라미터 없음/범위 밖 — 기본값 사용. 카메라 페이지에서 매핑을 설정·저장하세요.",
                axisKey);

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, toolAxis, clamped);

        _logger.LogInformation(
            "Bead{Id} 센터링: 진행축={Prog}, cross 툴{Axis} {Off:+0.0;-0.0}mm (측정 d={Meas:+0.0;-0.0}mm)",
            _peakId, progressAxis, FlatSurfaceCenteringService.AxisName(toolAxis), clamped, initialDMm);

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"비드 센터링 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);

        if (_peakId != 1)
        {
            // ⑫ 각도 정합을 위해 M2 는 갱신하지 않는다. 작업물 좌표계 점2 잔차 보정용으로만 슬롯을 건드리지 않고 재측정한다.
            var corr2 = await MeasurePoint2ResidualAsync(context, toolAxis, initialDMm, clamped, ct);
            var (ok2, note2) = await ShiftToInspectCamAsync(context, ct);
            return ok2
                ? StepResult.Ok(
                    $"Bead2 센터링 완료 — 툴{FlatSurfaceCenteringService.AxisName(toolAxis)} {clamped:+0.0;-0.0}mm 이동 " +
                    $"(각도 산출 정합을 위해 M2 는 갱신하지 않음).{corr2}{note2}{ResidualNote(residualWarn)}")
                : StepResult.Fail(note2);
        }

        // ── peakId=1, ⑦에서 사람이 비드선을 그은 경우: 재측정 팝업을 다시 띄우지 않는다 ──
        // 같은 자리·같은 표면이라 자동 재검출도 실패할 가능성이 커 팝업이 연달아 뜬다. 대신 그은 선을
        // 센터링 이동량만큼 영상에서 옮겨 잔차를 계산한다(잔차 = d1 − 이동량). 이동 방향 검증(이격 증가 판정)은
        // 생략되므로 cross 축 매핑이 현장에서 검증돼 있어야 한다.
        if (IsManualMeasurement(_weld.M1, _weld.DMm, initialDMm))
        {
            var shifted = await _weld.ShiftMeasurementByMoveAsync(_peakId, clamped, ct);
            if (shifted is not null)
            {
                var residualManual = _weld.DMm(shifted);
                if (residualMax > 0 && Math.Abs(residualManual) > residualMax)
                    residualWarn = $"잔차 {residualManual:+0.0;-0.0}mm 가 허용 ±{residualMax:0.#}mm 초과 — 그대로 진행";
                context.Bag[WeldSequenceSupport.BeadDMmBagKey(_peakId)] = residualManual;
                var corrM = RecordPointCorrection(context, toolAxis, residualManual, "수동 비드선 이동 반영 잔차");
                _logger.LogInformation(
                    "Bead{Id} 센터링: 수동 비드선 기준 — 재측정 생략, 잔차 d={Res:+0.0;-0.0}mm ({Px:+0.0;-0.0}px, 이동 전 {D1:+0.0;-0.0}mm)",
                    _peakId, residualManual, shifted.DPixel, initialDMm);

                var (shiftOkM, shiftNoteM) = await ShiftToInspectCamAsync(context, ct);
                if (!shiftOkM)
                    return StepResult.Fail(shiftNoteM);
                return StepResult.Ok(
                    $"Bead{_peakId} 센터링 완료 — {clamped:+0.0;-0.0}mm 이동, 수동 비드선을 이동량만큼 옮겨 " +
                    $"잔차 d={residualManual:+0.0;-0.0}mm (이동 전 {initialDMm:+0.0;-0.0}mm), M1 갱신(재측정 생략).{corrM}{shiftNoteM}{ResidualNote(residualWarn)}");
            }
            _logger.LogWarning("Bead{Id} 센터링: 수동 측정 이동 반영 불가(측정/스케일 없음) — 재측정으로 진행", _peakId);
        }

        // ── peakId=1: 재측정으로 M1 을 잔차로 갱신 (⑫ 정합) + 매핑 검증 ──
        var (peakRoi, _) = await WeldSequenceSupport.GetPeakRoiAsync(_param, _camera);
        var (beadRoi, _) = await WeldSequenceSupport.GetBeadRoiAsync(_param, _camera);
        if (peakRoi is null || beadRoi is null)
            return StepResult.Fail("재측정용 ROI 를 만들 수 없습니다.");

        await WeldSequenceSupport.ApplyDlModelAsync(_param, _weld);   // 저장된 DL 모델 선택 적용
        if (WeldSequenceSupport.MissingModel(_weld) is { } noModel)
            return StepResult.Fail($"Bead{_peakId} 재측정 중단 — {noModel}");
        var (m2, detect2) = await _weld.CapturePeakAsync(_peakId, peakRoi, beadRoi, ct);
        string? autoFail = detect2 is null || !detect2.Success || m2 is null
            ? $"이동 후 Bead{_peakId} 재검출 실패 — '{axisKey}' 매핑" +
              $"(현재 툴{FlatSurfaceCenteringService.AxisName(toolAxis)}) 또는 ROI 를 확인하세요." +
              (detect2 is null ? "" : $" ({detect2.Message})")
            // ⑦과 같은 게이트 — 직선피팅 실패(중앙값 폴백) d 로 방향 검증·M1 갱신을 하면 잘못 통과/실패한다.
            : !detect2.LineFitOk
                ? $"이동 후 Bead{_peakId} 직선피팅 실패(중앙값 폴백) — 잔차를 신뢰할 수 없습니다. coverage={detect2.Confidence:P0}"
                : null;

        // 재측정도 비드 찾기와 같은 수동 입력 조건을 따른다(수동으로 찾은 뒤 재검출만 실패해 멈추지 않게).
        var assist = await _manual.RunAsync(_peakId, $"{DisplayName}(재측정)", m2, detect2, autoFail is null, autoFail,
            peakRoi, beadRoi, context, ct);
        if (assist.Kind == BeadAssistKind.Manual) m2 = assist.Measurement;
        else if (!assist.UseAutoPath || autoFail is not null)
            return StepResult.Fail((autoFail ?? $"이동 후 Bead{_peakId} 재측정 실패") + assist.Note);
        if (m2 is null)
            return StepResult.Fail($"이동 후 Bead{_peakId} 재측정 결과가 없습니다.{assist.Note}");

        var residualMm = _weld.DMm(m2);

        if (Math.Abs(residualMm) >= Math.Abs(initialDMm))
            return StepResult.Fail(
                $"비드 이격이 증가했습니다 (d={initialDMm:+0.0;-0.0} → {residualMm:+0.0;-0.0}mm) — " +
                $"'{axisKey}' 를 현재 툴{FlatSurfaceCenteringService.AxisName(toolAxis)} 의 " +
                "반대 부호 축으로 설정해 보세요 (카메라 페이지).");

        if (residualMax > 0 && Math.Abs(residualMm) > residualMax)
            residualWarn = $"잔차 {residualMm:+0.0;-0.0}mm 가 허용 ±{residualMax:0.#}mm 초과 — 그대로 진행";

        context.Bag[WeldSequenceSupport.BeadDMmBagKey(_peakId)] = residualMm;
        var corr1 = RecordPointCorrection(context, toolAxis, residualMm, "재측정 잔차");

        // 측정·검증이 모두 끝났으므로 검사캠 아래로 시프트한다.
        var (shiftedOk, shiftedNote) = await ShiftToInspectCamAsync(context, ct);
        if (!shiftedOk)
            return StepResult.Fail(shiftedNote);

        return StepResult.Ok(
            $"Bead{_peakId} 센터링 완료 — {clamped:+0.0;-0.0}mm 이동, " +
            $"잔차 d={residualMm:+0.0;-0.0}mm (이동 전 {initialDMm:+0.0;-0.0}mm), M1 갱신.{corr1}{assist.Note}{shiftedNote}{ResidualNote(residualWarn)}");
    }

    /// <summary>
    /// 남은 cross 잔차를 작업물 좌표계 점(⑦⁺→점1, ⑪⁺→점2) 보정 오프셋으로 Bag 에 남긴다 — <see cref="WObjPointStep"/> 이
    /// 기록 포즈에 더한다. 보정 상한을 넘으면 남기지 않고 경고. 결과 메시지에 덧붙일 문구를 돌려준다.
    /// </summary>
    private string RecordPointCorrection(SequenceContext context, ToolAxisDir crossAxis, double residualMm, string source)
    {
        var offset = WObjPointStep.CrossCorrectionOffset(crossAxis, residualMm);
        if (offset is null)
        {
            _logger.LogWarning(
                "Bead{Id} 센터링: 잔차 {Res:+0.0;-0.0}mm ({Src}) 가 보정 상한 ±{Max:0}mm 밖 — 작업물 좌표계 점 잔차 보정 안 함",
                _peakId, residualMm, source, WObjPointStep.MaxResidualCorrectionMm);
            return $" ⚠ 잔차 {residualMm:+0.0;-0.0}mm 가 보정 상한 ±{WObjPointStep.MaxResidualCorrectionMm:0}mm 밖 — 점{_peakId} 보정 안 함.";
        }
        context.Bag[WObjPointStep.PointCrossCorrectionBagKey(_peakId)] = offset;
        _logger.LogInformation(
            "Bead{Id} 센터링: 작업물 좌표계 점 잔차 보정 {Res:+0.0;-0.0}mm ({Src}) → 툴{Axis}",
            _peakId, residualMm, source, FlatSurfaceCenteringService.AxisName(crossAxis));
        return $" 점{_peakId} 잔차 보정 {residualMm:+0.0;-0.0}mm({source}).";
    }

    /// <summary>
    /// ⑪⁺ 이동 후 Bead2 를 M2 를 바꾸지 않고 재측정해 점2 잔차 보정을 남긴다. 검출기 없음·검출 실패·직선피팅 실패면
    /// 보정 없이(종전과 같게) 진행하고 경고만 남긴다 — 점2 는 종전에도 검증 없이 기록됐으므로 실패시키지 않는다.
    /// </summary>
    private async Task<string> MeasurePoint2ResidualAsync(
        SequenceContext context, ToolAxisDir crossAxis, double initialDMm, double movedMm, CancellationToken ct)
    {
        if (await _param.GetBoolAsync(WObjPointStep.ResidualCorrectionKey) == false)
            return "";

        string Skip(string why)
        {
            _logger.LogWarning("Bead2 센터링: 점2 잔차 재측정 생략 — {Why} (점2 는 센터링 위치 그대로 기록)", why);
            return $" ⚠ 점2 잔차 재측정 생략({why}).";
        }

        try
        {
            var (peakRoi, _) = await WeldSequenceSupport.GetPeakRoiAsync(_param, _camera);
            var (beadRoi, _) = await WeldSequenceSupport.GetBeadRoiAsync(_param, _camera);
            if (peakRoi is null || beadRoi is null) return Skip("ROI 없음");
            await WeldSequenceSupport.ApplyDlModelAsync(_param, _weld);
            if (WeldSequenceSupport.MissingModel(_weld) is { } noModel) return Skip(noModel);

            var (m, detect) = await _weld.MeasureWithoutStoringAsync(_peakId, peakRoi, beadRoi, ct);
            if (m is null || detect is not { Success: true })
                return Skip($"재검출 실패{(detect is null ? "" : $": {detect.Message}")}");
            if (!detect.LineFitOk)
                return Skip($"직선피팅 실패(coverage={detect.Confidence:P0})");

            var residual = _weld.DMm(m);
            _logger.LogInformation(
                "Bead2 센터링: 점2 잔차 재측정 d={Res:+0.0;-0.0}mm (이동 전 {D:+0.0;-0.0}mm, 이동 {Mv:+0.0;-0.0}mm, M2·각도 미변경)",
                residual, initialDMm, movedMm);
            if (Math.Abs(movedMm) >= 2.0 && Math.Abs(residual) >= Math.Abs(initialDMm))
                return Skip($"재측정 d={residual:+0.0;-0.0}mm 가 이동 전 {initialDMm:+0.0;-0.0}mm 보다 큼 — 오검출 의심");
            return RecordPointCorrection(context, crossAxis, residual, "점2 재측정 잔차");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bead2 센터링: 점2 잔차 재측정 오류");
            return Skip($"오류: {ex.Message}");
        }
    }

    /// <summary>잔차 경고가 있으면 로그를 남기고 결과 메시지에 덧붙일 문구를 돌려준다.</summary>
    private string ResidualNote(string? warn)
    {
        if (warn is null) return "";
        _logger.LogWarning("Bead{Id} 센터링: {Warn} — 작업물 X축이 그만큼 돌아갈 수 있습니다.", _peakId, warn);
        return $" ⚠ {warn}";
    }

    /// <summary>
    /// ⑦이 사람이 그은 비드선으로 측정했는지. 측정 슬롯이 이번 ⑦ 결과(<paramref name="initialDMm"/>)와 일치할 때만
    /// 참 — 다른 실행에서 남은 슬롯을 잘못 쓰지 않게 한다.
    /// </summary>
    internal static bool IsManualMeasurement(PeakMeasurement? m, Func<PeakMeasurement, double> toMm, double initialDMm)
        => m is { Manual: true } && Math.Abs(toMm(m) - initialDMm) < 0.05;

    /// <summary>
    /// 검사 카메라 설치 오프셋만큼 2D 개루프 시프트 — 카메라가 영상 (−X, −Y) 방향으로 움직여
    /// 타깃이 화면상 (+X, +Y) 위치(= 검사 카메라 시야 중심)에 온다. 오프셋이 0 이면 아무것도 안 한다.
    /// 성공 시 <see cref="WeldSequenceSupport.InspectShiftedBagKey"/> 를 세워 ⑧이 역보정하게 한다.
    /// peakId=1 이면 시프트 <b>전</b> 포즈(= 센터링 완료 지점)를 앵커로 저장한다 — ⑧이 이 포즈 기준으로
    /// pitch 이동해 시프트·작업물 좌표계 교시용 이동을 자동 원복한다.
    /// </summary>
    private async Task<(bool Ok, string Note)> ShiftToInspectCamAsync(SequenceContext context, CancellationToken ct)
    {
        if (_peakId == 1)
            context.Bag[WeldSequenceSupport.Bead1CenteredPoseBagKey] =
                await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);

        var (offX, offY) = await WeldSequenceSupport.GetInspectCamOffsetAsync(_param);
        if (Math.Abs(offX) < SkipMoveMm && Math.Abs(offY) < SkipMoveMm)
            return (true, "");

        var (axX, _) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
        var (axY, _) = await WeldSequenceSupport.GetImageYAxisAsync(_param);

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var offset = WeldSequenceSupport.ImageShiftToToolOffset(axX, axY, -offX, -offY);

        _logger.LogInformation(
            "[Bead{Id} 센터링] 검사캠 시프트: 영상 ({X:+0.0;-0.0}, {Y:+0.0;-0.0})mm 역방향 이동 → 툴 오프셋 [{Ox:0.#}, {Oy:0.#}, {Oz:0.#}]",
            _peakId, offX, offY, offset[0], offset[1], offset[2]);

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return (false, $"검사캠 시프트 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);
        context.Bag[WeldSequenceSupport.InspectShiftedBagKey] = true;

        return (true, $" 검사캠 시프트 적용(영상 {offX:+0.0;-0.0}/{offY:+0.0;-0.0}mm).");
    }
}
