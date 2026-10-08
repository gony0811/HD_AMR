using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ① AMR 위치 이동 — 시퀀스 페이지 단독 테스트용. ACS 경로에서 VDA5050 order 의 nodePosition 주행
/// (<see cref="Vda5050OrderExecutor"/>)이 하던 일을 페이지에서 재현한다: 코봇을 홈으로 접은 뒤
/// <see cref="AmrDriveService.DriveToAsync"/>(REST <c>/robot/go</c>)로 목표 좌표에 정차할 때까지 기다린다.
///
/// ACS 경로에서는 주행이 order 실행기 몫이라 이 스텝을 돌리지 않는다
/// (<see cref="Inspection.WeldInspectionOrchestrator.SelectStepKeys"/> 가 항상 제외).
/// 목표(<see cref="SequenceContext.AmrTargetX"/>/Y/Theta)가 비어 있으면 이동 없이 통과한다 —
/// AMR 을 이미 세워 둔 상태에서 풀오토를 돌릴 수 있게.
/// </summary>
public class AmrMoveStep : ISequenceStep
{
    private readonly AmrDriveService _drive;
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly ILogger<AmrMoveStep> _logger;

    public AmrMoveStep(AmrDriveService drive, AMRService amr, CobotService cobot, ILogger<AmrMoveStep> logger)
    {
        _drive = drive;
        _amr = amr;
        _cobot = cobot;
        _logger = logger;
    }

    public const string StepKey = "amrMove";

    public string Key => StepKey;
    public string DisplayName => "AMR 위치 이동";
    public int DefaultOrder => 100;

    private static bool HasTarget(SequenceContext c) => c is { AmrTargetX: not null, AmrTargetY: not null, AmrTargetThetaRad: not null };

    public StepValidation Validate(SequenceContext context)
    {
        if (!HasTarget(context))
            return StepValidation.Ok();   // 목표 미지정 = 이동 생략

        if (!_amr.IsConnected || _amr.LatestStatus is null)
            return StepValidation.Fail("AMR 미연결");

        // 팔이 뻗은 채 주행하면 구조물 충돌 위험 — 주행 전 홈 복귀가 선행조건.
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결 — 주행 전 홈 복귀 불가");

        return SequenceEntry.ValidateHome(context);
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (!HasTarget(context))
        {
            const string skip = "AMR 목표 좌표 미지정 — 이동 생략.";
            context.Progress?.Invoke(skip);
            return StepResult.Ok(skip);
        }

        double x = context.AmrTargetX!.Value, y = context.AmrTargetY!.Value, theta = context.AmrTargetThetaRad!.Value;

        // 1) 코봇 홈 복귀 — 벽면 후퇴 → ready 경유 → 홈 (CobotHomeReturnStep 과 같은 경로).
        var home = context.Positions["home"];
        var homeJoints = new[]
        {
            home.J1!.Value, home.J2!.Value, home.J3!.Value,
            home.J4!.Value, home.J5!.Value, home.J6!.Value,
        };
        var cur = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
        if (!SequenceEntry.IsWithinJointTolerance(cur, homeJoints))
        {
            context.Progress?.Invoke("AMR 주행 전 코봇 홈 복귀…");
            await SequenceEntry.RetractFromWallAsync(_cobot, context, _logger, ct);
            await SequenceEntry.ReturnViaReadyAsync(_cobot, context, _logger, ct);
            await SequenceEntry.EnsureCobotAtHomeAsync(_cobot, context, ct);
        }

        // 2) 주행 + 정차 대기. 정지(취소) 시 AMR 도 세운다 — SequenceService.StopAsync 는 코봇만 정지한다.
        context.Progress?.Invoke($"AMR 이동 → ({x:0.###}, {y:0.###}) m, θ={theta * 180 / Math.PI:0.##}°");
        AmrDriveResult r;
        try
        {
            r = await _drive.DriveToAsync(x, y, theta, null, context.Progress, ct);
        }
        catch (OperationCanceledException)
        {
            await _drive.StopAsync(CancellationToken.None);
            throw;
        }

        if (!r.Success)
        {
            await _drive.StopAsync(CancellationToken.None);
            return StepResult.Fail($"AMR 이동 실패: {r.Error}");
        }

        var msg = $"AMR 정차 ({r.Pose.X:0.###}, {r.Pose.Y:0.###}) m, θ={r.Pose.Angle * 180 / Math.PI:0.##}° " +
                  $"— 목표 대비 {r.ResidualMm:0}mm / {r.ResidualDeg:0.0}°";
        _logger.LogInformation("① {Msg}", msg);
        context.Progress?.Invoke(msg);
        return StepResult.Ok(msg);
    }
}
