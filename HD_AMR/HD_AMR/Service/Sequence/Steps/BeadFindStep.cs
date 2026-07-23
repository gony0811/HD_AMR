using HD_AMR.Models;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑦/⑪ Bead 찾기 — 비드를 초록 중심선으로 검출하고, 자홍 Peak 선과의 교점(빨간 점)에서
/// FOV 센터로부터의 위치오차 d 를 측정해 <c>M1</c>/<c>M2</c> 슬롯에 저장한다. ⑫ 각도 산출의 입력이다.
///
/// 실패 판정은 기존 게이트를 그대로 따른다(시퀀스가 새 임계값을 만들지 않는다):
///   1) DL 검출 0건 — DlWeldVisionDetector 의 conf 임계에서 걸림
///   2) coverage &lt; 15% — WeldMaskAnalyzer 의 기존 게이트
///   3) 직선피팅 실패(LineFitOk=false) — 중앙값 폴백이라 교점이 Peak 위치와 무관해짐
/// 재시도는 하지 않는다. 실패해도 오버레이 JPEG 는 생성되므로 Weld 페이지에서 원인을 볼 수 있다.
/// </summary>
public class BeadFindStep : ISequenceStep
{
    private readonly int _peakId;
    private readonly WeldTrackingService _weld;
    private readonly CameraService _camera;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<BeadFindStep> _logger;

    /// <param name="peakId">1 또는 2. DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public BeadFindStep(
        int peakId,
        WeldTrackingService weld,
        CameraService camera,
        CobotService cobot,
        ParameterService param,
        ILogger<BeadFindStep> logger)
    {
        _peakId = peakId;
        _weld = weld;
        _camera = camera;
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => $"bead{_peakId}Find";
    public string DisplayName => $"Bead{_peakId} 찾기";
    public int DefaultOrder => _peakId == 1 ? 700 : 1100;

    public StepValidation Validate(SequenceContext context)
    {
        var common = WeldSequenceSupport.ValidateCommon(_cobot, _camera, _weld, requireDetector: true);
        if (!common.IsValid) return common;

        if (!context.Bag.ContainsKey(WeldSequenceSupport.PeakFindBagKey(_peakId)))
            return StepValidation.Fail(
                $"Peak{_peakId} 측정값 없음 — {(_peakId == 1 ? "⑤⑥" : "⑨⑩")} 단계를 먼저 실행하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var (roi, roiSrc) = await WeldSequenceSupport.GetRoiAsync(_param, _camera);
        if (roi is null)
            return StepResult.Fail("깊이 ROI 를 만들 수 없습니다 — IR 프레임/ROI 설정을 확인하세요.");

        // 측정 축을 ② 검사방향과 정합시킨다 — d 는 진행축과 수직(cross) 방향으로 측정된다.
        var progressAxis = WeldSequenceSupport.ApplyProgressAxis(_weld, context);

        // Peak ROI 와 Weld ROI 에 동일한 깊이 ROI 를 사용한다.
        var (m, detect) = await _weld.CapturePeakAsync(_peakId, roi, roi, ct);

        if (detect is null)
            return StepResult.Fail($"Bead{_peakId} 검출 결과가 없습니다.");

        if (!detect.Success)
            return StepResult.Fail($"Bead{_peakId} 검출 실패 — {detect.Message}");

        if (!detect.LineFitOk)
            return StepResult.Fail(
                $"Bead{_peakId} 직선피팅 실패(중앙값 폴백) — 빨간 교점이 Peak 위치와 무관해져 " +
                $"각도 산출 근거로 쓸 수 없습니다. coverage={detect.Confidence:P0}");

        if (m is null)
            return StepResult.Fail($"Bead{_peakId} 측정 슬롯 저장 실패.");

        var dMm = _weld.DMm(m);
        var extrapPx = ComputeExtrapolation(detect, progressAxis == WeldProgressAxis.Horizontal);

        // 비드 센터링(⑦⁺/⑪⁺)이 cross 축 이동량으로 소비한다.
        context.Bag[WeldSequenceSupport.BeadDMmBagKey(_peakId)] = dMm;

        _logger.LogInformation(
            "⑦/⑪ Bead{Id} 찾음: d={DMm:0.0}mm ({DPx:0.0}px), coverage={Cov:P0}, 외삽={Ex:0}px, ROI={Roi}",
            _peakId, dMm, detect.DPixel, detect.Confidence, extrapPx, roiSrc);

        var extrapNote = extrapPx > 0 ? $"외삽 {extrapPx:0}px" : "외삽 없음";
        return StepResult.Ok(
            $"Bead{_peakId} 찾음 — d={dMm:+0.0;-0.0}mm ({detect.DPixel:+0.0;-0.0}px), " +
            $"coverage={detect.Confidence:P0}, {extrapNote}, ROI={roiSrc}");
    }

    /// <summary>
    /// 빨간 교점이 비드가 실제로 검출된 구간 밖이면 그 초과 거리(px)를 반환한다. 구간 안이면 0.
    ///
    /// 비드는 물리적으로 판재 양끝까지 이어져 있으므로 외삽 자체는 정당하다(차단하지 않는다).
    /// 다만 외삽 거리가 클수록 기울기 오차가 증폭되므로, ⑦⑪ 두 측정의 외삽 거리가 크게 다르면
    /// 각도 왜곡의 유력한 원인이 된다. 그 판별을 위해 수치만 기록한다.
    /// </summary>
    private static double ComputeExtrapolation(WeldDetectionResult r, bool horiz)
    {
        if (r.Centerline.Count == 0 || r.WeldPoint is null) return 0;

        // 진행축 좌표(가로면 X, 세로면 Y)로 비교한다.
        // 피팅 성공 시 Centerline 은 유효 구간의 시작·끝 2점이다(WeldMaskAnalyzer).
        var first = horiz ? r.Centerline[0].X : r.Centerline[0].Y;
        var last = horiz ? r.Centerline[^1].X : r.Centerline[^1].Y;
        var lo = Math.Min(first, last);
        var hi = Math.Max(first, last);
        var t = horiz ? r.WeldPoint.X : r.WeldPoint.Y;

        if (t < lo) return lo - t;
        if (t > hi) return t - hi;
        return 0;
    }
}
