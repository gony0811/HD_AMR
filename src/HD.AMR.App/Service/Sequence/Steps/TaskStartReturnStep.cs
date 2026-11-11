using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑯⁺ 다음, 검사 순회 전 — 점2(피치 이동으로 끝쪽에 가 있는 교점)에서
/// <b>검사 가능한 용접 작업의 시작점</b>으로 돌아간다.
///
/// 끝점(점2·용접선 끝)은 팔 스트로크가 거의 없어 그 자리에서 평탄면 탐색이 실패하기 쉽다.
/// 그래서 비드1 센터링 자세(없으면 작업물 점1)로 복귀한 뒤, 이어지는 평탄면 탐색·WD 가 시작점에서 돈다.
/// 점2 포즈는 절대 목표로 쓰지 않는다.
/// </summary>
public class TaskStartReturnStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ILogger<TaskStartReturnStep> _logger;

    public const string StepKey = "taskStartReturn";

    /// <summary>모션 후 진동 대기(ms). Peak2 이동과 같은 값.</summary>
    private const int SettleMs = 500;

    public TaskStartReturnStep(CobotService cobot, ILogger<TaskStartReturnStep> logger)
    {
        _cobot = cobot;
        _logger = logger;
    }

    public string Key => StepKey;
    public string DisplayName => "검사 시작점 복귀";
    public int DefaultOrder => 1175;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");
        if (SelectStartPose(context.Bag, out _, out _) is null)
            return StepValidation.Fail("검사 시작점 없음 — 비드1 센터링 또는 작업물 점1 기록을 먼저 실행하세요.");
        return StepValidation.Ok();
    }

    /// <summary>
    /// 시작 자세. 비드1 센터링 완료 자세를 우선하고, 없으면 작업물 점1. 점2(끝쪽)는 쓰지 않는다.
    /// 반환 배열은 Bag 과 분리된 복사본이다.
    /// </summary>
    public static double[]? SelectStartPose(IReadOnlyDictionary<string, object> bag, out string source, out string ignoredEnd)
    {
        ignoredEnd = bag.ContainsKey(WObjPointStep.PointPoseBagKey(2)) ? "점2는 끝쪽이라 복귀 목표에서 제외" : "";
        if (CopyPose(bag, WeldSequenceSupport.Bead1CenteredPoseBagKey) is { } bead1)
        {
            source = "점1 교점(비드1 센터링)";
            return bead1;
        }
        if (CopyPose(bag, WObjPointStep.PointPoseBagKey(1)) is { } point1)
        {
            source = "작업물 점1";
            return point1;
        }
        source = "";
        return null;
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var target = SelectStartPose(context.Bag, out var source, out var ignoredEnd);
        if (target is null)
            return StepResult.Fail("검사 시작점 없음 — 비드1 센터링 또는 작업물 점1 기록을 먼저 실행하세요.");

        _logger.LogInformation(
            "[검사 시작점 복귀] {Source} 로 이동 [{X:0.0}, {Y:0.0}, {Z:0.0}]. {Ignored}",
            source, target[0], target[1], target[2], ignoredEnd);

        var rc = await _cobot.Rpc.MoveLAsync(target, tool: context.Tool, user: 0,
            vel: context.Velocity, acc: 100, ovl: 100, blendR: -1, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"검사 시작점 복귀 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        await Task.Delay(SettleMs, ct);
        var note = string.IsNullOrEmpty(ignoredEnd) ? "" : $" {ignoredEnd}.";
        return StepResult.Ok($"검사 시작점 복귀 완료 — {source} [{target[0]:0.0}, {target[1]:0.0}, {target[2]:0.0}].{note}");
    }

    private static double[]? CopyPose(IReadOnlyDictionary<string, object> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value) || value is not double[] { Length: 6 } pose)
            return null;
        return (double[])pose.Clone();
    }
}
