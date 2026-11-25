using System.Text.Json;
using HD.AMR.App.Communication;
using HD.AMR.App.Communication.GasDetector;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
///
/// 검사 센서(<see cref="InspectionSensorSettings.Mode"/>)가 <c>GasDetector</c> 면 CAPTURE_REQ 대신 경유점마다
/// <see cref="GasDetectorService.MeasureAsync"/> 로 헬륨 리크레이트를 측정해 OK/NG 판정한다. NG(리크 검출)는 정상적인
/// 검사 결과라 집계만 하고, 측정 실패(통신)만 비전 실패와 같이 실패율 판정에 넣는다.
/// 측정 방식이 <see cref="GasMeasureMode.Continuous"/> 면 경유점에 멈춰 재지 않고 경유점 사이를 이동하는 동안
/// <see cref="GasDetectorService.MeasureDuringAsync"/> 로 계속 샘플링해 구간 최대값으로 판정한다(결과는 도착 경유점의 포인트).
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
    private readonly GasDetectorService _gas;
    private readonly bool _useGas;
    private readonly DrawingService _drawing;
    private readonly ParameterService _param;
    private readonly MissionPauseGate _pauseGate;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ILogger<InspectionRunStep> _logger;

    /// <summary>비전 CAPTURE_REQ 응답 대기 기본값(초) — 프로필 DelaySec 미설정(0 이하) 시. 검사 프로파일 화면 기본값과 동일.</summary>
    public const double DefaultVisionTimeoutSec = 10.0;

    /// <summary>기본 검사 속도 [%] 파라미터 — 검사 프로파일에 속도가 없을 때(0 이하) 경유점 순회에 쓴다.</summary>
    public const string DefaultInspectVelKey = "Inspection.DefaultInspectVelPercent";
    /// <summary>기본 검사 속도 파라미터의 기본값 [%].</summary>
    public const int DefaultInspectVelPercent = 2;

    /// <summary>
    /// 경유점 순회(검사 스캔) 속도 [%] 결정 — ① 지정값(레시피 검사 속도·시퀀스 화면 입력) ② 검사 프로파일에 저장된 속도
    /// ③ 기본 검사 속도 파라미터(<see cref="DefaultInspectVelKey"/>, 1~100 밖이면 <see cref="DefaultInspectVelPercent"/>).
    /// 이동 속도(접근·복귀)로는 떨어지지 않는다 — 검사 속도를 따로 주지 않았다고 빠른 이동 속도로 훑으면 안 된다.
    /// </summary>
    public static (int Percent, string Source) ResolveScanVelocity(int? explicitPercent, int profileRunVel, double? defaultParam)
    {
        if (explicitPercent is { } e) return (Math.Clamp(e, 1, 100), "지정값");
        if (profileRunVel > 0) return (Math.Min(profileRunVel, 100), "검사 프로파일 속도");
        return defaultParam is double d && d >= 1 && d <= 100
            ? ((int)Math.Round(d), "기본 검사 속도 파라미터")
            : (DefaultInspectVelPercent, "기본 검사 속도");
    }

    /// <summary>직행 이동이 사이 경유점보다 높이 떠서 건너갈 때 두는 여유 [mm].</summary>
    public const double DirectTravelClearanceMm = 5.0;

    /// <summary>
    /// 떨어진 두 경유점 사이 직행 계획(작업물 좌표 Z — 면에서 멀어지는 쪽이 +). 사이 경유점이 모두 양 끝 중 낮은 쪽보다
    /// 높지 않으면 직선 한 번으로 간다(직선은 양 끝 높이 사이에 있으므로 교시 경로보다 면에 가까워지지 않는다).
    /// 더 높은 경유점(면이 솟은 곳)이 있으면 전체 최고 높이 + <see cref="DirectTravelClearanceMm"/> 로 올라가 건너간 뒤 내려온다.
    /// </summary>
    public static (double SafeZ, bool Rise, bool Descend) PlanDirectTravel(double fromZ, double toZ, IEnumerable<double> betweenZ)
    {
        const double tol = 0.5;
        var between = betweenZ.ToList();
        var low = Math.Min(fromZ, toZ);
        if (between.Count == 0 || between.Max() <= low + tol)
            return (Math.Max(fromZ, toZ), false, false);
        var safeZ = Math.Max(Math.Max(fromZ, toZ), between.Max()) + DirectTravelClearanceMm;
        return (safeZ, true, true);
    }

    /// <summary>MoveL 가속/오버라이드 — /inspection 페이지 RunWaypoints 와 동일값.</summary>
    private const double MoveAcc = 100.0;
    private const double MoveOvl = 100.0;

    /// <param name="dryRun">true 면 경유점을 <b>이동만</b> 하고 비전 CAPTURE_REQ 를 보내지 않는다(드라이런).
    /// DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    public InspectionRunStep(
        bool dryRun, CobotService cobot, VisionInterfaceService vision, GasDetectorService gas,
        IOptions<InspectionSensorSettings> sensor, DrawingService drawing,
        ParameterService param, MissionPauseGate pauseGate, LaserDisplacementSensorService laser,
        ILogger<InspectionRunStep> logger)
    {
        _laser = laser;
        _gas = gas;
        _useGas = sensor.Value.UsesGasDetector;
        _dryRun = dryRun;
        _pauseGate = pauseGate;
        _cobot = cobot;
        _vision = vision;
        _drawing = drawing;
        _param = param;
        _logger = logger;
    }

    public string Key => _dryRun ? "inspectionRunDry" : "inspectionRun";
    public string DisplayName => _dryRun
        ? (_useGas ? "검사 수행 (도면 순회·측정 없음)" : "검사 수행 (도면 순회·비전 없음)")
        : (_useGas ? "검사 수행 (도면 순회·가스 측정)" : "검사 수행 (도면 순회)");

    /// <summary>요약·로그에 쓰는 센서 이름.</summary>
    private string SensorName => _useGas ? "가스" : "비전";
    public int DefaultOrder => _dryRun ? 1210 : 1200;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");
        if (context.InspectionProfileId <= 0)
            return StepValidation.Fail("티칭설정 미선택 — 파라미터 칸에서 도면·티칭설정을 선택하세요.");
        if (_useGas && !_dryRun && !_gas.IsConnected)
            return StepValidation.Fail($"가스 디텍터 미연결 ({_gas.Settings.EndpointText}) — SETTINGS > Gas Detector 에서 통신을 확인하세요.");
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

        // 우현 면은 좌현의 거울상이다 — 좌현에서 교시한 프로필을 작업물 Y 로 거울 반전해 실행한다(ProfileMirror).
        // 이후 사전 검증·이동·촬영 위치 보고는 모두 반전된 경유점을 쓴다.
        var mirrorEnabled = await GetMirrorEnabledAsync();
        if (ProfileMirror.ShouldMirror(context.InspectionSurfaceId, context.FacePlacement, mirrorEnabled))
        {
            waypoints = ProfileMirror.Mirror(waypoints);
            _logger.LogInformation(
                "[검사 수행] 우현 면(WallID 0x{Sid:X2}) — 티칭설정 '{Prof}' 경유점을 거울 반전(Y·Rz·Rx 부호)해 실행",
                context.InspectionSurfaceId, profile.Name);
            context.Progress?.Invoke("우현 면 — 티칭 경유점을 거울 반전해 실행");
        }
        else if (mirrorEnabled && ProfileMirror.ShouldMirror(context.InspectionSurfaceId, null)
                 && waypoints.Any(w => w.Y != 0 || w.RzDeg != 0 || w.RxDeg != 0))
        {
            _logger.LogWarning(
                "[검사 수행] 우현 면(WallID 0x{Sid:X2})인데 용접선이 면 축의 − 방향 — 거울 반전하지 않고 교시값 그대로 실행(세로 방향 위치가 어긋날 수 있음)",
                context.InspectionSurfaceId);
        }

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

        // 순회(검사 스캔) 속도: 지정값 → 검사 프로파일 속도 → 기본 검사 속도 파라미터(ResolveScanVelocity).
        var (scanVel, scanVelFrom) = ResolveScanVelocity(context.InspectVelocity, profile.RunVel, await GetDefaultInspectVelAsync());
        var scanVelSource = $"({scanVelFrom})";

        _logger.LogInformation(
            "[검사 수행] 시작: 도면 {Draw}, 티칭설정 '{Prof}'({N}점), 내부 wobj #{Id} 원점 [{X:0.0},{Y:0.0},{Z:0.0}], " +
            "tool={Tool}, 이동 vel={Vel}%, 검사 vel={InspVel}%{InspSrc}, SurfaceID 0x{Sid:X2}, RZ 유지={Rz0:0.0}° (틸트부호 {Sign:+0;-0})",
            profile.DrawingId, profile.Name, waypoints.Count, wobjId,
            frame[0], frame[1], frame[2], context.Tool, context.Velocity,
            scanVel, scanVelSource,
            context.InspectionSurfaceId,
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
        // (J6 −310°~−160°) 안에 머물러야 한다 — 밖으로 돌면 센서 헤드–링크암 간섭(실기). IK 로 미리 확인한다.
        if (profile.PoseAbsolute)
        {
            var twist = await SelectSafeTwistAsync(context, waypoints[firstIdx], frame, attitude, ct);
            if (twist.Error is { } err)
                return StepResult.Fail(err);
            attitude = attitude with { TwistOverrideDeg = twist.TwistDeg };
        }

        // 움직이기 전에 경유점 전체를 순서대로 IK 로 풀어 손목 특이점·팔꿈치 가지 전환을 거른다 — 도중에 컨트롤러가
        // 멈추면 팔이 특이 자세에 남아 다음 task 의 복귀 경로까지 막힌다(실기 2026-11-03 L2-R02C05 H04 #5: J5 0.48°, rc=185).
        var chainError = await CheckWaypointChainAsync(
            context, Enumerable.Range(0, waypoints.Count).Where(i => !IsSkipped(waypoints[i]))
                .Select(i => (i, ToTargetBase(waypoints[i]))).ToList(), ct);
        if (chainError is not null)
        {
            _logger.LogWarning("[검사 수행] 경유점 사전 검증 실패 — {Err}", chainError);
            return StepResult.Fail($"{chainError} — 움직이지 않고 중단. 정차 위치를 이 용접선 쪽으로 옮기거나 티칭설정 '{profile.Name}' 의 자세를 확인하세요.");
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
        var wdByIndex = new Dictionary<int, double?>();   // 경유점별 레이저 거리(촬영 직후, 캐시 값만 읽음 — 시간 추가 없음)
        var ngByIndex = new Dictionary<int, bool>();   // 가스 모드: 리크 검출(NG) 점
        var gasPoints = new SortedDictionary<int, GasPointResult>();   // 가스 모드: 경유점별 scan 기록(SAIGE points[])
        // 임계값은 Task 시작 시 한 번 잡는다 — 도중에 SAIGE 가 바꾸면 다음 Task 부터 적용(SAIGE §3).
        var gasThreshold = _useGas && !_dryRun ? _gas.CurrentThreshold : null;
        var gasStarted = DateTime.UtcNow;
        // 연속 측정: 경유점에 멈춰 재는 대신 경유점 사이를 이동하는 동안 계속 샘플링한다. Task 시작 시 한 번 정한다.
        var gasSettings = _gas.Settings;
        var continuous = gasThreshold is not null && gasSettings.MeasureMode == GasMeasureMode.Continuous;
        // 무정지: 연속 측정이면서 블렌딩 반경 > 0 — 첫 경유점 이후를 멈추지 않고 한 번에 지나간다(RunNonStopPassAsync).
        var nonStop = continuous && gasSettings.NonStopBlendRadiusMm > 0;
        if (gasThreshold is not null)
            _logger.LogInformation("[검사 수행] 가스 판정 임계값 {Th} (초과 NG, NG 시 재측정 최대 {Retry}회), 측정 방식 {Mode}",
                gasThreshold.Describe(), Math.Clamp(gasSettings.MaxRetries, 0, 2),
                continuous ? $"이동 중 연속(샘플 간격 {Math.Max(Xl3000AsciiProtocol.MinSampleIntervalMs, gasSettings.MeasureSampleIntervalMs)} ms)" : "경유점 정지");
        var lastMoved = -1;
        var prevIdx = -1;   // 직전에 도착한 경유점 — 연속 측정 구간의 출발점

        // 연속 측정 1구간: from → to 를 검사 속도로 이동하면서 샘플링해 구간 최대값으로 판정한다. 결과는 도착 경유점(to)의
        // 포인트로 남긴다. OK 가 아니면(NG·실패) 출발점으로 되돌아가 같은 구간을 다시 훑는다(재측정 — 정지 측정의 "같은 자리").
        // prior 가 있으면(무정지 통과에서 이미 잰 1건) 그 뒤 재측정으로 이어 간다 — 이때 코봇은 출발점(from)에 있어야 한다.
        async Task<(GasPointResult? Point, string? MoveError)> ScanSegmentAsync(int from, int to,
            List<GasMeasurementResult>? prior = null)
        {
            var a = waypoints[from];
            var b = waypoints[to];
            var lengthMm = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
            var label = $"WALL 0x{context.InspectionSurfaceId:X2} #{from + 1}→#{to + 1}";
            var maxRetries = Math.Clamp(gasSettings.MaxRetries, 0, 2);
            var responseDelay = TimeSpan.FromMilliseconds(Math.Max(0, gasSettings.MeasureDwellMs));
            var scans = prior ?? new List<GasMeasurementResult>();
            double? peakMm = null;
            while (true)
            {
                var rc = 0;
                DateTime moveStart = default, moveEnd = default;
                var scan = await _gas.MeasureDuringAsync(label, async c =>
                {
                    moveStart = DateTime.UtcNow;
                    rc = await _cobot.Rpc.MoveLAsync(ToTargetBase(b), tool: context.Tool, user: 0,
                        vel: scanVel, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: c);
                    moveEnd = DateTime.UtcNow;
                    return rc == 0;
                }, gasThreshold, scans.Count, ct);
                if (scan is null)
                    return (null, $"경유점 #{to + 1} 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}");
                scans.Add(scan);
                peakMm = scan.PeakFraction(moveStart, moveEnd, responseDelay) is { } f ? f * lengthMm : null;
                if (!GasPointResult.NeedsRetry(scan.Judgement, scans.Count - 1, maxRetries))
                    break;

                // 재측정 — 일시정지 중이면 풀릴 때까지 기다렸다가, 측정 없이 이동 속도로 출발점에 되돌아간다.
                await _pauseGate.WaitWhilePausedAsync(ct);
                _logger.LogInformation("[검사 수행] 구간 #{From}→#{To} {Judge} — 출발점으로 되돌아가 재측정 {Retry}",
                    from + 1, to + 1, scan.Judgement, scans.Count);
                var backRc = await _cobot.Rpc.MoveLAsync(ToTargetBase(a), tool: context.Tool, user: 0,
                    vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
                if (backRc != 0)
                    return (null, $"구간 #{from + 1}→#{to + 1} 재측정 복귀 이동 실패 (rc={backRc}){FairinoErrorCodes.Suffix(backRc)}");
            }

            var (gu, gv, _) = FaceLocalMapper.ToFaceLocal((ushort)context.InspectionSurfaceId, b.X, b.Y, b.Z, context.FacePlacement);
            var point = new GasPointResult(to + 1, gu, gv, scans);
            var last = scans[^1];
            var retryNote = scans.Count > 1 ? $" (재측정 {scans.Count - 1}회)" : "";
            var peakNote = peakMm is { } mm ? $", 최대값 추정 위치 #{from + 1} 에서 약 {mm:0} mm" : "";
            if (point.Result == GasJudgement.Ng)
                _logger.LogInformation("[검사 수행] 구간 #{From}→#{To}({Len:0} mm) NG — 최대 {Max:0.###} {Unit}{Peak}",
                    from + 1, to + 1, lengthMm, last.Value, last.Unit, peakNote);
            context.Progress?.Invoke(point.Result switch
            {
                GasJudgement.Ok => $"구간 #{from + 1}→#{to + 1} 가스 OK 최대 {last.Value:0.###} {last.Unit} ({last.SampleCount}샘플){retryNote}",
                GasJudgement.Ng => $"구간 #{from + 1}→#{to + 1} 가스 NG — 최대 {last.Value:0.###} > 임계 {last.Threshold:0.###} {last.Unit}{peakNote}{retryNote}",
                _ => $"구간 #{from + 1}→#{to + 1} 가스 측정 실패: {last.Error}{retryNote}",
            });
            return (point, null);
        }

        // 무정지 연속 측정: from(지금 서 있는 경유점)에서 rest 의 경유점들을 멈추지 않고(블렌딩) 한 번에 지나가며 잰다.
        // 샘플은 시간 비율로 구간에 나눠 구간마다 도착 경유점의 포인트로 남긴다. OK 가 아닌 구간은 통과 뒤에
        // 그 구간 출발점으로 직행해(TravelAsync) 종전 구간 방식(ScanSegmentAsync)으로 재측정하고, 끝나면 마지막 경유점으로 돌아간다.
        // 일시정지는 통과 도중에는 걸리지 않는다(통과를 마친 뒤 재측정 앞에서 멈춘다).
        async Task<string?> RunNonStopPassAsync(int from, List<int> rest)
        {
            var idxs = new List<int> { from };
            idxs.AddRange(rest);
            var passPoses = idxs.Select(k => ToTargetBase(waypoints[k])).ToList();
            var passLabels = Enumerable.Range(0, rest.Count)
                .Select(n => $"WALL 0x{context.InspectionSurfaceId:X2} #{idxs[n] + 1}→#{idxs[n + 1] + 1}").ToList();
            IReadOnlyList<double[]> passJoints;
            try { passJoints = await _cobot.Rpc.ResolveChainJointsAsync(passPoses.Skip(1).ToList(), context.Tool, 0, ct); }
            catch (InvalidOperationException ex) { return $"무정지 경로 IK 실패({ex.Message})"; }

            _logger.LogInformation("[검사 수행] 무정지 연속 측정 — 경유점 #{From}→#{To} ({N}구간, 블렌딩 반경 {R:0.#} mm)",
                from + 1, rest[^1] + 1, rest.Count, _gas.NonStopBlendRadiusMm);
            context.Progress?.Invoke($"무정지 연속 측정 — 경유점 #{from + 1}→#{rest[^1] + 1} ({rest.Count}구간)");
            var inspectVel = scanVel;
            var (passScans, passMoveError) = await _gas.MeasureNonStopPassAsync(passLabels, passPoses,
                (k, blendR, c) => _cobot.Rpc.MoveLAsync(passPoses[k], jointPos: passJoints[k - 1], tool: context.Tool, user: 0,
                    vel: inspectVel, acc: MoveAcc, ovl: MoveOvl, blendR: blendR, ct: c),
                c => _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, c),
                () => _cobot.StopMotionImmediateAsync(), gasThreshold, ct);
            if (passScans is null)
                return $"무정지 연속 이동 실패 — {passMoveError}";

            // 떨어진 경유점으로 가는 이동(재측정 출발점·마지막 경유점 복귀)은 직행한다 — 사이 경유점이 양 끝보다 높지 않으면
            // 직선 한 번, 높은 곳(코로게이션 마루 등)이 있으면 그 위 안전 높이로 올라가 건너간 뒤 내려온다(PlanDirectTravel).
            // 직행 경로는 교시 경로가 아니라 사전 검증(IK 사슬)이 안 돼 있으므로 먼저 풀어 보고, 막히면 종전처럼 경유점을 차례로 밟는다.
            var passPos = idxs.Count - 1;
            async Task<(bool Done, string? Error)> TryDirectTravelAsync(int target)
            {
                var fromWp = waypoints[idxs[passPos]];
                var toWp = waypoints[idxs[target]];
                int lo = Math.Min(passPos, target), hi = Math.Max(passPos, target);
                var plan = PlanDirectTravel(fromWp.Z, toWp.Z,
                    Enumerable.Range(lo + 1, hi - lo - 1).Select(k => waypoints[idxs[k]].Z));
                var legs = new List<double[]>();
                if (plan.Rise) legs.Add(ToTargetBase(fromWp with { Z = plan.SafeZ }));
                if (plan.Descend) legs.Add(ToTargetBase(toWp with { Z = plan.SafeZ }));
                legs.Add(passPoses[target]);

                if (await CheckWaypointChainAsync(context, legs.Select(p => (idxs[target], p)).ToList(), ct) is { } blocker)
                {
                    _logger.LogInformation("[검사 수행] 경유점 #{From}→#{To} 직행 불가({Why}) — 경유점을 차례로 밟아 이동",
                        idxs[passPos] + 1, idxs[target] + 1, blocker);
                    return (false, null);
                }
                _logger.LogInformation("[검사 수행] 경유점 #{From}→#{To} 직행{How}",
                    idxs[passPos] + 1, idxs[target] + 1,
                    legs.Count > 1 ? $" — 안전 높이 z={plan.SafeZ:0.#}mm 경유({legs.Count}회 이동)" : "");
                foreach (var leg in legs)
                {
                    var legRc = await _cobot.Rpc.MoveLAsync(leg, tool: context.Tool, user: 0,
                        vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
                    if (legRc != 0)
                        return (false, $"경유점 #{idxs[target] + 1} 직행 이동 실패 (rc={legRc}){FairinoErrorCodes.Suffix(legRc)}");
                }
                return (true, null);
            }

            async Task<string?> TravelAsync(int target)
            {
                if (Math.Abs(target - passPos) > 1)
                {
                    var (done, directError) = await TryDirectTravelAsync(target);
                    if (directError is not null) return directError;
                    if (done) { passPos = target; return null; }
                }
                while (passPos != target)
                {
                    var next = passPos + Math.Sign(target - passPos);
                    var travelRc = await _cobot.Rpc.MoveLAsync(passPoses[next], tool: context.Tool, user: 0,
                        vel: context.Velocity, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
                    if (travelRc != 0)
                        return $"경유점 #{idxs[next] + 1} 재측정 이동 실패 (rc={travelRc}){FairinoErrorCodes.Suffix(travelRc)}";
                    passPos = next;
                }
                return null;
            }

            var passMaxRetries = Math.Clamp(gasSettings.MaxRetries, 0, 2);
            for (var n = 0; n < rest.Count; n++)
            {
                int segFrom = idxs[n], segTo = idxs[n + 1];
                var first = passScans[n];
                movedSet.Add(segTo);
                GasPointResult passPoint;
                if (GasPointResult.NeedsRetry(first.Judgement, 0, passMaxRetries))
                {
                    await _pauseGate.WaitWhilePausedAsync(ct);
                    _logger.LogInformation("[검사 수행] 구간 #{From}→#{To} {Judge} — 구간 출발점으로 되돌아가 재측정",
                        segFrom + 1, segTo + 1, first.Judgement);
                    if (await TravelAsync(n) is { } travelError) return travelError;
                    var (retried, retryError) = await ScanSegmentAsync(segFrom, segTo, new List<GasMeasurementResult> { first });
                    if (retried is null) return retryError;
                    passPos = n + 1;
                    passPoint = retried;
                }
                else
                {
                    var wp = waypoints[segTo];
                    var (pu, pv, _) = FaceLocalMapper.ToFaceLocal((ushort)context.InspectionSurfaceId, wp.X, wp.Y, wp.Z, context.FacePlacement);
                    passPoint = new GasPointResult(segTo + 1, pu, pv, new[] { first });
                    context.Progress?.Invoke(passPoint.Result switch
                    {
                        GasJudgement.Ok => $"구간 #{segFrom + 1}→#{segTo + 1} 가스 OK 최대 {first.Value:0.###} {first.Unit} ({first.SampleCount}샘플)",
                        GasJudgement.Ng => $"구간 #{segFrom + 1}→#{segTo + 1} 가스 NG — 최대 {first.Value:0.###} > 임계 {first.Threshold:0.###} {first.Unit}",
                        _ => $"구간 #{segFrom + 1}→#{segTo + 1} 가스 측정 실패: {first.Error}",
                    });
                }
                gasPoints[segTo] = passPoint;
                visByIndex[segTo] = passPoint.Result != GasJudgement.Error;
                ngByIndex[segTo] = passPoint.Result == GasJudgement.Ng;
            }
            if (await TravelAsync(idxs.Count - 1) is { } returnError) return returnError;
            lastMoved = idxs[^1];
            prevIdx = idxs[^1];
            return null;
        }

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

        // 검사 가속도: 순회 동안만 전역 가속도 배율 변경 → finally 에서 기본값 복원(성공·실패·취소 공통).
        var accChanged = await ApplyInspectAccelerationAsync(context, ct);
        try
        {
        for (var i = 0; i < waypoints.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // 일시정지 체크포인트 ① — 다음 경유점으로 이동하기 전. 재개되면 정지 직전 경유점으로 되돌아가 재촬영(§4.3).
            // 연속 측정은 구간 측정이 이동과 함께 끝나 있으므로 되돌아가지 않고 다음 구간부터 이어 간다.
            if (await _pauseGate.WaitWhilePausedAsync(ct) && lastMoved >= 0 && !_dryRun && !continuous && lastMoved < i)
            {
                _logger.LogInformation("[검사 수행] 일시정지 해제 — 정지 직전 경유점 #{Idx} 부터 재촬영", lastMoved + 1);
                context.Progress?.Invoke($"일시정지 해제 — 경유점 #{lastMoved + 1} 부터 재촬영");
                i = lastMoved;
            }

            var w = waypoints[i];
            if (IsSkipped(w)) { skippedSet.Add(i); continue; }

            // 연속 측정: 앞 경유점 → 이 경유점 구간을 이동하면서 잰다. 첫 경유점은 앞 구간이 없어 아래 정지 측정으로 잰다.
            // 무정지: 남은 경유점 전체를 한 번에 지나가며 재고 순회를 끝낸다.
            if (nonStop && prevIdx >= 0)
            {
                var remaining = Enumerable.Range(i, waypoints.Count - i).ToList();
                foreach (var skippedIdx in remaining.Where(k => IsSkipped(waypoints[k])))
                    skippedSet.Add(skippedIdx);
                if (await RunNonStopPassAsync(prevIdx, remaining.Where(k => !IsSkipped(waypoints[k])).ToList()) is { } passError)
                    return StepResult.Fail($"{passError} — {movedSet.Count}점 이동 후 중단.");
                break;
            }
            if (continuous && prevIdx >= 0)
            {
                var (segPoint, moveError) = await ScanSegmentAsync(prevIdx, i);
                if (segPoint is null)
                    return StepResult.Fail($"{moveError} — {movedSet.Count}점 이동 후 중단.");
                movedSet.Add(i);
                lastMoved = i;
                prevIdx = i;
                gasPoints[i] = segPoint;
                visByIndex[i] = segPoint.Result != GasJudgement.Error;
                ngByIndex[i] = segPoint.Result == GasJudgement.Ng;
                continue;
            }

            // 경유점 순회(검사 스캔)만 검사 속도 적용 — 스테이징 등 접근성 모션은 이동 속도(Velocity).
            var rc = await _cobot.Rpc.MoveLAsync(ToTargetBase(w), tool: context.Tool, user: 0,
                vel: scanVel, acc: MoveAcc, ovl: MoveOvl, blendR: -1, ct: ct);
            if (rc != 0)
                return StepResult.Fail(
                    $"경유점 #{i + 1} 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — {movedSet.Count}점 이동 후 중단.");
            movedSet.Add(i);
            lastMoved = i;
            prevIdx = i;

            if (settle > TimeSpan.Zero)
                await Task.Delay(settle, ct);

            // 일시정지 체크포인트 ② — 이동은 마쳤고 촬영 전. 재개되면 이 자리(정지 직전 경유점)에서 촬영한다.
            if (await _pauseGate.WaitWhilePausedAsync(ct))
                _logger.LogInformation("[검사 수행] 일시정지 해제 — 경유점 #{Idx} 촬영부터 재개", i + 1);

            // 드라이런: 경유점 이동만 하고 비전 CAPTURE_REQ 는 보내지 않는다(좌표·경로 검증용).
            if (_dryRun)
            {
                wdByIndex[i] = ReadLaserWd();
                continue;
            }

            // 가스 디텍터: 도착 후 대기·샘플링해 임계값 판정(초과 NG), NG 면 같은 자리에서 최대 2회 재측정(SAIGE §2).
            // 일시정지 재개로 같은 점을 다시 재면 그 점의 결과를 새 측정으로 덮어쓴다.
            if (_useGas)
            {
                var (gu, gv, _) = FaceLocalMapper.ToFaceLocal((ushort)context.InspectionSurfaceId, w.X, w.Y, w.Z, context.FacePlacement);
                var scans = await _gas.MeasurePointAsync($"WALL 0x{context.InspectionSurfaceId:X2} #{i + 1}", gasThreshold!, ct);
                var point = new GasPointResult(i + 1, gu, gv, scans);
                gasPoints[i] = point;
                visByIndex[i] = point.Result != GasJudgement.Error;
                ngByIndex[i] = point.Result == GasJudgement.Ng;
                var last = scans[^1];
                var retryNote = scans.Count > 1 ? $" (재측정 {scans.Count - 1}회)" : "";
                context.Progress?.Invoke(point.Result switch
                {
                    GasJudgement.Ok => $"경유점 #{i + 1} 가스 OK {last.Value:0.###} {last.Unit}{retryNote}",
                    GasJudgement.Ng => $"경유점 #{i + 1} 가스 NG — {last.Value:0.###} > 임계 {last.Threshold:0.###} {last.Unit}{retryNote}",
                    _ => $"경유점 #{i + 1} 가스 측정 실패: {last.Error}{retryNote}",
                });
                continue;
            }

            // surface type 우선순위: 경유점 수동 지정 > |θ| 자동 규칙(구 도면 솎기 프로필).
            // (레시피 SurfaceOverride 는 경유점 단위 지정으로 일원화되며 폐기 — 2026-09-18.)
            var surfaceType = w.SurfaceManual
                ? SurfaceTypes.OrFlat(w.Surface)
                : Math.Abs(w.Theta) >= profile.CorrugThresholdDeg
                    ? SurfaceType.Corrugation
                    : SurfaceType.Flat;
            // v3.2: 코봇 wobj pose → 면-로컬 (u,v,h). 축·부호는 wobj 티칭 규약(§5)에 흡수(FaceLocalMapper).
            var wallId = (ushort)context.InspectionSurfaceId;
            var (u, v, rawH) = FaceLocalMapper.ToFaceLocal(wallId, w.X, w.Y, w.Z, context.FacePlacement);
            // 음수 높이는 비전이 거부한다 — 0 으로 올려 보낸다(코봇 위치는 그대로).
            var h = FaceLocalMapper.CaptureHeight(rawH);
            if (h != rawH)
                _logger.LogInformation("[검사 수행] 경유점 #{Idx} 촬영 높이 {Raw}mm → {H}mm 로 올려 전송(비전은 음수 높이를 받지 않음)",
                    i + 1, rawH, h);
            captureSeq = context.CaptureSeqSource?.Invoke() is { } nextSeq ? nextSeq : (ushort)(captureSeq + 1);
            var data = CaptureReqPayload.Build(surfaceType, wallId, u, v, h, taskId, attempt, captureSeq);
            var outcome = await _vision.Client.RequestCaptureAsync(data, visionTimeout, ct);
            visByIndex[i] = outcome.Success;
            wdByIndex[i] = ReadLaserWd();
            if (!outcome.Success)
            {
                _logger.LogWarning("[검사 수행] 경유점 #{Idx} 비전 실패: task={Task}, seq={Seq}, sent={Sent}, responded={Resp}, code={Code}",
                    i + 1, taskId, captureSeq, outcome.Sent, outcome.Responded,
                    outcome.Code is { } c ? ResultCodeNames.NameOf((ushort)c) : "—");
            }
        }
        }
        finally
        {
            if (accChanged) await RestoreAccelerationAsync();
        }

        // 가스 Task 결과 — 오케스트레이터가 SAIGE 결과 본문으로 만들어 ACS actionState 에 싣는다.
        if (gasThreshold is not null)
            context.Bag[GasTaskResult.BagKey] = new GasTaskResult(gasStarted, DateTime.UtcNow, gasThreshold,
                gasPoints.Values.ToList());

        int moved = movedSet.Count, skipped = skippedSet.Count;
        int visOk = visByIndex.Values.Count(ok => ok), visFail = visByIndex.Count - visOk;
        int gasNg = ngByIndex.Values.Count(ng => ng);
        var sensorSummary = _useGas
            ? $", 가스 측정 {visOk}/{moved} (NG {gasNg}" + (visFail > 0 ? $", 실패 {visFail})" : ")")
            : $", 비전 OK {visOk}/{moved}" + (visFail > 0 ? $" (실패 {visFail})" : "");
        var msg =
            (_dryRun ? $"검사 수행 완료(드라이런·{SensorName} 없음) — " : "검사 수행 완료 — ") +
            $"티칭설정 '{profile.Name}', 이동 {moved}점" +
            (skipped > 0 ? $"(θ 초과 {skipped}점 제외)" : "") +
            (_dryRun ? "" : sensorSummary) +
            $" [내부 wobj #{wobjId}→BASE/user 0, tool {context.Tool}, WallID 0x{context.InspectionSurfaceId:X2}, Run {runId}].";
        _logger.LogInformation("[검사 수행] {Msg}", msg);
        if (FormatWdLog(waypoints, wdByIndex) is { } wdLog)
            _logger.LogInformation("[검사 수행] 경유점별 레이저 WD — {WdLog}", wdLog);

        // 비전 OK 가 하나도 없으면 실패율 설정과 무관하게 실패 — 순회 중 비전 링크가 끊기면 촬영 요청이 즉시
        // (false,false)로 끝나 코봇은 이동만 하고 "성공"으로 보고되며 anchor 프레임까지 캐시되던 문제(기본 상한 1.0 = 판정 없음).
        if (!_dryRun && moved > 0 && visOk == 0)
            return StepResult.Fail(
                $"비전 OK 0/{moved} — 촬영 결과를 하나도 받지 못했습니다. 비전 S/W 링크·촬영 응답을 확인하세요. {msg}");

        // ACS 경로: 비전 실패율 상한 초과 시 스텝 실패로 승격(→ 액션 FAILED + inspectionFailed, §6.4 재시도 정책).
        // UI 단독 실행(VisionFailRatioMax=null)은 현행대로 집계만 하고 성공 반환. 드라이런은 비전이 없어 판정 생략.
        if (!_dryRun && context.VisionFailRatioMax is { } maxRatio && moved > 0)
        {
            var failRatio = (double)visFail / moved;
            if (failRatio > maxRatio)
                return StepResult.Fail(
                    $"{SensorName} 실패율 초과 — {visFail}/{moved} ({failRatio:P0} > 허용 {maxRatio:P0}). {msg}");
        }

        return StepResult.Ok(msg);
    }

    private double? ReadLaserWd()
    {
        if (!_laser.IsConnected) return null;
        var p = _laser.GetPlanePose();
        return p.Valid ? p.Z : null;
    }

    /// <summary>
    /// 경유점별 레이저 거리 요약 — 기준은 첫 θ≈0 유효값. 경유점 Z 가 면 높이를 따르므로 초점이 맞으면 모든 점이 기준과 같아야 한다
    /// (기울인 점도 툴이 면 법선이면 같다). 차이 = 초점 어긋남 추정(+ 면이 멀다). 레이저가 하나도 없으면 null.
    /// 형식: "기준 343.1mm (#1) · 평면 Δ -0.3~+2.1 · 경사 Δ -4.0~+1.0 | #1 θ0 Z33.8 +0.0, #2 θ10 Z33.4 +0.6, ..."
    /// </summary>
    internal static string? FormatWdLog(IReadOnlyList<InspectionWaypoint> waypoints, IReadOnlyDictionary<int, double?> wd)
    {
        var valid = wd.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key).ToList();
        if (valid.Count == 0) return null;
        var refKv = valid.FirstOrDefault(kv => Math.Abs(waypoints[kv.Key].Theta) < 1.0);
        if (refKv.Value is null) refKv = valid[0];
        var refWd = refKv.Value!.Value;

        string Range(IEnumerable<double> ds)
        {
            var l = ds.ToList();
            return l.Count == 0 ? "—" : $"{l.Min():+0.0;-0.0;0.0}~{l.Max():+0.0;-0.0;0.0}";
        }
        var flat = valid.Where(kv => Math.Abs(waypoints[kv.Key].Theta) < 1.0).Select(kv => kv.Value!.Value - refWd);
        var tilt = valid.Where(kv => Math.Abs(waypoints[kv.Key].Theta) >= 1.0).Select(kv => kv.Value!.Value - refWd);
        var points = wd.OrderBy(kv => kv.Key).Select(kv =>
        {
            var w = waypoints[kv.Key];
            var d = kv.Value is { } v ? (v - refWd).ToString("+0.0;-0.0;0.0") : "없음";
            return $"#{kv.Key + 1} θ{w.Theta:0} Z{w.Z:0.#} {d}";
        });
        return $"기준 {refWd:0.0}mm (#{refKv.Key + 1}) · 평면 Δ {Range(flat)} · 경사 Δ {Range(tilt)} | {string.Join(", ", points)}";
    }

    /// <summary>레시피 검사 가속도가 있으면 전역 가속도 배율을 그 값으로 바꾼다. 바꿨으면 true(복원 필요).
    /// 실패해도 검사는 중단하지 않고 기본 가속도로 진행한다 — 가속도는 품질이 아니라 진동·사이클 조정용이다.</summary>
    private async Task<bool> ApplyInspectAccelerationAsync(SequenceContext context, CancellationToken ct)
    {
        if (context.InspectAcceleration is not { } acc) return false;
        // 순회 동안 CobotService 의 주기 동기화가 평소 값으로 덮어쓰지 않게 일시 값으로 등록한다
        // (실기 2026-11-25 12:35:09: 순회 중 파라미터를 10→15% 로 저장하자 동기화가 레시피 10% 를 15% 로 바꿨다).
        FairinoRpcClient.SetOaccOverride(acc);
        try
        {
            var rc = await _cobot.Rpc.SetOaccScaleAsync(acc, ct);
            if (rc == 0)
            {
                _logger.LogInformation("[검사 수행] 순회 가속도 배율 {Acc}% 적용 (종료 시 {Def}% 복원)", acc, FairinoRpcClient.BaseOaccScalePct);
                return true;
            }
            _logger.LogWarning("[검사 수행] 순회 가속도 배율 {Acc}% 설정 실패 (rc={Rc}){Desc} — 기본 가속도로 진행",
                acc, rc, FairinoErrorCodes.Suffix(rc));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[검사 수행] 순회 가속도 배율 {Acc}% 설정 실패 — 기본 가속도로 진행", acc);
        }
        // 설정 실패라도 일부 반영됐을 수 있으므로 복원은 시도한다.
        return true;
    }

    /// <summary>전역 가속도 배율 복원. 취소된 뒤에도 반드시 보내야 하므로 호출자 토큰을 쓰지 않는다.</summary>
    private async Task RestoreAccelerationAsync()
    {
        // 일시 값 해제 — 아래 복원이 실패해도 CobotService 가 5초 안에 평소 값(파라미터)을 다시 보낸다.
        FairinoRpcClient.SetOaccOverride(null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var def = FairinoRpcClient.BaseOaccScalePct;   // 평소 값(파라미터 cobot.accScalePercent, 비면 100)
            var rc = await _cobot.Rpc.SetOaccScaleAsync(def, cts.Token);
            if (rc == 0)
                _logger.LogInformation("[검사 수행] 가속도 배율 {Def}% 복원", def);
            else
                _logger.LogError("[검사 수행] 가속도 배율 복원 실패 (rc={Rc}){Desc} — 이후 모션 가속도가 바뀐 채일 수 있습니다",
                    rc, FairinoErrorCodes.Suffix(rc));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[검사 수행] 가속도 배율 복원 실패 — 이후 모션 가속도가 바뀐 채일 수 있습니다");
        }
    }

    /// <summary>
    /// 경유점 BASE 목표를 순서대로 IK(앞 점 해 기준 최소 변위)로 풀어 <see cref="WaypointChainBlocker"/> 로 판정한다.
    /// 끝점만이 아니라 <b>현재 TCP → 첫 경유점(스테이징)</b>과 <b>경유점 사이 MoveL 경로</b>도 위치 100mm·자세 10° 간격으로
    /// 샘플링해 이어 푼다(<see cref="CobotInspectionMoveStep.PathStepBlocker"/> — 관절 점프·팔꿈치·손목). 끝점이 멀쩡해도
    /// 그 사이에서 J5 가 0 을 지나면 MoveL 이 도중에 rc=38/185 로 멈추기 때문이다(스테이징은 앞 단계가 끝난 자리에서 출발해 길 수 있다).
    /// IK 는 계산만 하는 호출이라 코봇은 움직이지 않는다. 통과하면 null.
    /// </summary>
    private async Task<string?> CheckWaypointChainAsync(
        SequenceContext context, IReadOnlyList<(int Index, double[] Target)> targets, CancellationToken ct)
    {
        var start = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);
        var prevPose = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var chain = new List<(int Index, double[] Joints)>(targets.Count);
        var seed = start;
        var first = true;
        foreach (var (idx, target) in targets)
        {
            var label = first ? $"첫 경유점 #{idx + 1} 스테이징 경로" : $"경유점 #{idx + 1} 앞 경로";
            foreach (var pose in CobotInspectionMoveStep.SamplePoseSegment(
                         prevPose, target, CobotInspectionMoveStep.PlanarSampleMm, CobotInspectionMoveStep.ChainSampleDeg))
            {
                double[] j;
                try { j = await _cobot.Rpc.GetInverseKinNearestAsync(pose, seed, context.Tool, 0, ct); }
                catch (InvalidOperationException ex) { return $"{label} [{Fmt(pose)}] 도달 불가({ex.Message})"; }
                if (CobotInspectionMoveStep.PathStepBlocker(seed, j, start) is { } step)
                    return $"{label} [{Fmt(pose)}] {step}";
                if (Inspection.WObjAttitude.J6RangeBlocker(start[5], j[5]) is { } j6)
                    return $"{label} [{Fmt(pose)}] {j6}";
                seed = j;
            }
            chain.Add((idx, seed));   // 마지막 샘플 = 경유점 자신
            prevPose = target;
            first = false;
        }
        return WaypointChainBlocker(start, chain);

        static string Fmt(double[] p) => string.Join(", ", p.Take(3).Select(v => v.ToString("0")));
    }

    /// <summary>
    /// 경유점 관절 해 사슬에서 첫 문제점의 사유, 없으면 null. 손목 특이점(|J5| &lt; <see cref="CobotInspectionMoveStep.PlanarWristMarginDeg"/>
    /// 또는 출발 자세와 J5 부호 반전 — 사이에서 0 을 지난다)과 팔꿈치 가지 전환, J6 안전 범위
    /// (<see cref="Inspection.WObjAttitude.J6RangeBlocker"/>, 출발 J6 감김 기준)를 본다. J6 는 경유점마다 비틀림이 달라
    /// 크게 바뀌는 것이 정상이라 변화량은 보지 않고 범위만 본다.
    /// </summary>
    internal static string? WaypointChainBlocker(double[] start, IReadOnlyList<(int Index, double[] Joints)> chain)
    {
        foreach (var (idx, j) in chain)
        {
            if (Math.Abs(j[4]) < CobotInspectionMoveStep.PlanarWristMarginDeg || Math.Sign(j[4]) != Math.Sign(start[4]))
                return $"경유점 #{idx + 1} 손목 특이점(J5 {start[4]:0.0}° → {j[4]:0.0}°, 해 [{string.Join(", ", j.Select(v => v.ToString("0.0")))}]°)";
            if (FairinoRpcClient.IsElbowFlip(start, j))
                return $"경유점 #{idx + 1} 팔꿈치 가지 전환(J3 {start[2]:0.0}° → {j[2]:0.0}°)";
            if (Inspection.WObjAttitude.J6RangeBlocker(start[5], j[5]) is { } j6)
                return $"경유점 #{idx + 1} {j6}";
        }
        return null;
    }

    private async Task<bool> GetMirrorEnabledAsync()
    {
        try { return await _param.GetBoolAsync(ProfileMirror.EnabledKey) ?? true; }
        catch { return true; }
    }

    private async Task<double?> GetDefaultInspectVelAsync()
    {
        try { return await _param.GetDoubleAsync(DefaultInspectVelKey); }
        catch { return null; }
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjPointStep.WObjIdKey) ?? 1; }
        catch { return 1; }
    }

    /// <summary>
    /// 절대 모드 첫 경유점의 비틀림 후보(0/180°)를 IK 로 풀어 J6 안전 범위(−310°~−160°)를 벗어나지 않는 쪽을
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
            return (0, $"첫 경유점으로 가려면 J6 를 안전 범위({Inspection.WObjAttitude.SafeJ6RangeText}, 현재 감김 기준) 밖으로 돌려야 합니다(센서 헤드 간섭 위험) — 이동하지 않고 중단. " +
                       $"현재 J6 {current[5]:0.0}°, 후보 {desc}. 'Cobot 검사위치 이동'의 검사 방향(수평/수직)을 확인하세요.");
        }

        _logger.LogInformation("[검사 수행] 비틀림 {Twist:0}° 선택 — J6 {Cur:0.0}° → {Next:0.0}° (Δ{Delta:+0.0;-0.0}°, 간섭 없음). 후보 {Desc}",
            p.TwistDeg, current[5], current[5] + p.DeltaJ6, p.DeltaJ6, desc);
        return (p.TwistDeg, null);
    }
}
