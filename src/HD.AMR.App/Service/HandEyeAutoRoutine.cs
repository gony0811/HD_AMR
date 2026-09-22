using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 핸드아이(T_T_C) <b>자동</b> 표본 수집 루틴 — 작업자가 마커가 FOV 에 들어온 초기 자세만 잡아주면,
/// 코봇이 스스로 손목을 서로 직교하는 축(Rz/Rx/Ry·대각)으로 ±θ 돌려가며 각 자세에서
/// <see cref="ArucoHandEyeService.CaptureAsync"/> 로 표본을 모은다. 구조는
/// <see cref="LaserHeadCalibrationRoutine"/> 을 본떴다(앵커 star 패턴, 저속, finally 앵커 복귀 보장).
///
/// <b>마커 유지(피벗 회전):</b> TCP 중심 회전은 카메라 시선을 θ만큼 흔들어 마커가 FOV(D435 수직 42°)를
/// 벗어난다. 초기 검출에서 얻은 마커 위치를 피벗으로 삼아, 회전 R 에 보상 병진 t = p − R·p 를 합성한
/// tool 오프셋으로 이동해 시선이 마커 근처에 머물게 한다. 그래도 놓치면 θ/2 로 1회 재시도 후 건너뛴다.
///
/// <b>안전:</b> 속도 5%(≤30 클램프), 회전 ≤30°, 보상 병진 ≤150mm, <see cref="SequenceRunGate"/> 로
/// 프로세스 전역 모션 1건 강제, 리프트/AMR 이동 감지 시 즉시 중단, 취소/실패와 무관하게 앵커 복귀.
/// 예외를 밖으로 던지지 않고 항상 결과 객체로 반환한다.
/// </summary>
public class HandEyeAutoRoutine
{
    private const double DefaultVelPct = 5;
    private const int SettleMs = 600;
    private const double MinTiltDeg = 5;
    private const double MaxTiltDeg = 30;
    private const double MaxPivotTransMm = 150;
    private const double LiftMovedToleranceMm = 1.0;
    private const double AmrMovedToleranceMm = 5.0;
    private const double AmrMovedToleranceDeg = 0.2;

    private readonly CameraService _cam;
    private readonly CobotService _cobot;
    private readonly ArucoHandEyeService _handEye;
    private readonly TelescopicService _lift;
    private readonly AMRService _amr;
    private readonly SequenceRunGate _gate;
    private readonly ILogger<HandEyeAutoRoutine> _logger;

    public HandEyeAutoRoutine(CameraService cam, CobotService cobot, ArucoHandEyeService handEye,
        TelescopicService lift, AMRService amr, SequenceRunGate gate, ILogger<HandEyeAutoRoutine> logger)
    {
        _cam = cam; _cobot = cobot; _handEye = handEye; _lift = lift; _amr = amr; _gate = gate; _logger = logger;
    }

    public async Task<HandEyeAutoResult> RunAsync(
        ArucoSettings settings, int tool, double[]? ttcEstimate, double tiltDeg,
        Action<string>? progress, CancellationToken ct)
    {
        double theta = Math.Clamp(tiltDeg, MinTiltDeg, MaxTiltDeg);
        double vel = Math.Clamp(DefaultVelPct, 1, 30);

        void Report(string msg)
        {
            _logger.LogInformation("핸드아이 자동 캡처: {Msg}", msg);
            progress?.Invoke(msg);
        }

        // ── 전제조건 ──────────────────────────────────────────────────
        if (!OperatingSystem.IsWindows())
            return HandEyeAutoResult.Fail("ArUco 검출은 Windows(OpenCV 네이티브)에서만 동작합니다.");
        if (!_cobot.IsConnected)
            return HandEyeAutoResult.Fail("코봇 RPC 미연결.");
        if (!_cobot.IsServoEnabled)
            return HandEyeAutoResult.Fail("서보 OFF — 서보 ON 후 실행하세요.");
        if (!_cam.IsStreaming || _cam.LatestColor is null)
            return HandEyeAutoResult.Fail("카메라 스트리밍이 필요합니다.");
        if (_cam.GetD2CParams() is not { IsValid: true })
            return HandEyeAutoResult.Fail("카메라 내부 파라미터를 읽을 수 없습니다.");
        if (settings.SizeMm <= 0)
            return HandEyeAutoResult.Fail("ArUco 한 변의 실측 크기(mm)를 입력하세요.");
        if (!_gate.TryEnter())
            return HandEyeAutoResult.Fail("다른 모션 루틴(시퀀스/조그)이 실행 중입니다 — 종료 후 다시 시도하세요.");

        var samples = new List<HandEyeSample>();
        int attempted = 0, skipped = 0, entryTool = -1;
        double[]? anchor = null;
        double[]? anchorJoints = null;
        bool atAnchor = true;
        bool returned = true;
        string? error = null;

        // 플랫폼 정지 기준값 — 루틴 중 움직이면 AX=XB 전제가 깨지므로 즉시 중단한다.
        double? liftStart = _lift.Latest?.HeightMm;
        var amrStart = _amr.LatestStatus?.Pose;

        try
        {
            entryTool = await NormalizeFramesAsync(tool, Report, ct);
            Report("앵커 pose 조회(무모션)…");
            anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, ct);
            anchorJoints = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);   // IK 실패 시 복귀 폴백용.

