using System.Text.Json;
using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑱ 검사 수행 — ⑯⁺에서 계산한 내부 작업물 좌표계를 이용해 저장된 티칭설정의 경유점을
/// 선택된 툴 TCP의 BASE 목표 포즈로 변환하여 순회한다. 컨트롤러 작업물 좌표계는 사용하지 않는다.
/// /inspection 페이지의 <c>RunWaypoints</c> 와 동일한 이동+캡처 패턴을 시퀀스 스텝으로 옮긴 것이다.
///
/// 흐름:
///   1) 실행 컨텍스트에서 BASE 기준 내부 작업물 프레임을 읽는다.
///   2) 그 좌표계 <b>원점</b>을 BASE TCP 목표로 변환해 MoveL — 검사 시작 스테이징. 자세의 RZ 는
///      <b>현재 툴 RZ(프레임 기준 rz0)를
///      유지</b>한다 — 등록 프레임의 X축(비드1→비드2)이 툴 X와 반대면 프레임이 툴 대비 RZ 180° 회전 상태라,
///      rz=0 을 명령하면 툴이 180° 돌아가는 문제가 있었음(실기). 회전 없이 현재 자세로 바로 검사한다.
///   3) 프로필 경유점을 순회: 각 점의 프레임 pose=[x,0,z, 0, ±θ, rz0]를 BASE TCP pose로 변환해 MoveL 후,
///      진동 흡수 대기 → surface type(θ 로 재판정) 과 Surface ID 로 CAPTURE_REQ 전송/응답 대기.
///      틸트 부호: ZYX 규약에서 Ry_frame(θ)·Rz(rz0) = Rz(rz0)·Ry(±θ) — rz0≈±180 이면 ry 부호가 반전되므로
///      tiltSign = sign(cos rz0) 을 곱한다.
///
/// 프레임: MoveL의 tool = 시퀀스 검사 공구(<see cref="SequenceContext.Tool"/>), user = 0(BASE) 고정.
/// 컨트롤러는 내부 작업물 프레임을 알지 못하고 최종 변환된 TCP 목표만 받는다.
/// 비전 실패/무응답은 /inspection 페이지와 동일하게 <b>중단하지 않고</b> 집계만 하고 다음 점으로 진행한다.
/// (MoveL 실패는 즉시 중단.)
/// </summary>
public class InspectionRunStep : ISequenceStep
{
    private readonly bool _dryRun;
    private readonly CobotService _cobot;
    private readonly VisionInterfaceService _vision;
    private readonly DrawingService _drawing;
    private readonly ParameterService _param;
    private readonly ILogger<InspectionRunStep> _logger;

    /// <summary>비전 CAPTURE_REQ 응답 대기 기본값(초) — 프로필 DelaySec 미설정(0 이하) 시. 검사 프로파일 화면 기본값과 동일.</summary>
    public const double DefaultVisionTimeoutSec = 10.0;

    /// <summary>MoveL 가속/오버라이드 — /inspection 페이지 RunWaypoints 와 동일값.</summary>
    private const double MoveAcc = 100.0;
    private const double MoveOvl = 100.0;

