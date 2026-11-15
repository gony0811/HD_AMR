using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑤/⑨ Peak 찾기 — 깊이 영상에서 코루게이션 Peak(마루)를 찾아 자홍색 선으로 표시하고,
/// 진행축(② 검사방향에서 자동 결정 — <see cref="WeldSequenceSupport.GetProgressAxis"/>) FOV 센터로부터의
/// 이격거리를 mm 로 환산해 <see cref="SequenceContext.Bag"/> 에 담는다.
/// 이동은 하지 않는다. 후속 센터링 단계(⑥/⑩)가 이 값을 소비한다.
/// </summary>
public class PeakFindStep : ISequenceStep
{
    private readonly int _peakId;
    private readonly WeldTrackingService _weld;
    private readonly CameraService _camera;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<PeakFindStep> _logger;


    /// <param name="peakId">1 또는 2. DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public PeakFindStep(
        int peakId,
        WeldTrackingService weld,
        CameraService camera,
        CobotService cobot,
        ParameterService param,
        ILogger<PeakFindStep> logger)
    {
        _peakId = peakId;
        _weld = weld;
        _camera = camera;
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => $"peak{_peakId}Find";
    public string DisplayName => $"Peak{_peakId} 찾기";
    public int DefaultOrder => _peakId == 1 ? 500 : 900;

    public StepValidation Validate(SequenceContext context)
        // Peak 탐색은 깊이만 쓰므로 비드 검출기(OpenCV)는 필요 없다.
        => WeldSequenceSupport.ValidateCommon(_cobot, _camera, _weld, requireDetector: false);

    public Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
        => BundleTeach.RunWithPoint2FallbackAsync(_peakId, context, _cobot, _param, _logger, ct, RunAsync);

    private async Task<StepResult> RunAsync(SequenceContext context, CancellationToken ct)
    {
        // 새 측정 사이클의 시작 — 이전 실행의 M1/M2 가 남아 있으면 새 M1 과 짝지어 각도가 자동 계산돼
        // 낡은 θ 가 화면에 표시된다. Peak1 찾기에서 측정 슬롯을 비운다.
        if (_peakId == 1) _weld.ResetMeasurements();

        var pitchMm = await WeldSequenceSupport.GetPitchMmAsync(_param, context);
        if (pitchMm <= 0)
            return StepResult.Fail($"'{WeldSequenceSupport.PitchMmKey}' 파라미터가 0 이하입니다.");

        var (roi, roiSrc) = await WeldSequenceSupport.GetPeakRoiAsync(_param, _camera);
        if (roi is null)
            return StepResult.Fail("Peak ROI 를 만들 수 없습니다 — IR 프레임/ROI 설정을 확인하세요.");

        // 측정 축을 ② 검사방향과 정합시킨다 — Weld 패널 수동 설정과 무관하게 시퀀스가 소유.
        var (progressAxis, axisSrc) = await WeldSequenceSupport.ApplyProgressAxisAsync(_weld, context, _cobot, _param, ct);
        _logger.LogInformation("[Peak{Id} 찾기] 진행축={Axis} — {Src}", _peakId, progressAxis, axisSrc);

        // 진행축 ROI 길이가 pitch 를 넘으면 ROI 안에 Peak 가 2개 들어올 수 있다. 분석기는 그중 FOV 센터에 가장 가까운
        // Peak 를 고르지만(국소 마루 판정), ROI 끝에 걸린 마루는 판정에서 빠질 수 있어 경고는 남긴다(차단하지 않음).
        // ROI 폭 경고용 가정 거리 — ③ 에서 이 거리로 정렬된 상태를 전제한다. ②의 면 후퇴 거리와 같은
        // 값을 쓰므로 레시피에서 목표거리를 바꾸면 여기도 따라간다(예전에는 400 하드코딩이었다).
        var assumedMm = (int)Math.Round(CobotInspectionMoveStep.ResolveApproachDistanceMm(context));
        var pitchPx = WeldSequenceSupport.PitchToPixels(pitchMm, assumedMm, _camera, progressAxis);
        var roiProgressLen = progressAxis == WeldProgressAxis.Horizontal ? roi.Width : roi.Height;
        if (pitchPx > 0 && roiProgressLen > pitchPx)
            _logger.LogWarning(
                "Peak{Id} ROI 진행축 길이 {W}px 가 pitch 환산 {P:0}px 를 초과 — ROI 안에 Peak 가 2개 들어올 수 있습니다.",
                _peakId, roiProgressLen, pitchPx);

        var r = await _weld.FindPeakAsync(roi, ct);
        if (!r.Found)
            return StepResult.Fail($"Peak{_peakId} 미검출 — {r.Message}");

        var (offsetMm, scaleNote) = WeldSequenceSupport.ResolveOffsetMm(r, _weld, _camera);

        // ⑧에서 pitch 만큼 이동했으므로 Peak2 는 센터 근처에 있어야 정상이다.
        // 크게 벗어났다면 인접한 다른 Peak 를 잡았을 가능성이 높다.
        if (_peakId == 2 && Math.Abs(offsetMm) > pitchMm / 3)
            return StepResult.Fail(
                $"Peak2 이격 {offsetMm:+0.0;-0.0}mm 가 pitch/3({pitchMm / 3:0}mm)를 초과 — " +
                $"다른 Peak 를 잡았을 가능성이 있습니다. '{WeldSequenceSupport.PitchDirKey}' / " +
                $"'{WeldSequenceSupport.PitchMmKey}' 를 확인하세요.");

        context.Bag[WeldSequenceSupport.PeakFindBagKey(_peakId)] = r;
        context.Bag[WeldSequenceSupport.OffsetMmBagKey(_peakId)] = offsetMm;

        _logger.LogInformation(
            "[Peak{Id} 찾기] 찾음: offset={Px:0.0}px / {Mm:0.0}mm, depth={D}mm, conf={C:0.00}, ROI 내 Peak {N}개(센터에 가장 가까운 것), ROI={Roi}({X},{Y},{W},{H})",
            _peakId, r.OffsetPx, offsetMm, r.DepthMm, r.Confidence, r.PeakCount, roiSrc, roi.X, roi.Y, roi.Width, roi.Height);

        return StepResult.Ok(
            $"Peak{_peakId} 찾음 — 이격 {offsetMm:+0.0;-0.0}mm ({r.OffsetPx:+0.0;-0.0}px), " +
            $"깊이 {r.DepthMm}mm, conf {r.Confidence:0.00}, {scaleNote}, ROI={roiSrc}");
    }
}
