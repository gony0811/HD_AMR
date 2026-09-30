using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑯⁺ 내부 작업물 좌표계 계산 — 점1(코로게이션 Top)·점2(용접선 X+ 방향)와 점1 교시 자세의
/// 툴 +Z로 BASE 기준 프레임을 만든다. 레시피 원점은 코로게이션 바닥이므로 Top에서 작업물 Z- 방향
/// 33.8mm 내린 가상점으로 설정한다. 계산 결과는 실행 컨텍스트에만 보관하며 컨트롤러에는 등록하지 않는다.
/// </summary>
public class WObjRegisterStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<WObjRegisterStep> _logger;

    private const int DefaultWObjId = 1;

    public WObjRegisterStep(CobotService cobot, ParameterService param, ILogger<WObjRegisterStep> logger)
    {
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => "wobjRegister";
    public string DisplayName => "내부 작업물 좌표계 계산 (Top Z−33.8)";
    public int DefaultOrder => 1170;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        if (!context.Bag.ContainsKey(WObjPointStep.PointPoseBagKey(1))
            || !context.Bag.ContainsKey(WObjPointStep.PointPoseBagKey(2)))
            return StepValidation.Fail("점1·점2 기록 없음 — 작업물 좌표계 점1/점2 기록 단계를 먼저 실행하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var wobjId = (int)(await GetWObjIdAsync());
        if (wobjId is < 0 or > 14)
            return StepResult.Fail($"'{WObjPointStep.WObjIdKey}' 범위 초과 ({wobjId}) — 0~14 로 설정하세요.");

        if (context.Bag[WObjPointStep.PointPoseBagKey(1)] is not double[] p1 || p1.Length != 6
            || context.Bag[WObjPointStep.PointPoseBagKey(2)] is not double[] p2 || p2.Length != 6)
            return StepResult.Fail("점1·점2 포즈를 읽을 수 없습니다 — 기록 단계를 다시 실행하세요.");

        double[] frame;
        try
        {
            frame = InternalWorkpieceFrame.Compute(p1, p2);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return StepResult.Fail($"내부 작업물 좌표계 계산 실패 — {ex.Message}");
        }

        context.Bag[InternalWorkpieceFrame.BagKey] = frame;
        var span = Math.Sqrt(
            Math.Pow(p2[0] - p1[0], 2) + Math.Pow(p2[1] - p1[1], 2) + Math.Pow(p2[2] - p1[2], 2));

        _logger.LogInformation(
            "⑯⁺ 내부 작업물 좌표계 계산: id {Id}, Top [{X1:0.0},{Y1:0.0},{Z1:0.0}], X+점 [{X2:0.0},{Y2:0.0},{Z2:0.0}], " +
            "간격 {Span:0.0}mm, 바닥 원점(Top Z-{Height:0.0}) [{Ox:0.0},{Oy:0.0},{Oz:0.0}], RPY [{Rx:0.00},{Ry:0.00},{Rz:0.00}]",
            wobjId, p1[0], p1[1], p1[2], p2[0], p2[1], p2[2], span,
            InternalWorkpieceFrame.DefaultCorrugationTopHeightMm,
            frame[0], frame[1], frame[2], frame[3], frame[4], frame[5]);

        return StepResult.Ok(
            $"내부 작업물 좌표계 계산 완료 — 교시 간격 {span:0.0}mm, 바닥 원점 " +
            $"[{frame[0]:0.0}, {frame[1]:0.0}, {frame[2]:0.0}], RPY " +
            $"[{frame[3]:0.00}, {frame[4]:0.00}, {frame[5]:0.00}] " +
            $"(Top에서 작업물 Z−{InternalWorkpieceFrame.DefaultCorrugationTopHeightMm:0.0}mm, 컨트롤러 등록 없음).");
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjPointStep.WObjIdKey) ?? DefaultWObjId; }
        catch { return DefaultWObjId; }
    }
}
