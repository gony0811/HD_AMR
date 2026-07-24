using System.Text.Json;
using HD_AMR.Communication;
using HD_AMR.Communication.Vision;
using HD_AMR.Data.Entities;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑱ 검사 수행 — ⑯⁺(작업물 좌표계 등록) 이후, 저장된 티칭설정(<see cref="InspectionProfile"/>)의
/// 경유점을 <b>등록된 작업물 좌표계 기준</b>으로 순회하며 각 점에서 비전 CAPTURE_REQ 를 보낸다.
/// /inspection 페이지의 <c>RunWaypoints</c> 와 동일한 이동+캡처 패턴을 시퀀스 스텝으로 옮긴 것이다.
///
/// 흐름:
///   1) 작업물 좌표계 번호 = <see cref="WObjPointStep.WObjIdKey"/> 파라미터(⑩/⑯/⑯⁺가 등록에 쓴 값).
///      해당 좌표계가 컨트롤러에 등록돼 있는지 <see cref="FairinoRpcClient.GetWObjCoordAsync"/> 로 확인.
///   2) 그 좌표계 <b>원점</b>으로 MoveL — 검사 시작 스테이징. 자세의 RZ 는 <b>현재 툴 RZ(프레임 기준 rz0)를
///      유지</b>한다 — 등록 프레임의 X축(비드1→비드2)이 툴 X와 반대면 프레임이 툴 대비 RZ 180° 회전 상태라,
///      rz=0 을 명령하면 툴이 180° 돌아가는 문제가 있었음(실기). 회전 없이 현재 자세로 바로 검사한다.
///   3) 프로필 경유점을 순회: 각 점을 프레임 기준 pose=[x,0,z, 0, ±θ, rz0] 로 MoveL 후,
///      진동 흡수 대기 → surface type(θ 로 재판정) 과 Surface ID 로 CAPTURE_REQ 전송/응답 대기.
///      틸트 부호: ZYX 규약에서 Ry_frame(θ)·Rz(rz0) = Rz(rz0)·Ry(±θ) — rz0≈±180 이면 ry 부호가 반전되므로
///      tiltSign = sign(cos rz0) 을 곱한다.
///   4) 종료 시(성공/실패/취소 모두) 무이동 MoveL(user:0)로 <b>활성 작업물 좌표계를 0(베이스)으로 복귀</b>한다.
///
/// 프레임: MoveL 의 tool = 시퀀스 페이지 상단 공구 번호(<see cref="SequenceContext.Tool"/>),
///         user = 위 작업물 좌표계 번호. 속도 = 시퀀스 페이지 속도(<see cref="SequenceContext.Velocity"/>).
/// 비전 실패/무응답은 /inspection 페이지와 동일하게 <b>중단하지 않고</b> 집계만 하고 다음 점으로 진행한다.
/// (MoveL 실패는 즉시 중단.)
/// </summary>
public class InspectionRunStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly VisionInterfaceService _vision;
    private readonly DrawingService _drawing;
    private readonly ParameterService _param;
    private readonly ILogger<InspectionRunStep> _logger;

    /// <summary>MoveL 가속/오버라이드 — /inspection 페이지 RunWaypoints 와 동일값.</summary>
    private const double MoveAcc = 100.0;
    private const double MoveOvl = 100.0;

    public InspectionRunStep(
        CobotService cobot, VisionInterfaceService vision, DrawingService drawing,
        ParameterService param, ILogger<InspectionRunStep> logger)
    {
        _cobot = cobot;
        _vision = vision;
        _drawing = drawing;
        _param = param;
        _logger = logger;
    }

    public string Key => "inspectionRun";
    public string DisplayName => "검사 수행 (도면 순회)";
    public int DefaultOrder => 1200;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");
        if (context.InspectionProfileId <= 0)
            return StepValidation.Fail("티칭설정 미선택 — 파라미터 칸에서 도면·티칭설정을 선택하세요.");
        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        // ── 작업물 좌표계 번호 (⑩/⑯/⑯⁺ 등록과 동일 파라미터) ──────────
        var wobjId = (int)(await GetWObjIdAsync());
        if (wobjId is < 0 or > 14)
            return StepResult.Fail($"'{WObjPointStep.WObjIdKey}' 범위 초과 ({wobjId}) — 0~14 로 설정하세요.");

        // ── 티칭설정 로드 + 경유점 역직렬화 ─────────────────────────────
        var profile = await _drawing.GetProfileAsync(context.InspectionProfileId, ct);
        if (profile is null)
            return StepResult.Fail($"티칭설정(id={context.InspectionProfileId})을 찾을 수 없습니다 — 다시 선택하세요.");

        List<InspectionWaypoint> waypoints;
        try
        {
            waypoints = JsonSerializer.Deserialize<List<InspectionWaypoint>>(profile.WaypointsJson) ?? new();
        }
        catch (JsonException ex)
        {
            return StepResult.Fail($"티칭설정 '{profile.Name}' 경유점 파싱 실패: {ex.Message}");
        }
        if (waypoints.Count < 2)
            return StepResult.Fail($"티칭설정 '{profile.Name}' 경유점이 부족합니다({waypoints.Count}개, 2개 이상 필요).");

        // ── 작업물 좌표계 등록 확인 ─────────────────────────────────────
        double[] frame;
        try
        {
            frame = await _cobot.Rpc.GetWObjCoordAsync(wobjId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return StepResult.Fail($"작업물 좌표계 #{wobjId} 확인 실패 — {ex.Message}");
        }
        if (frame.Length != 6 || frame.All(v => v == 0))
            return StepResult.Fail(
                $"작업물 좌표계 #{wobjId} 가 등록되지 않았습니다(원점=0) — " +
                "⑯⁺ 작업물 좌표계 등록 단계를 먼저 실행하세요.");

        // ── 현재 자세의 프레임 기준 RZ — 회전 없이 검사하기 위한 유지값 ──
        // 등록 프레임 X(비드1→비드2)가 툴 X와 반대면 rz0 ≈ ±180°. rz=0 을 명령하면 툴이 180° 회전한다.
        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var rel = FrameMath.ToFrame(cur, frame);
        var rz0 = rel[5];
        // ZYX 규약: Ry_frame(θ)·Rz(rz0) = Rz(rz0)·Ry(±θ) — rz0≈±180 이면 틸트 부호 반전.
        var tiltSign = Math.Cos(rz0 * Math.PI / 180.0) >= 0 ? 1.0 : -1.0;

        _logger.LogInformation(
            "⑱ 검사 수행 시작: 도면 {Draw}, 티칭설정 '{Prof}'({N}점), wobj #{Id} 원점 [{X:0.0},{Y:0.0},{Z:0.0}], " +
            "tool={Tool}, vel={Vel}%, SurfaceID 0x{Sid:X2}, RZ 유지={Rz0:0.0}° (틸트부호 {Sign:+0;-0})",
            profile.DrawingId, profile.Name, waypoints.Count, wobjId,
            frame[0], frame[1], frame[2], context.Tool, context.Velocity, context.InspectionSurfaceId,
            rz0, tiltSign);

        try
        {
            // ── 작업물 좌표계 원점으로 이동 — RZ 는 현재값 유지(회전 없음) ──
            var originRc = await _cobot.Rpc.MoveLAsync(
                new[] { 0.0, 0.0, 0.0, 0.0, 0.0, rz0 }, tool: context.Tool, user: wobjId,
                vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
            if (originRc != 0)
                return StepResult.Fail(
                    $"작업물 좌표계 #{wobjId} 원점 이동 실패 (rc={originRc}){FairinoErrorCodes.Suffix(originRc)}.");

            // ── 경유점 순회 + 비전 캡처 ────────────────────────────────────
            var visionTimeout = TimeSpan.FromSeconds(Math.Max(0, profile.DelaySec));
            var settle = TimeSpan.FromSeconds(Math.Max(0, profile.SettleDelaySec));
            int moved = 0, skipped = 0, visOk = 0, visFail = 0;

            for (var i = 0; i < waypoints.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var w = waypoints[i];
                // th_max 초과 점은 /inspection 과 동일하게 제외.
                if (Math.Abs(w.Theta) > profile.ThMax) { skipped++; continue; }

                var pose = new[] { w.X, 0.0, w.Z, 0.0, tiltSign * w.Theta, rz0 };
                var rc = await _cobot.Rpc.MoveLAsync(pose, tool: context.Tool, user: wobjId,
                    vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
                if (rc != 0)
                    return StepResult.Fail(
                        $"경유점 #{i + 1} 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — {moved}점 이동 후 중단.");
                moved++;

                if (settle > TimeSpan.Zero)
                    await Task.Delay(settle, ct);

                // surface type: 프로필 로드 후 페이지와 동일 규칙(|θ| ≥ 코로게이션 판정각 → Corrugation).
                var surfaceType = Math.Abs(w.Theta) >= profile.CorrugThresholdDeg
                    ? SurfaceType.Corrugation
                    : SurfaceType.Flat;
                var data = CaptureReqPayload.Build(surfaceType, (ushort)context.InspectionSurfaceId,
                    (int)Math.Round(w.X), (int)Math.Round(w.Z));

                var outcome = await _vision.Client.RequestCaptureAsync(data, visionTimeout, ct);
                if (outcome.Success) visOk++;
                else
                {
                    visFail++;
                    _logger.LogWarning("⑱ 경유점 #{Idx} 비전 실패: sent={Sent}, responded={Resp}, code={Code}",
                        i + 1, outcome.Sent, outcome.Responded,
                        outcome.Code is { } c ? ResultCodeNames.NameOf((ushort)c) : "—");
                }
            }

            var msg =
                $"검사 수행 완료 — 티칭설정 '{profile.Name}', 이동 {moved}점" +
                (skipped > 0 ? $"(θ 초과 {skipped}점 제외)" : "") +
                $", 비전 OK {visOk}/{moved}" +
                (visFail > 0 ? $" (실패 {visFail})" : "") +
                $" [wobj #{wobjId}, tool {context.Tool}, SurfaceID 0x{context.InspectionSurfaceId:X2}]. " +
                "활성 작업물 좌표계 0 복귀.";
            _logger.LogInformation("⑱ {Msg}", msg);
            return StepResult.Ok(msg);
        }
        finally
        {
            // user=wobjId MoveL 로 활성 프레임이 바뀐 상태 — 성공/실패/취소 모두에서 0(베이스)으로 복귀.
            await RestoreBaseFrameAsync(context);
        }
    }

    /// <summary>무이동 MoveL(user:0)로 컨트롤러 활성 작업물 좌표계를 베이스(0)로 복귀.
    /// 취소 중에도 실행돼야 하므로 CancellationToken.None 사용. 실패는 경고 로그만.</summary>
    private async Task RestoreBaseFrameAsync(SequenceContext context)
    {
        try
        {
            var end = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, CancellationToken.None);
            var rc = await _cobot.Rpc.MoveByToolOffsetAsync(end, user: 0, new double[6],
                tool: context.Tool, vel: context.Velocity, ct: CancellationToken.None);
            if (rc == 0)
                _logger.LogInformation("⑱ 활성 작업물 좌표계 0(베이스) 복귀 완료.");
            else
                _logger.LogWarning("⑱ 활성 작업물 좌표계 0 복귀 실패 (rc={Rc}){Sfx} — 티치펜던트에서 확인하세요.",
                    rc, FairinoErrorCodes.Suffix(rc));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⑱ 활성 작업물 좌표계 0 복귀 중 예외 — 티치펜던트에서 확인하세요.");
        }
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjPointStep.WObjIdKey) ?? 1; }
        catch { return 1; }
    }
}
