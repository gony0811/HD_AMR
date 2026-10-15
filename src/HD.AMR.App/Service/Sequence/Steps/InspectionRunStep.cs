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
///   2) <b>첫 경유점</b>(Top 높이)을 BASE TCP 목표로 변환해 MoveL — 검사 시작 스테이징. 프레임 원점(Top Z−33.8
///      바닥 가상점)으로는 가지 않는다 — 광축 방향으로 작업물에 파고들어 기구물과 충돌했다(실기). 자세의 RZ 는
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
///
/// 일시정지(통신 단절 사양 §3·§4.3, <see cref="MissionPauseGate"/>): 진행 중 경유점 MoveL 은 끝까지 수행하고
/// 다음 촬영·이동으로 넘어가지 않는다. 재개 시 <b>정지 직전 경유점부터 다시 촬영</b>한다 — 정지 순간의
/// 촬영 성공 여부가 불확실하므로 같은 점의 결과는 마지막 촬영으로 덮어써 집계한다.
/// </summary>
public class InspectionRunStep : ISequenceStep
{
    private readonly bool _dryRun;
    private readonly CobotService _cobot;
    private readonly VisionInterfaceService _vision;
    private readonly DrawingService _drawing;
    private readonly ParameterService _param;
    private readonly MissionPauseGate _pauseGate;
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
        ParameterService param, MissionPauseGate pauseGate, ILogger<InspectionRunStep> logger)
    {
        _dryRun = dryRun;
        _pauseGate = pauseGate;
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
                "내부 작업물 좌표계가 없습니다 — 점1(Top)·점2(X+) 기록과 '내부 작업물 좌표계 계산' 단계를 먼저 실행하세요.");

        // ── 현재 자세의 프레임 기준 RZ — 회전 없이 검사하기 위한 유지값(WObjAttitude 참조) ──
        // 등록 프레임 X(비드1→비드2)가 툴 X와 반대면 rz0 ≈ ±180°. rz=0 을 명령하면 툴이 180° 회전한다.
        var cur = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var attitude = Inspection.WObjAttitude.FromCurrent(cur, frame);
        var rz0 = attitude.Rz0;
        var tiltSign = attitude.TiltSign;

        _logger.LogInformation(
            "[검사 수행] 시작: 도면 {Draw}, 티칭설정 '{Prof}'({N}점), 내부 wobj #{Id} 원점 [{X:0.0},{Y:0.0},{Z:0.0}], " +
            "tool={Tool}, vel={Vel}%, SurfaceID 0x{Sid:X2}, RZ 유지={Rz0:0.0}° (틸트부호 {Sign:+0;-0})",
            profile.DrawingId, profile.Name, waypoints.Count, wobjId,
            frame[0], frame[1], frame[2], context.Tool, context.Velocity, context.InspectionSurfaceId,
            rz0, tiltSign);

        // th_max 초과 점은 /inspection 과 동일하게 제외 (절대 6-DOF 모드는 θ=절대 Ry 라 필터 미적용).
        bool IsSkipped(InspectionWaypoint w) => !profile.PoseAbsolute && Math.Abs(w.Theta) > profile.ThMax;

        // 자세 해석 — profile.PoseAbsolute:
        //  · false(상대 틸트): θ/RzDeg 를 프레임 유지값 rz0 에 합성. Rx=0 (LINE 도면 솎기·CROSS 패턴).
        //  · true(절대 6-DOF): 캡처한 X/Y/Z/Rx/Ry(θ)/Rz 를 작업물 좌표계 기준 그대로 명령
        //    (/inspection-points 조그+캡처 — 코로게이션 법선 추종). rz0/tiltSign 합성 금지.
        double[] ToTargetBase(InspectionWaypoint w)
        {
            var pose = profile.PoseAbsolute
                ? new[] { w.X, w.Y, w.Z, w.RxDeg, w.Theta, w.RzDeg }
                : attitude.Pose(w.X, w.Y, w.Z, w.Theta, w.RzDeg);
            var targetBase = InternalWorkpieceFrame.ToBasePose(pose, frame);
            // 절대 모드는 RzDeg 가 프레임 X(검사 이동 방향) 기준이라 역방향 검사에서 J6 가 180° 뒤집힌다 — 보정.
            return profile.PoseAbsolute ? attitude.KeepTwist(targetBase) : targetBase;
        }

        // ── 첫 경유점으로 바로 이동(검사 시작 스테이징) — RZ 는 현재값 유지(회전 없음) ──
        // 과거엔 프레임 원점(z=0)으로 먼저 갔는데, 원점은 Top 에서 작업물 Z− 로 33.8mm 내린 코로게이션 바닥
        // 가상점이라 Peak2→Peak1 횡이동 중 툴이 작업물 쪽으로 파고들어 기구물과 충돌했다(실기). 첫 경유점은
        // Top 높이이므로 광축(Z) 방향 진입 없이 그 자리로 간다.
        var firstIdx = waypoints.FindIndex(w => !IsSkipped(w));
        if (firstIdx < 0)
            return StepResult.Fail(
                $"티칭설정 '{profile.Name}' 경유점이 모두 th_max({profile.ThMax}°) 초과로 제외되었습니다.");
        // 절대 모드는 첫 경유점에서 툴을 광축 둘레로 0/180° 중 하나로 맞추는데, 그 회전은 J6 안전 범위
        // (물리 90°~180°) 안에 머물러야 한다 — 밖으로 돌면 센서 헤드–링크암 간섭(실기). IK 로 미리 확인한다.
        if (profile.PoseAbsolute)
        {
            var twist = await SelectSafeTwistAsync(context, waypoints[firstIdx], frame, attitude, ct);
            if (twist.Error is { } err)
                return StepResult.Fail(err);
            attitude = attitude with { TwistOverrideDeg = twist.TwistDeg };
        }

        _logger.LogInformation("[검사 수행] 첫 경유점 #{Idx} 로 스테이징 이동 (프레임 z={Z:0.0}mm)",
            firstIdx + 1, waypoints[firstIdx].Z);
        await _pauseGate.WaitWhilePausedAsync(ct);
        var stageRc = await _cobot.Rpc.MoveLAsync(
            ToTargetBase(waypoints[firstIdx]), tool: context.Tool, user: 0,
            vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
        if (stageRc != 0)
            return StepResult.Fail(
                $"첫 경유점 #{firstIdx + 1} 스테이징 이동 실패 (rc={stageRc}){FairinoErrorCodes.Suffix(stageRc)}.");

        // ── 경유점 순회 + 비전 캡처 ────────────────────────────────────
        // DelaySec = CAPTURE_REQ 응답 대기 제한(초). 0 이하(검사 프로파일 화면에 입력 칸이 없던 구버전 저장분)면
        // 즉시 타임아웃으로 모든 촬영이 실패 집계되므로 기본값으로 대체한다.
        var visionTimeoutSec = profile.DelaySec > 0 ? profile.DelaySec : DefaultVisionTimeoutSec;
        if (profile.DelaySec <= 0)
            _logger.LogWarning("[검사 수행] 티칭설정 '{Prof}' 비전 응답 대기 미설정(DelaySec={Delay}) — 기본 {Default}초 사용",
                profile.Name, profile.DelaySec, DefaultVisionTimeoutSec);
        var visionTimeout = TimeSpan.FromSeconds(visionTimeoutSec);
        var settle = TimeSpan.FromSeconds(Math.Max(0, profile.SettleDelaySec));
        // 집계는 경유점 인덱스 단위 — 일시정지 재개 시 같은 점을 다시 이동·촬영하므로 중복 집계를 막는다.
        var movedSet = new HashSet<int>();
        var skippedSet = new HashSet<int>();
        var visByIndex = new Dictionary<int, bool>();
        var lastMoved = -1;

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
            _logger.LogInformation("[검사 수행] 검사 Run={RunId}, Task={TaskId}, attempt={Attempt} (ACS order={OrderId}, action={ActionId}, jobRef={JobRef})",
                runId, taskId, attempt, context.AcsOrderId, context.AcsActionId, context.AcsJobRef);
        else
            _logger.LogInformation("[검사 수행] 검사 Run={RunId}, Task={TaskId}, attempt={Attempt}", runId, taskId, attempt);

        for (var i = 0; i < waypoints.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // 일시정지 체크포인트 ① — 다음 경유점으로 이동하기 전. 재개되면 정지 직전 경유점으로 되돌아가 재촬영(§4.3).
            if (await _pauseGate.WaitWhilePausedAsync(ct) && lastMoved >= 0 && !_dryRun && lastMoved < i)
            {
                _logger.LogInformation("[검사 수행] 일시정지 해제 — 정지 직전 경유점 #{Idx} 부터 재촬영", lastMoved + 1);
                context.Progress?.Invoke($"일시정지 해제 — 경유점 #{lastMoved + 1} 부터 재촬영");
                i = lastMoved;
            }

            var w = waypoints[i];
            if (IsSkipped(w)) { skippedSet.Add(i); continue; }

            // 경유점 순회(검사 스캔)만 검사 속도 적용 — 스테이징 등 접근성 모션은 이동 속도(Velocity).
            var rc = await _cobot.Rpc.MoveLAsync(ToTargetBase(w), tool: context.Tool, user: 0,
                vel: context.InspectVelocity ?? context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
            if (rc != 0)
                return StepResult.Fail(
                    $"경유점 #{i + 1} 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — {movedSet.Count}점 이동 후 중단.");
            movedSet.Add(i);
            lastMoved = i;

            if (settle > TimeSpan.Zero)
                await Task.Delay(settle, ct);

            // 일시정지 체크포인트 ② — 이동은 마쳤고 촬영 전. 재개되면 이 자리(정지 직전 경유점)에서 촬영한다.
            if (await _pauseGate.WaitWhilePausedAsync(ct))
                _logger.LogInformation("[검사 수행] 일시정지 해제 — 경유점 #{Idx} 촬영부터 재개", i + 1);

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
            visByIndex[i] = outcome.Success;
            if (!outcome.Success)
            {
                _logger.LogWarning("[검사 수행] 경유점 #{Idx} 비전 실패: task={Task}, seq={Seq}, sent={Sent}, responded={Resp}, code={Code}",
                    i + 1, taskId, captureSeq, outcome.Sent, outcome.Responded,
                    outcome.Code is { } c ? ResultCodeNames.NameOf((ushort)c) : "—");
            }
        }

        int moved = movedSet.Count, skipped = skippedSet.Count;
        int visOk = visByIndex.Values.Count(ok => ok), visFail = visByIndex.Count - visOk;
        var msg =
            (_dryRun ? "검사 수행 완료(드라이런·비전 없음) — " : "검사 수행 완료 — ") +
            $"티칭설정 '{profile.Name}', 이동 {moved}점" +
            (skipped > 0 ? $"(θ 초과 {skipped}점 제외)" : "") +
            (_dryRun ? "" : $", 비전 OK {visOk}/{moved}" + (visFail > 0 ? $" (실패 {visFail})" : "")) +
            $" [내부 wobj #{wobjId}→BASE/user 0, tool {context.Tool}, WallID 0x{context.InspectionSurfaceId:X2}, Run {runId}].";
        _logger.LogInformation("[검사 수행] {Msg}", msg);

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

    /// <summary>
    /// 절대 모드 첫 경유점의 비틀림 후보(0/180°)를 IK 로 풀어 J6 안전 범위(물리 90°~180°)를 벗어나지 않는 쪽을
    /// 고른다(<see cref="Inspection.WObjAttitude.SelectSafeTwist"/>). IK 는 현재 관절 기준 최소 변위 해라
    /// 손목을 뒤집지 않는 해 = MoveL 이 실제로 갈 J6 와 같다. 고를 수 없으면 Error 에 사유를 담는다.
    /// </summary>
    private async Task<(double TwistDeg, string? Error)> SelectSafeTwistAsync(
        SequenceContext context, InspectionWaypoint first, double[] frame,
        Inspection.WObjAttitude attitude, CancellationToken ct)
    {
        var current = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
        var raw = InternalWorkpieceFrame.ToBasePose(
            new[] { first.X, first.Y, first.Z, first.RxDeg, first.Theta, first.RzDeg }, frame);

        var candidates = new List<(double TwistDeg, double? J6, double? TotalDeg)>();
        foreach (var twist in new[] { 0.0, 180.0 })
        {
            var target = (attitude with { TwistOverrideDeg = twist }).KeepTwist(raw);
            double? j6 = null, total = null;
            try
            {
                var joints = await _cobot.Rpc.GetInverseKinNearestAsync(target, current, context.Tool, 0, ct);
                j6 = joints[5];
                total = joints.Zip(current, (a, b) => Math.Abs(a - b)).Sum();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[검사 수행] 비틀림 {Twist:0}° 후보 IK 실패: {Msg}", twist, ex.Message);
            }
            candidates.Add((twist, j6, total));
        }

        var desc = string.Join(", ", candidates.Select(c =>
            c.J6 is { } j ? $"{c.TwistDeg:0}°→J6 {j:0.0}°(Δ{j - current[5]:+0.0;-0.0}, 총 변위 {c.TotalDeg:0}°)" : $"{c.TwistDeg:0}°→IK 불가"));
        var pick = Inspection.WObjAttitude.SelectSafeTwist(current[5], candidates);
        if (pick is not { } p)
        {
            _logger.LogWarning("[검사 수행] 안전 회전 후보 없음 — 현재 J6 {J6:0.0}°, 후보 {Desc}", current[5], desc);
            return (0, $"첫 경유점으로 가려면 J6 를 안전 범위(90°~180°) 밖으로 돌려야 합니다(센서 헤드 간섭 위험) — 이동하지 않고 중단. " +
                       $"현재 J6 {current[5]:0.0}°, 후보 {desc}. 'Cobot 검사위치 이동'의 검사 방향(수평/수직)을 확인하세요.");
        }

        _logger.LogInformation("[검사 수행] 비틀림 {Twist:0}° 선택 — J6 {Cur:0.0}° → {Next:0.0}° (Δ{Delta:+0.0;-0.0}°, 간섭 없음). 후보 {Desc}",
            p.TwistDeg, current[5], current[5] + p.DeltaJ6, p.DeltaJ6, desc);
        return (p.TwistDeg, null);
    }
}
