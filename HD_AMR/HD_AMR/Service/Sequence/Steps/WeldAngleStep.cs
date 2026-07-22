using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑫ 각도 산출 — ⑦⑪이 저장한 두 측정(<c>M1</c>/<c>M2</c>)의 위치오차 d1·d2 와 pitch 로
/// 비드선 기울기 θ = atan2(d2 − d1, pitch) 를 구한다. 화면 표시만 하고 로봇은 움직이지 않는다.
/// (DB 저장·자세 보정은 θ 신뢰도가 확인된 뒤 별도로 붙인다.)
/// </summary>
public class WeldAngleStep : ISequenceStep
{
    private readonly WeldTrackingService _weld;
    private readonly ParameterService _param;
    private readonly ILogger<WeldAngleStep> _logger;

    /// <summary>이 coverage 미만이면 메시지에 주의 표시를 단다. 실패 처리는 하지 않는다.</summary>
    private const double LowCoverageWarn = 0.30;

    public WeldAngleStep(WeldTrackingService weld, ParameterService param, ILogger<WeldAngleStep> logger)
    {
        _weld = weld;
        _param = param;
        _logger = logger;
    }

    public string Key => "weldAngle";
    public string DisplayName => "각도 산출";
    public int DefaultOrder => 1200;

    public StepValidation Validate(SequenceContext context)
    {
        if (_weld.M1 is null)
            return StepValidation.Fail("Peak1 측정값 없음 — ⑦ 단계를 먼저 실행하세요.");
        if (_weld.M2 is null)
            return StepValidation.Fail("Peak2 측정값 없음 — ⑪ 단계를 먼저 실행하세요.");
        if (!_weld.ScaleAvailable)
            return StepValidation.Fail("스케일(fx) 없음 — 카메라 해상도/FOV 설정을 확인하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var pitchMm = await WeldSequenceSupport.GetPitchMmAsync(_param);
        if (pitchMm <= 0)
            return StepResult.Fail($"'{WeldSequenceSupport.PitchMmKey}' 파라미터가 0 이하입니다.");

        // WeldTrackingService.Pitch 는 기본 0·비영속이라 여기서 설정한 뒤 계산한다.
        var angle = await _weld.ComputeAngleAsync(pitchMm, ct);
        if (angle is null)
            return StepResult.Fail($"각도 산출 실패 — {_weld.Message}");

        // 두 측정의 coverage 가 낮으면 θ 신뢰도가 떨어지므로 주의만 병기한다(공정 검증 전이라 차단하지 않음).
        var cov1 = _weld.M1?.Confidence ?? 0;
        var cov2 = _weld.M2?.Confidence ?? 0;
        var warn = cov1 < LowCoverageWarn || cov2 < LowCoverageWarn
            ? $" ⚠coverage 낮음 (#1 {cov1:P0}, #2 {cov2:P0})"
            : "";

        _logger.LogInformation(
            "⑫ 각도 산출: θ={Theta:0.000}°, d1={D1:0.0}mm, d2={D2:0.0}mm, pitch={Pitch:0.#}mm, cov=({C1:P0},{C2:P0})",
            angle.ThetaDeg, angle.D1, angle.D2, angle.Pitch, cov1, cov2);

        return StepResult.Ok(
            $"θ = {angle.ThetaDeg:0.00}° " +
            $"(d1={angle.D1:+0.0;-0.0}mm, d2={angle.D2:+0.0;-0.0}mm, pitch={angle.Pitch:0.#}mm){warn}");
    }
}