    /// <param name="dryRun">true 면 경유점을 <b>이동만</b> 하고 비전 CAPTURE_REQ 를 보내지 않는다(드라이런).
    /// DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public InspectionRunStep(
        bool dryRun, CobotService cobot, VisionInterfaceService vision, DrawingService drawing,
        ParameterService param, ILogger<InspectionRunStep> logger)
    {
        _dryRun = dryRun;
        _cobot = cobot;
        _vision = vision;
        _drawing = drawing;
        _param = param;
        _logger = logger;
    }

    public string Key => _dryRun ? "inspectionRunDry" : "inspectionRun";
    public string DisplayName => _dryRun ? "검사 수행 (도면 순회·비전 없음)" : "검사 수행 (도면 순회)";
    public int DefaultOrder => _dryRun ? 1210 : 1200;

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

        // 경유점 소스: 티칭 프로필 저장분(LINE 도면 솎기 / CROSS3·CROSS4 X-Y 캡처 교시).
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
            return StepResult.Fail(
                $"티칭설정 '{profile.Name}' 경유점이 부족합니다({waypoints.Count}개, 2개 이상 필요).");

        // ── 애플리케이션 내부 작업물 좌표계 확인 ─────────────────────────
        if (!context.Bag.TryGetValue(InternalWorkpieceFrame.BagKey, out var frameValue)
            || frameValue is not double[] { Length: 6 } frame)
            return StepResult.Fail(
                "내부 작업물 좌표계가 없습니다 — 점1(Top)·점2(X+) 기록과 ⑯⁺ 계산 단계를 먼저 실행하세요.");

        // ── 현재 자세의 프레임 기준 RZ — 회전 없이 검사하기 위한 유지값(WObjAttitude 참조) ──
        // 등록 프레임 X(비드1→비드2)가 툴 X와 반대면 rz0 ≈ ±180°. rz=0 을 명령하면 툴이 180° 회전한다.
        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var attitude = Inspection.WObjAttitude.FromCurrent(cur, frame);
        var rz0 = attitude.Rz0;
        var tiltSign = attitude.TiltSign;

        _logger.LogInformation(
            "⑱ 검사 수행 시작: 도면 {Draw}, 티칭설정 '{Prof}'({N}점), 내부 wobj #{Id} 원점 [{X:0.0},{Y:0.0},{Z:0.0}], " +
            "tool={Tool}, vel={Vel}%, SurfaceID 0x{Sid:X2}, RZ 유지={Rz0:0.0}° (틸트부호 {Sign:+0;-0})",
            profile.DrawingId, profile.Name, waypoints.Count, wobjId,
            frame[0], frame[1], frame[2], context.Tool, context.Velocity, context.InspectionSurfaceId,
            rz0, tiltSign);

        // ── 작업물 좌표계 원점으로 이동 — RZ 는 현재값 유지(회전 없음) ──
        var originTarget = InternalWorkpieceFrame.ToBasePose(
            new[] { 0.0, 0.0, 0.0, 0.0, 0.0, rz0 }, frame);
        var originRc = await _cobot.Rpc.MoveLAsync(
            originTarget, tool: context.Tool, user: 0,
            vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
        if (originRc != 0)
            return StepResult.Fail(
                $"내부 작업물 좌표계 원점 이동 실패 (rc={originRc}){FairinoErrorCodes.Suffix(originRc)}.");

        // ── 경유점 순회 + 비전 캡처 ────────────────────────────────────
        // DelaySec = CAPTURE_REQ 응답 대기 제한(초). 0 이하(검사 프로파일 화면에 입력 칸이 없던 구버전 저장분)면
        // 즉시 타임아웃으로 모든 촬영이 실패 집계되므로 기본값으로 대체한다.
        var visionTimeoutSec = profile.DelaySec > 0 ? profile.DelaySec : DefaultVisionTimeoutSec;
        if (profile.DelaySec <= 0)
            _logger.LogWarning("⑱ 티칭설정 '{Prof}' 비전 응답 대기 미설정(DelaySec={Delay}) — 기본 {Default}초 사용",
                profile.Name, profile.DelaySec, DefaultVisionTimeoutSec);
        var visionTimeout = TimeSpan.FromSeconds(visionTimeoutSec);
        var settle = TimeSpan.FromSeconds(Math.Max(0, profile.SettleDelaySec));
        int moved = 0, skipped = 0, visOk = 0, visFail = 0;

        // v3: 검사 세션 1회 = Run ID 1개(GUID). 각 경유점 캡처 = Task ID 1개.
        // ACS 연동 실행이면 jobRef 는 작업지시(VDA5050 action jobRef) 기반으로 발급하고,
        // 단독 실행에서는 현행대로 "P{프로필}-W{순번}" 자기발급.
        // 이 식별자는 CAPTURE_REQ 로 비전에 전달되어, 결과 에코를 통해 엔터프라이즈가 ACS 진행현황과 매칭한다.
        // runId 는 로깅/추적용 내부 상관값(프레임 미탑재). CAPTURE_REQ 식별자는 taskId 하나.
        var runId = Guid.NewGuid();
        // v3.2: taskId·attempt 는 ACS 발급(VDA 액션에서 주입). 미연동/수동 실행이면 폴백(Empty/1).
        // 검사 실행 1건 = TASK 1개(용접선 1구간)이므로 taskId·attempt 는 경유점 전체에 공통이며,
        // captureSeq 만 촬영마다 1부터 증가시킨다(로봇 발번, §3.4).
        var taskId  = context.AcsTaskId ?? Guid.Empty;
        var attempt = context.AcsAttempt ?? (byte)1;
        ushort captureSeq = 0;
        if (context.AcsOrderId is not null)
            _logger.LogInformation("⑱ 검사 Run={RunId}, Task={TaskId}, attempt={Attempt} (ACS order={OrderId}, action={ActionId}, jobRef={JobRef})",
                runId, taskId, attempt, context.AcsOrderId, context.AcsActionId, context.AcsJobRef);
        else
            _logger.LogInformation("⑱ 검사 Run={RunId}, Task={TaskId}, attempt={Attempt}", runId, taskId, attempt);

        for (var i = 0; i < waypoints.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var w = waypoints[i];
            // th_max 초과 점은 /inspection 과 동일하게 제외 (절대 6-DOF 모드는 θ=절대 Ry 라 필터 미적용).
            if (!profile.PoseAbsolute && Math.Abs(w.Theta) > profile.ThMax) { skipped++; continue; }

            // 자세 해석 — profile.PoseAbsolute:
            //  · false(상대 틸트): θ/RzDeg 를 프레임 유지값 rz0 에 합성. Rx=0 (LINE 도면 솎기·CROSS 패턴).
            //  · true(절대 6-DOF): 캡처한 X/Y/Z/Rx/Ry(θ)/Rz 를 작업물 좌표계 기준 그대로 명령
            //    (/inspection-points 조그+캡처 — 코로게이션 법선 추종). rz0/tiltSign 합성 금지.
            var pose = profile.PoseAbsolute
                ? new[] { w.X, w.Y, w.Z, w.RxDeg, w.Theta, w.RzDeg }
                : attitude.Pose(w.X, w.Y, w.Z, w.Theta, w.RzDeg);
            var targetBase = InternalWorkpieceFrame.ToBasePose(pose, frame);
            // 절대 모드는 RzDeg 가 프레임 X(검사 이동 방향) 기준이라 역방향 검사에서 J6 가 180° 뒤집힌다 — 보정.
            if (profile.PoseAbsolute)
                targetBase = attitude.KeepTwist(targetBase);
            // 경유점 순회(검사 스캔)만 검사 속도 적용 — 원점 이동 등 접근성 모션은 이동 속도(Velocity).
            var rc = await _cobot.Rpc.MoveLAsync(targetBase, tool: context.Tool, user: 0,
                vel: context.InspectVelocity ?? context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
            if (rc != 0)
                return StepResult.Fail(
                    $"경유점 #{i + 1} 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — {moved}점 이동 후 중단.");
            moved++;

            if (settle > TimeSpan.Zero)
                await Task.Delay(settle, ct);

            // 드라이런: 경유점 이동만 하고 비전 CAPTURE_REQ 는 보내지 않는다(좌표·경로 검증용).
            if (_dryRun)
                continue;

            // surface type 우선순위: 경유점 수동 지정 > |θ| 자동 규칙(구 도면 솎기 프로필).
            // (레시피 SurfaceOverride 는 경유점 단위 지정으로 일원화되며 폐기 — 2026-09-18.)
            var surfaceType = w.SurfaceManual
                ? (SurfaceType)w.Surface
                : Math.Abs(w.Theta) >= profile.CorrugThresholdDeg
                    ? SurfaceType.Corrugation
                    : SurfaceType.Flat;
            // v3.2: 코봇 wobj pose → 면-로컬 (u,v,h). 축·부호는 wobj 티칭 규약(§5)에 흡수(FaceLocalMapper).
            var wallId = (ushort)context.InspectionSurfaceId;
            var (u, v, h) = FaceLocalMapper.ToFaceLocal(wallId, w.X, w.Y, w.Z);
            captureSeq++;
            var data = CaptureReqPayload.Build(surfaceType, wallId, u, v, h, taskId, attempt, captureSeq);
            var outcome = await _vision.Client.RequestCaptureAsync(data, visionTimeout, ct);
            if (outcome.Success) visOk++;
            else
            {
                visFail++;
                _logger.LogWarning("⑱ 경유점 #{Idx} 비전 실패: task={Task}, seq={Seq}, sent={Sent}, responded={Resp}, code={Code}",
                    i + 1, taskId, captureSeq, outcome.Sent, outcome.Responded,
                    outcome.Code is { } c ? ResultCodeNames.NameOf((ushort)c) : "—");
            }
        }

        var msg =
            (_dryRun ? "검사 수행 완료(드라이런·비전 없음) — " : "검사 수행 완료 — ") +
            $"티칭설정 '{profile.Name}', 이동 {moved}점" +
            (skipped > 0 ? $"(θ 초과 {skipped}점 제외)" : "") +
            (_dryRun ? "" : $", 비전 OK {visOk}/{moved}" + (visFail > 0 ? $" (실패 {visFail})" : "")) +
            $" [내부 wobj #{wobjId}→BASE/user 0, tool {context.Tool}, WallID 0x{context.InspectionSurfaceId:X2}, Run {runId}].";
        _logger.LogInformation("⑱ {Msg}", msg);

        // ACS 경로: 비전 실패율 상한 초과 시 스텝 실패로 승격(→ 액션 FAILED + inspectionFailed, §6.4 재시도 정책).
        // UI 단독 실행(VisionFailRatioMax=null)은 현행대로 집계만 하고 성공 반환. 드라이런은 비전이 없어 판정 생략.
        if (!_dryRun && context.VisionFailRatioMax is { } maxRatio && moved > 0)
        {
            var failRatio = (double)visFail / moved;
            if (failRatio > maxRatio)
                return StepResult.Fail(
                    $"비전 실패율 초과 — {visFail}/{moved} ({failRatio:P0} > 허용 {maxRatio:P0}). {msg}");
        }

        return StepResult.Ok(msg);
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjPointStep.WObjIdKey) ?? 1; }
        catch { return 1; }
    }
}