            // ── 초기 검출: 마커 ID 확정(미지정이면 가장 큰 마커) + 피벗 거리 확보 ──
            var frame = _cam.LatestColor ?? throw new InvalidOperationException("카메라 프레임이 없습니다.");
            var intr = _cam.GetD2CParams()!;
            int markerId;
            if (settings.MarkerId is { } fixedId) markerId = fixedId;
            else
            {
                var infos = await Task.Run(() => ArucoPoseEstimator.DetectMarkerInfos(frame, settings.Dictionary), ct);
                if (infos.Length == 0)
                    throw new InvalidOperationException("초기 자세에서 마커가 보이지 않습니다 — 마커가 FOV 에 들어오게 조그한 뒤 시작하세요.");
                markerId = infos.MaxBy(m => m.AreaPx)!.Id;
                Report($"기대 ID 미지정 — 가장 큰 마커 ID {markerId} 채택.");
            }
            var det = await Task.Run(() => ArucoPoseEstimator.DetectAndEstimate(
                frame, intr, settings.SizeMm, markerId, settings.Dictionary), ct);
            if (det is null)
                throw new InvalidOperationException(
                    $"초기 자세에서 ID {markerId} 마커가 검출되지 않습니다 — 마커가 FOV 에 크게 들어오게 조그한 뒤 시작하세요.");

            var effSettings = new ArucoSettings
            { Dictionary = settings.Dictionary, SizeMm = settings.SizeMm, MarkerId = markerId };
            var pivot = EstimatePivotInTool(ttcEstimate, det.PoseCQ);
            Report($"마커 ID {markerId}, 거리 {det.PoseCQ[2]:0} mm, 피벗(tool) " +
                   $"[{pivot[0]:0}, {pivot[1]:0}, {pivot[2]:0}] mm, 틸트 ±{theta:0.#}°, 속도 {vel:0}%.");

            // ── 표본 #1: 앵커 ─────────────────────────────────────────
            attempted++;
            samples.Add(await _handEye.CaptureAsync(effSettings, tool, 5, ct));
            Report($"표본 1/{1 + Waypoints.Length}: 앵커 캡처 완료.");

            // ── star 패턴: 각 자세 → 캡처 → 앵커 복귀 ─────────────────
            for (int i = 0; i < Waypoints.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                EnsurePlatformStationary(liftStart, amrStart);

                var (label, axis, sign) = Waypoints[i];
                attempted++;
                bool captured = false;

                foreach (var angle in new[] { theta, theta / 2 })
                {
                    var offset = BuildPivotOffsetPose(axis, sign * angle, pivot, MaxPivotTransMm);
                    Report($"{label} {sign * angle:+0.#;-0.#}° 이동…");
                    atAnchor = false;
                    await MoveOffsetAsync(anchor, offset, tool, vel, label, ct);
                    await Task.Delay(SettleMs, ct);

                    if (await IsMarkerVisibleAsync(markerId, effSettings, ct))
                    {
                        try
                        {
                            samples.Add(await _handEye.CaptureAsync(effSettings, tool, 5, ct));
                            captured = true;
                            Report($"표본 {samples.Count}/{1 + Waypoints.Length}: {label} 캡처 완료.");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Report($"{label} 캡처 실패({ex.Message}) — 건너뜀."); }
                        break;
                    }

                    Report($"{label}: 마커 미검출 — 앵커 복귀 후 " +
                           (angle == theta ? "절반 각도로 재시도." : "이 방향은 건너뜀."));
                    await MoveOffsetAsync(anchor, new double[6], tool, vel, "앵커 복귀", ct);
                    atAnchor = true;
                    await Task.Delay(SettleMs, ct);
                }

                if (!captured) skipped++;
                if (!atAnchor)
                {
                    await MoveOffsetAsync(anchor, new double[6], tool, vel, "앵커 복귀", ct);
                    atAnchor = true;
                    await Task.Delay(SettleMs, ct);
                }
            }

