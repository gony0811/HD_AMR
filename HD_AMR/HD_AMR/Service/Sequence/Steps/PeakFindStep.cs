using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑤/⑨ Peak 찾기 — 깊이 영상에서 코루게이션 Peak(마루)를 찾아 자홍색 선으로 표시하고,
/// 진행축(BASE X) FOV 센터로부터의 이격거리를 mm 로 환산해 <see cref="SequenceContext.Bag"/> 에 담는다.
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

    /// <summary>ROI 폭 경고를 위한 가정 거리(mm). ③에서 400mm 로 정렬된 상태를 전제.</summary>
    private const int AssumedStandoffMm = 400;

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

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var pitchMm = await WeldSequenceSupport.GetPitchMmAsync(_param);
        if (pitchMm <= 0)
            return StepResult.Fail($"'{WeldSequenceSupport.PitchMmKey}' 파라미터가 0 이하입니다.");

        var (roi, roiSrc) = await WeldSequenceSupport.GetRoiAsync(_param, _camera);
        if (roi is null)
            return StepResult.Fail("깊이 ROI 를 만들 수 없습니다 — IR 프레임/ROI 설정을 확인하세요.");

        // ROI 폭이 pitch 를 넘으면 ROI 안에 Peak 가 2개 들어올 수 있고, 분석기는 더 가까운 쪽을
        // 고른다. 판이 기울어 있으면 중앙이 아닌 Peak 를 잡을 수 있어 경고만 남긴다(차단하지 않음).
        var pitchPx = WeldSequenceSupport.PitchToPixels(pitchMm, AssumedStandoffMm, _camera);
        if (pitchPx > 0 && roi.Width > pitchPx)
            _logger.LogWarning(
                "Peak{Id} ROI 폭 {W}px 가 pitch 환산 {P:0}px 를 초과 — ROI 안에 Peak 가 2개 들어올 수 있습니다.",
                _peakId, roi.Width, pitchPx);

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
            "⑤/⑨ Peak{Id} 찾음: offset={Px:0.0}px / {Mm:0.0}mm, depth={D}mm, conf={C:0.00}, ROI={Roi}({X},{Y},{W},{H})",
            _peakId, r.OffsetPx, offsetMm, r.DepthMm, r.Confidence, roiSrc, roi.X, roi.Y, roi.Width, roi.Height);

        return StepResult.Ok(
            $"Peak{_peakId} 찾음 — 이격 {offsetMm:+0.0;-0.0}mm ({r.OffsetPx:+0.0;-0.0}px), " +
            $"깊이 {r.DepthMm}mm, conf {r.Confidence:0.00}, {scaleNote}, ROI={roiSrc}");
    }
}
