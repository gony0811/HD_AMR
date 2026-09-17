using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vision;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑱ᶜ 코너3 검사 수행 — 3면(135°·90°·90°) 코너를 고정 티칭 슬롯(corner3.{L|R}.*)으로 직접 순회한다.
///
/// CORNER3 은 평탄면 정렬 체인(③~⑯⁺)을 적용할 수 없어(레이저/비드 정렬 전제가 단일 평면),
/// 정렬 없이 현장 티칭 자세를 그대로 재현한다 — AMR 정차 재현성(ACS 정차점 산출이 코너별 동일)이 전제.
///
/// 순회: 접근(via, 촬영 없음) → 면1(135°) → 면2(90°) → 면3(90°) → 복귀(via).
/// 각 면에서 진동 흡수 대기 후 비전 CAPTURE_REQ(SurfaceType=Corner 고정)를
/// 전송한다. 비전 실패는 ⑱과 동일하게 중단하지 않고 집계만 하며(<see cref="SequenceContext.VisionFailRatioMax"/>
/// 초과 시 스텝 Fail), MoveL 실패는 즉시 중단한다.
///
/// 좌/우 거울은 별도 티칭 2세트(corner3.L.* / corner3.R.*) — side 는 오케스트레이터가
/// <see cref="SequenceContext.CornerSide"/> 로 전달(wall_code P*→L, S*→R). 작업물 추종 티칭(UserFrame)도
/// <see cref="CobotInspectionMoveStep.ComputeTargetPoseAsync"/> 재사용으로 수용한다.
/// </summary>
public class CornerInspectionRunStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly VisionInterfaceService _vision;
    private readonly ILogger<CornerInspectionRunStep> _logger;

    /// <summary>MoveL 가속/오버라이드 — ⑱(InspectionRunStep)과 동일값.</summary>
    private const double MoveAcc = 100.0;
    private const double MoveOvl = 100.0;

    /// <summary>면 도달 후 진동 흡수 대기 / 비전 응답 대기 — 프로필이 없는 경로라 고정값
    /// (LINE 은 프로필 SettleDelaySec/DelaySec 사용). 튜닝 필요 시 파라미터화한다.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan VisionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>순회 슬롯 접미사 — via 2점은 촬영하지 않는다.</summary>
    private static readonly (string Suffix, bool Capture)[] Route =
    {
        ("approach", false),
        ("face1", true),
        ("face2", true),
        ("face3", true),
        ("retreat", false),
    };

    public CornerInspectionRunStep(
        CobotService cobot, VisionInterfaceService vision, ILogger<CornerInspectionRunStep> logger)
    {
        _cobot = cobot;
        _vision = vision;
        _logger = logger;
    }

    public string Key => "cornerInspectionRun";
    public string DisplayName => "코너3 검사 수행 (티칭 슬롯 순회)";
    public int DefaultOrder => 1250;

    private static string SlotKey(string side, string suffix) => $"corner3.{side}.{suffix}";

    public StepValidation Validate(SequenceContext context)
    {
        // 비코너 경로(LINE/CROSS 풀시퀀스, UI 풀오토 — CornerSide 미설정)에서는 no-op 으로 통과한다.
        // 이 스텝은 레지스트리에 상시 등록되므로, 여기서 걸러야 기존 풀시퀀스가 깨지지 않는다.
        if (context.CornerSide is null)
            return StepValidation.Ok();

        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        var side = context.CornerSide;
        foreach (var (suffix, _) in Route)
        {
            var key = SlotKey(side, suffix);
            if (!context.Positions.TryGetValue(key, out var pos))
                return StepValidation.Fail($"티칭 슬롯 '{key}' 없음 — Teaching 시드 확인 필요.");
            if (!pos.IsTaught)
                return StepValidation.Fail($"티칭 슬롯 '{key}' 미티칭 — Teaching에서 먼저 저장하세요.");
        }
        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (context.CornerSide is not { } side)
            return StepResult.Ok("건너뜀 — CornerSide 미설정(비코너 경로).");

        // 검사 세션 1회 = Run ID 1개, 면 캡처 1건 = Task ID 1개 — ⑱과 동일한 v3 식별 체계.
        var runId = Guid.NewGuid();
        _logger.LogInformation(
            "⑱ᶜ 코너3 검사 시작: side={Side}, Run {RunId}, SurfaceID 0x{Sid:X2}, vel={Vel}%" +
            (context.AcsOrderId is not null ? " (ACS order={OrderId}, jobRef={JobRef})" : ""),
            side, runId, context.InspectionSurfaceId, context.Velocity,
            context.AcsOrderId, context.AcsJobRef);

        int captured = 0, visOk = 0, visFail = 0;

        foreach (var (suffix, capture) in Route)
        {
            ct.ThrowIfCancellationRequested();

            var key = SlotKey(side, suffix);
            var pos = context.Positions[key];   // Validate 가 존재/티칭을 보장

            var (target, _) = await CobotInspectionMoveStep.ComputeTargetPoseAsync(_cobot, pos, ct);
            context.Progress?.Invoke($"코너3 [{key}] 이동…");
            var rc = await _cobot.Rpc.MoveLAsync(target, tool: pos.Tool, user: 0,
                vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
            if (rc != 0)
                return StepResult.Fail(
                    $"'{key}' 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — 순회 중단.");

            if (!capture) continue;

            await Task.Delay(Settle, ct);
            captured++;

            var surfaceType = SurfaceType.Corner;
            var taskId = Guid.NewGuid();
            var jobRef = context.AcsJobRef is not null
                ? $"{context.AcsJobRef}-C{captured}"
                : $"CORNER3-{side}-C{captured}";
            var data = CaptureReqPayload.Build(runId, taskId, jobRef, surfaceType,
                (ushort)context.InspectionSurfaceId, 0, 0);

            var outcome = await _vision.Client.RequestCaptureAsync(data, VisionTimeout, ct);
            if (outcome.Success) visOk++;
            else
            {
                visFail++;
                _logger.LogWarning("⑱ᶜ 면{Idx} 비전 실패: task={Task}, sent={Sent}, responded={Resp}, code={Code}",
                    captured, taskId, outcome.Sent, outcome.Responded,
                    outcome.Code is { } c ? ResultCodeNames.NameOf((ushort)c) : "—");
            }
        }

        var msg =
            $"코너3 검사 완료 — side={side}, 면 {captured}점, 비전 OK {visOk}/{captured}" +
            (visFail > 0 ? $" (실패 {visFail})" : "") +
            $" [WallID 0x{context.InspectionSurfaceId:X2}, Run {runId}].";
        _logger.LogInformation("⑱ᶜ {Msg}", msg);

        // ACS 경로: 비전 실패율 상한 초과 시 스텝 실패 승격(⑱과 동일 정책).
        if (context.VisionFailRatioMax is { } maxRatio && captured > 0)
        {
            var failRatio = (double)visFail / captured;
            if (failRatio > maxRatio)
                return StepResult.Fail(
                    $"비전 실패율 초과 — {visFail}/{captured} ({failRatio:P0} > 허용 {maxRatio:P0}). {msg}");
        }

        return StepResult.Ok(msg);
    }
}
