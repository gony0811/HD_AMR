using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 내부 작업물 좌표계 2점 참조점 기록 — 현재 TCP의 BASE 포즈를 실행 컨텍스트에만 보관한다.
/// 컨트롤러 작업물 좌표계 버퍼/등록 기능은 사용하지 않는다.
///   - 점1(Top 기준): ⑦⁺ Bead1 센터링 완료 위치 (order 760)
///   - 점2(X축 방향): ⑪⁺ Bead2 센터링 완료 위치 (order 1160) — 비드1→비드2 가 작업물 X축(용접 진행 방향)이 된다.
/// 점1·점2 모두 검사캠 시프트가 적용된 위치에서 기록되지만 같은 상수 오프셋이라 X축 방향(점2−점1)에는 영향이 없다.
///
/// 점1 교시 자세의 툴 +Z가 작업물 Z+가 되며, 등록 단계가 Top에서 Z-33.8mm인 바닥 원점을 계산한다.
/// </summary>
public class WObjPointStep : ISequenceStep
{
    private readonly int _pointNum;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<WObjPointStep> _logger;

    /// <summary>레거시 UI/설정 호환용 내부 작업물 좌표계 식별 번호. 컨트롤러에는 등록하지 않는다.</summary>
    public const string WObjIdKey = "Sequence.WObj.Id";

    /// <summary>기록된 점 N 의 BASE 기준 TCP 포즈(double[6])를 담는 Bag 키.
    /// 등록 스텝(WObjRegisterStep)이 가상 점3과 함께 클라이언트 계산 경로에 쓴다.</summary>
    public static string PointPoseBagKey(int n) => $"wobj.point{n}Pose";

    private const int DefaultWObjId = 1;

    /// <param name="pointNum">1(원점) 또는 2(X축 방향). DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public WObjPointStep(int pointNum, CobotService cobot, ParameterService param, ILogger<WObjPointStep> logger)
    {
        _pointNum = pointNum;
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    private string PointName => _pointNum == 1 ? "점1(Top 기준)" : "점2(X+방향)";

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

        // 컨트롤러의 SetWObjCoordPoint 버퍼는 사용하지 않는다. 선택된 TCP의 BASE 포즈만 내부 보관한다.
        var pose = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        context.Bag[PointPoseBagKey(_pointNum)] = (double[])pose.Clone();

        _logger.LogInformation(
            "내부 작업물 좌표계 {Point} 기록: TCP [{X:0.0}, {Y:0.0}, {Z:0.0}] (tool {Tool}, 내부 id {Id})",
            PointName, pose[0], pose[1], pose[2], context.Tool, wobjId);

        var remaining = _pointNum == 1 ? "점2(X+방향)" : "내부 프레임 계산";
        return StepResult.Ok(
            $"내부 작업물 좌표계 {PointName} 기록 — BASE TCP [{pose[0]:0.0}, {pose[1]:0.0}, {pose[2]:0.0}] " +
            $"(tool {context.Tool}, 내부 id {wobjId}. 다음: {remaining}).");
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjIdKey) ?? DefaultWObjId; }
        catch { return DefaultWObjId; }
    }
}
