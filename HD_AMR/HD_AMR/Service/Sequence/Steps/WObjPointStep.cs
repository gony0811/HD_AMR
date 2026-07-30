using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// 작업물 좌표계 3점법 참조점 기록 — 현재 TCP 를 점 N 으로 컨트롤러에 기록한다
/// (<see cref="FairinoRpcClient.SetWObjCoordPointAsync"/>).
///   - 점1(원점): ⑦⁺ Bead1 센터링 완료 위치 (order 760)
///   - 점2(X축 방향): ⑪⁺ Bead2 센터링 완료 위치 (order 1160) — 비드1→비드2 가 작업물 X축(용접 진행 방향)이 된다.
/// 점1·점2 모두 검사캠 시프트가 적용된 위치에서 기록되지만 같은 상수 오프셋이라 X축 방향(점2−점1)에는 영향이 없다.
///
/// 좌표계 번호는 <see cref="WObjIdKey"/> 파라미터로 설정하고, 계산법은 <b>0(원점-X축-Z축)으로 고정</b>이다.
/// 번호·계산법은 점 기록 시점에는 컨트롤러에 전달되지 않는다 — 점3 기록 후의
/// 계산·등록(<see cref="FairinoRpcClient.RegisterWObjFromTeachingAsync"/>) 단계가 소비한다.
/// </summary>
public class WObjPointStep : ISequenceStep
{
    private readonly int _pointNum;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<WObjPointStep> _logger;

    /// <summary>3점법 등록 대상 작업물 좌표계 번호(0~14) 파라미터 키. 없으면 기본 1.</summary>
    public const string WObjIdKey = "Sequence.WObj.Id";

    /// <summary>기록된 점 N 의 BASE 기준 TCP 포즈(double[6])를 담는 Bag 키.
    /// 등록 스텝(WObjRegisterStep)이 가상 점3과 함께 클라이언트 계산 경로에 쓴다.</summary>
    public static string PointPoseBagKey(int n) => $"wobj.point{n}Pose";

    private const int DefaultWObjId = 1;

    /// <summary>3점법 계산법 — 0: 원점-X축-Z축 고정 (Fairino ComputeWObjCoord method).</summary>
    private const int Method = 0;

    /// <param name="pointNum">1(원점) 또는 2(X축 방향). DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public WObjPointStep(int pointNum, CobotService cobot, ParameterService param, ILogger<WObjPointStep> logger)
    {
        _pointNum = pointNum;
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    private string PointName => _pointNum == 1 ? "점1(원점)" : "점2(X방향)";

    public string Key => $"wobjPoint{_pointNum}";
    public string DisplayName => $"작업물 좌표계 {PointName} 기록";
    public int DefaultOrder => _pointNum == 1 ? 760 : 1160;

    public StepValidation Validate(SequenceContext context)
        // 순수 기록 단계 — 코봇 연결만 필요하다.
        => _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var wobjId = (int)(await GetWObjIdAsync());
        if (wobjId is < 0 or > 14)
            return StepResult.Fail($"'{WObjIdKey}' 범위 초과 ({wobjId}) — 0~14 로 설정하세요.");

        var rc = await _cobot.Rpc.SetWObjCoordPointAsync(_pointNum, ct);
        if (rc != 0)
            return StepResult.Fail($"{PointName} 기록 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        // 확인용 표기 + 등록 스텝(가상 점3 클라이언트 계산)용 보관.
        var pose = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        context.Bag[PointPoseBagKey(_pointNum)] = pose;

        _logger.LogInformation(
            "작업물 좌표계 {Point} 기록: TCP [{X:0.0}, {Y:0.0}, {Z:0.0}] (wobj {Id}, 계산법 {M}: 원점-X축-Z축)",
            PointName, pose[0], pose[1], pose[2], wobjId, Method);

        var remaining = _pointNum == 1 ? "점2·3" : "점3";
        return StepResult.Ok(
            $"작업물 좌표계 {PointName} 기록 — TCP [{pose[0]:0.0}, {pose[1]:0.0}, {pose[2]:0.0}] " +
            $"(wobj {wobjId}, 계산법 {Method}: 원점-X축-Z축. {remaining} 기록 후 등록됩니다.)");
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjIdKey) ?? DefaultWObjId; }
        catch { return DefaultWObjId; }
    }
}