            Report($"자동 캡처 종료 — 표본 {samples.Count}개, 건너뜀 {skipped}개.");
        }
        catch (OperationCanceledException)
        {
            error = "중단됨 (사용자 취소).";
            Report(error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "핸드아이 자동 캡처 실패");
            error = ex.Message;
            Report($"실패: {error}");
        }
        finally
        {
            // 성공/실패/취소와 무관하게 앵커 복귀 보장 — 사용자 취소 토큰과 분리된 자체 15초 한도.
            if (anchor is not null && !atAnchor)
            {
                try
                {
                    Report("앵커 복귀 시도…");
                    using var homeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, new double[6],
                        tool: tool, vel: vel, ct: homeCts.Token);
                    returned = rc == 0;
                    if (rc != 0)
                        Report($"앵커 복귀 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — 수동 조그로 복귀하세요.");
                }
                catch (Exception ex)
                {
                    returned = false;
                    Report($"앵커 복귀 실패: {ex.Message} — 수동 조그로 복귀하세요.");
                }

                // MoveL(IK) 경로가 막혔으면 시작 관절각으로 MoveJ 폴백 — MoveJ 는 IK 를 쓰지 않아
                // 활성 좌표계가 어긋나 있어도 성공한다(ResetActiveFrameAsync 와 같은 이유).
                if (!returned && anchorJoints is not null)
                {
                    try
                    {
                        using var jointCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        var rc = await _cobot.Rpc.MoveJAsync(anchorJoints, new double[6], tool: tool, user: 0,
                            vel: vel, ct: jointCts.Token);
                        returned = rc == 0;
                        Report(rc == 0
                            ? "시작 관절각(MoveJ)으로 앵커에 복귀했습니다."
                            : $"앵커 복귀(MoveJ) 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — 수동 조그로 복귀하세요.");
                    }
                    catch (Exception ex) { Report($"앵커 복귀(MoveJ) 실패: {ex.Message} — 수동 조그로 복귀하세요."); }
                }
            }
            await RestoreEntryToolAsync(entryTool, tool, Report);
            _gate.Exit();
        }

        if (error is not null)
            return new HandEyeAutoResult(false, error, samples, attempted, skipped, returned);
        if (samples.Count < HandEyeSolver.MinPoses)
            return new HandEyeAutoResult(false,
                $"유효 표본이 {samples.Count}개뿐입니다(최소 {HandEyeSolver.MinPoses}) — 마커 위치/조명을 바꿔 다시 시도하세요.",
                samples, attempted, skipped, returned);
        return new HandEyeAutoResult(true, null, samples, attempted, skipped, returned);
    }

    /// <summary>진입 시 활성 좌표계를 (공구 <paramref name="tool"/>, 작업물 0)으로 정규화하고, 진입 시점의
    /// 활성 공구를 돌려준다(종료 시 복원용). 컨트롤러에서 활성 공구를 읽지 못하면 설정 기본값
    /// (<c>DefaultToolId</c>)이 오므로 복원 대상도 그 값이 된다 — 조그 리본·시퀀스의 정규화와 같은 기준이다.
    /// 무변위 MoveJ 라 로봇은 움직이지 않는다.
    /// 실패해도 중단하지 않는다 — <see cref="Communication.FairinoRpcClient.GetInverseKinForMoveAsync"/> 가
    /// 활성 공구를 스스로 보정하므로 이중 방어다.</summary>
    private async Task<int> NormalizeFramesAsync(int tool, Action<string> report, CancellationToken ct)
    {
        var (entryTool, entryUser) = await _cobot.Rpc.ResolveActiveFramesAsync(ct, strict: false);
        if (entryTool == tool && entryUser == 0) return entryTool;
        try
        {
            var rc = await _cobot.Rpc.ResetActiveFrameAsync(tool, 0, ct);
            report(rc == 0
                ? $"활성 좌표계 정규화(무변위 MoveJ): 툴 #{entryTool}/작업물 #{entryUser} → 툴 #{tool}/베이스(0)."
                : $"활성 좌표계 정규화 실패(rc={rc}){FairinoErrorCodes.Suffix(rc)} — IK 공구 보정으로 계속합니다.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { report($"활성 좌표계 정규화 생략({ex.Message}) — IK 공구 보정으로 계속합니다."); }
        return entryTool;
    }

    /// <summary>루틴이 바꾼 활성 공구를 진입 시점 값으로 되돌린다(무변위 MoveJ). MoveL 의 tool 인자가 활성
    /// 공구를 바꾸므로, 보정용 공구(예: 카메라 기준 #0)를 그대로 남기면 이후 다른 화면·시퀀스가 자기
    /// 진입부에서 다시 정규화하기 전까지 그 공구 기준으로 해석된다.</summary>
    private async Task RestoreEntryToolAsync(int entryTool, int tool, Action<string> report)
    {
        if (entryTool < 0 || entryTool == tool) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var rc = await _cobot.Rpc.ResetActiveFrameAsync(entryTool, 0, cts.Token);
            report(rc == 0
                ? $"활성 공구를 진입 시점 값(#{entryTool})으로 복원했습니다."
                : $"활성 공구 복원 실패(rc={rc}){FairinoErrorCodes.Suffix(rc)} — 조그 리본의 '활성 좌표계 초기화'로 맞추세요.");
        }
        catch (Exception ex)
        { report($"활성 공구 복원 실패: {ex.Message} — 조그 리본의 '활성 좌표계 초기화'로 맞추세요."); }
    }

    /// <summary>웨이포인트 스케줄 — 직교 3축 + 대각. Rz(광축) 먼저(마커 이탈 위험이 가장 낮다).</summary>
    private static readonly (string Label, double[] Axis, int Sign)[] Waypoints =
    {
        ("Rz", new[] { 0.0, 0.0, 1.0 }, +1),
        ("Rz", new[] { 0.0, 0.0, 1.0 }, -1),
        ("Rx", new[] { 1.0, 0.0, 0.0 }, +1),
        ("Rx", new[] { 1.0, 0.0, 0.0 }, -1),
        ("Ry", new[] { 0.0, 1.0, 0.0 }, +1),
        ("Ry", new[] { 0.0, 1.0, 0.0 }, -1),
        ("Rxy", new[] { 0.70710678, 0.70710678, 0.0 }, +1),
        ("Rxy", new[] { 0.70710678, 0.70710678, 0.0 }, -1),
    };

    /// <summary>
    /// 피벗 회전용 tool 오프셋 pose 를 만든다 — 축 <paramref name="axisUnit"/>(tool 좌표계 단위벡터) 둘레
    /// <paramref name="thetaDeg"/> 회전 + 피벗점 <paramref name="pivotMm"/> 이 제자리에 남도록 하는
    /// 보상 병진 t = p − R·p. 병진 크기는 <paramref name="maxTransMm"/> 로 클램프한다(방향 유지, 축소).
    /// </summary>
    public static double[] BuildPivotOffsetPose(double[] axisUnit, double thetaDeg, double[] pivotMm, double maxTransMm)
    {
        var r = AxisAngleRotation(axisUnit, thetaDeg);

        var rp = new double[3];
        for (int i = 0; i < 3; i++)
            rp[i] = r[i, 0] * pivotMm[0] + r[i, 1] * pivotMm[1] + r[i, 2] * pivotMm[2];
        var t = new[] { pivotMm[0] - rp[0], pivotMm[1] - rp[1], pivotMm[2] - rp[2] };

        double norm = Math.Sqrt(t[0] * t[0] + t[1] * t[1] + t[2] * t[2]);
        if (norm > maxTransMm && norm > 0)
        {
            double s = maxTransMm / norm;
            t[0] *= s; t[1] *= s; t[2] *= s;
        }

        var m = new double[4, 4];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] = r[i, j];
        m[0, 3] = t[0]; m[1, 3] = t[1]; m[2, 3] = t[2]; m[3, 3] = 1.0;
        return FrameMath.MatrixToPose(m);
    }

    /// <summary>
    /// tool 좌표계 기준 마커(피벗) 위치 추정. T_T_C 추정치가 있으면 p_T = T_T_C · p_C,
    /// 없으면 카메라≈TCP·광축≈tool Z 가정으로 [0, 0, z(마커 거리)].
    /// </summary>
    public static double[] EstimatePivotInTool(double[]? ttcEstimate, double[] markerPoseCQ)
    {
        if (ttcEstimate is { Length: 6 } ttc && ttc.Any(v => Math.Abs(v) > 1e-9))
        {
            var m = FrameMath.Multiply(FrameMath.PoseToMatrix(ttc), FrameMath.PoseToMatrix(
                new[] { markerPoseCQ[0], markerPoseCQ[1], markerPoseCQ[2], 0, 0, 0 }));
            return new[] { m[0, 3], m[1, 3], m[2, 3] };
        }
        return new[] { 0.0, 0.0, markerPoseCQ[2] };
    }

    /// <summary>축-각 회전행렬(로드리게스). <paramref name="axisUnit"/> 은 단위벡터 가정(내부 정규화).</summary>
    private static double[,] AxisAngleRotation(double[] axisUnit, double thetaDeg)
    {
        double n = Math.Sqrt(axisUnit[0] * axisUnit[0] + axisUnit[1] * axisUnit[1] + axisUnit[2] * axisUnit[2]);
        double ux = axisUnit[0] / n, uy = axisUnit[1] / n, uz = axisUnit[2] / n;
        double th = thetaDeg * Math.PI / 180.0;
        double c = Math.Cos(th), s = Math.Sin(th), v = 1 - c;

        return new[,]
        {
            { c + ux * ux * v,      ux * uy * v - uz * s,  ux * uz * v + uy * s },
            { uy * ux * v + uz * s, c + uy * uy * v,       uy * uz * v - ux * s },
            { uz * ux * v - uy * s, uz * uy * v + ux * s,  c + uz * uz * v      },
        };
    }

    private async Task<bool> IsMarkerVisibleAsync(int markerId, ArucoSettings settings, CancellationToken ct)
    {
        var frame = _cam.LatestColor;
        if (frame is null || DateTime.UtcNow - _cam.LastFrameAt > TimeSpan.FromSeconds(1)) return false;
        var infos = await Task.Run(() => ArucoPoseEstimator.DetectMarkerInfos(frame, settings.Dictionary), ct);
        return infos.Any(m => m.Id == markerId);
    }

    private void EnsurePlatformStationary(double? liftStart, RobotPose? amrStart)
    {
        if (liftStart is { } h0 && _lift.Latest is { HeightMm: >= 0 } s && Math.Abs(s.HeightMm - h0) > LiftMovedToleranceMm)
            throw new InvalidOperationException(
                $"리프트가 움직였습니다({h0:0} → {s.HeightMm} mm) — 표본이 무효이므로 중단합니다.");

        if (amrStart is { } a0 && _amr.LatestStatus?.Pose is { } a)
        {
            double dMm = Math.Sqrt(Math.Pow((a.X - a0.X) * 1000, 2) + Math.Pow((a.Y - a0.Y) * 1000, 2));
            double dDeg = Math.Abs(a.Angle - a0.Angle) * 180 / Math.PI;
            if (dMm > AmrMovedToleranceMm || dDeg > AmrMovedToleranceDeg)
                throw new InvalidOperationException(
                    $"AMR 이 움직였습니다(Δ{dMm:0.0} mm, {dDeg:0.00}°) — 표본이 무효이므로 중단합니다.");
        }
    }

    /// <summary>앵커 기준 tool 오프셋 이동. rc≠0 이면 사유를 붙여 예외.</summary>
    private async Task MoveOffsetAsync(
        double[] anchor, double[] offset, int tool, double vel, string what, CancellationToken ct)
    {
        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset, tool: tool, vel: vel, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException($"{what} 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
    }
}

/// <summary>자동 캡처 결과 — 수집 표본과 진행 통계. 산출/저장은 호출부(UI)가 기존 흐름으로 진행한다.</summary>
public sealed record HandEyeAutoResult(
    bool Success, string? Error, IReadOnlyList<HandEyeSample> Samples,
    int Attempted, int Skipped, bool ReturnedToAnchor)
{
    public static HandEyeAutoResult Fail(string error, bool returned = true)
        => new(false, error, Array.Empty<HandEyeSample>(), 0, 0, returned);
}
