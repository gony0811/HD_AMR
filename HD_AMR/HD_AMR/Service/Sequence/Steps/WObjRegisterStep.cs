using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑯⁺ 작업물 좌표계 등록 — 점1(원점, ⑦⁺⁺)·점2(X방향, ⑯)에 더해, <b>실제 이동 없이</b>
/// "현재 TCP 에서 툴 Z+ 방향 <see cref="ZOffsetMm"/>mm 위치"를 가상 점3(Z방향)으로 삼아
/// 계산법 0(원점-X축-Z축)으로 좌표계를 계산하고 <see cref="WObjPointStep.WObjIdKey"/> 번호에 등록한다.
///
/// 컨트롤러의 3점 버퍼(SetWObjCoordPoint)는 현재 TCP 만 기록할 수 있어 가상점을 넣을 수 없으므로,
/// Bag 에 보관된 점1·점2 포즈와 가상 점3으로 <b>클라이언트 계산 경로</b>
/// (<see cref="FairinoRpcClient.RegisterWObjFromPointsAsync"/> = ComputeFramePose + SetWObjCoord)를 쓴다.
/// ComputeFramePose 의 method 0 은 점3의 X축 성분을 외적 과정에서 제거하므로 "현재 위치(점2 근방) + 툴Z 50mm"
/// 가상점으로 정확하다. (오일러 ZYX 규약 주의 — ComputeFramePose 기존 경고 참조.)
/// </summary>
public class WObjRegisterStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<WObjRegisterStep> _logger;

    /// <summary>가상 점3 거리(mm) — 현재 TCP 에서 툴 Z+ 방향.</summary>
    private const double ZOffsetMm = 50.0;

    /// <summary>3점법 계산법 — 0: 원점-X축-Z축 고정.</summary>
    private const int Method = 0;

    private const int DefaultWObjId = 1;

    public WObjRegisterStep(CobotService cobot, ParameterService param, ILogger<WObjRegisterStep> logger)
    {
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => "wobjRegister";
    public string DisplayName => "작업물 좌표계 등록 (점3 Z+50)";
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

        // 가상 점3: 현재 TCP 에서 툴 Z+ 방향 ZOffsetMm 병진 합성 — 모션 없음.
        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var p3 = FrameMath.FromFrame(new[] { 0.0, 0.0, ZOffsetMm, 0.0, 0.0, 0.0 }, cur);

        _logger.LogInformation(
            "⑯⁺ 작업물 좌표계 등록: wobj {Id}, p1 [{X1:0.0},{Y1:0.0},{Z1:0.0}], p2 [{X2:0.0},{Y2:0.0},{Z2:0.0}], " +
            "p3(가상, 툴Z+{Off:0}mm) [{X3:0.0},{Y3:0.0},{Z3:0.0}]",
            wobjId, p1[0], p1[1], p1[2], p2[0], p2[1], p2[2], ZOffsetMm, p3[0], p3[1], p3[2]);

        double[] pose;
        try
        {
            pose = await _cobot.Rpc.RegisterWObjFromPointsAsync(wobjId, p1, p2, p3, Method, ct: ct);
        }
        catch (InvalidOperationException ex)
        {
            return StepResult.Fail($"작업물 좌표계 등록 실패 — {ex.Message}");
        }

        return StepResult.Ok(
            $"작업물 좌표계 wobj {wobjId} 등록 완료 — 원점 [{pose[0]:0.0}, {pose[1]:0.0}, {pose[2]:0.0}], " +
            $"RPY [{pose[3]:0.00}, {pose[4]:0.00}, {pose[5]:0.00}] " +
            $"(점3 = 현재 TCP + 툴Z {ZOffsetMm:0}mm 가상점, 계산법 {Method}: 원점-X축-Z축).");
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjPointStep.WObjIdKey) ?? DefaultWObjId; }
        catch { return DefaultWObjId; }
    }
}
