using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 작업자가 최초에 AMR/ArUco +Y 축을 맞추고 마커가 보이는 안전한 코봇 자세만 잡으면,
/// AMR을 여러 자세로 이동시키고 각 정차점에서 카메라를 같은 월드 pose로 되돌려
/// <see cref="ArucoMountSample"/>을 자동 수집한다.
/// 최종 T_A_B 산출/적용은 호출부가 <see cref="ArucoMountCalibration"/>로 수행한다.
/// </summary>
public sealed class ArucoMountAutoRoutine
{
    private const double CobotVelocityPct = 5;
    private const int CobotSettleMs = 600;

    private readonly AMRService _amr;
    private readonly AmrDriveService _drive;
    private readonly CobotService _cobot;
    private readonly ArucoHandEyeService _capture;
    private readonly TelescopicService _lift;
    private readonly SequenceRunGate _gate;
    private readonly ILogger<ArucoMountAutoRoutine> _logger;

    public ArucoMountAutoRoutine(AMRService amr, AmrDriveService drive, CobotService cobot,
        ArucoHandEyeService capture, TelescopicService lift, SequenceRunGate gate,
        ILogger<ArucoMountAutoRoutine> logger)
    {
        _amr = amr;
        _drive = drive;
        _cobot = cobot;
        _capture = capture;
        _lift = lift;
        _gate = gate;
        _logger = logger;
    }

    public async Task<ArucoMountAutoResult> RunAsync(
        ArucoSettings marker, int tool, double[] toolToCamera, double[] initialMount,
        ArucoMountAutoOptions? options, Action<string>? progress, CancellationToken ct)
    {
        options ??= new ArucoMountAutoOptions();

        void Report(string message)
        {
            _logger.LogInformation("ArUco 장착 자동보정: {Message}", message);
            progress?.Invoke(message);
        }

        if (!OperatingSystem.IsWindows())
            return ArucoMountAutoResult.Fail("ArUco 검출은 Windows(OpenCV 네이티브)에서만 동작합니다.");
        if (!_amr.IsConnected || _amr.LatestStatus is null)
            return ArucoMountAutoResult.Fail("AMR 연결 또는 pose가 없습니다.");
        if (!_cobot.IsConnected)
            return ArucoMountAutoResult.Fail("코봇 RPC 미연결.");
        if (!_cobot.IsServoEnabled)
            return ArucoMountAutoResult.Fail("코봇 서보 OFF — 서보 ON 후 실행하세요.");
        if (marker.SizeMm <= 0)
            return ArucoMountAutoResult.Fail("ArUco 한 변의 실측 크기(mm)를 입력하세요.");
        if (toolToCamera.Length != 6 || toolToCamera.All(v => v == 0))
            return ArucoMountAutoResult.Fail("T_T_C가 미설정입니다. 핸드아이 보정을 먼저 완료하세요.");
        if (initialMount.Length != 6 || initialMount.All(v => v == 0))
            return ArucoMountAutoResult.Fail("재조준용 초기 T_A_B가 없습니다. 대략적인 장착값을 먼저 저장하세요.");
        if (!_gate.TryEnter())
            return ArucoMountAutoResult.Fail("다른 모션 루틴(시퀀스/조그)이 실행 중입니다.");

        var samples = new List<ArucoMountSample>();
        double[]? travelPose = null;
        double[]? travelJoints = null;
        RobotPose? startPose = null;
        double[,]? fixedCameraW = null;
        double? liftStart = _lift.Latest?.HeightMm;
        int visited = 0, skipped = 0, entryTool = -1;
        bool returned = true;
        string? error = null;

        try
        {
            startPose = _amr.LatestStatus!.Pose;
            entryTool = await NormalizeFramesAsync(tool, Report, ct);
            travelPose = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, ct);
            travelJoints = await _cobot.Rpc.GetActualJointPosAsync(ct: ct);   // IK 실패 시 복귀 폴백용.
            await PreflightTravelPoseAsync(travelPose, tool, ct);
            Report("초기 자세에서 마커를 계측합니다…");

            var first = await _capture.CaptureAsync(marker, tool, options.FramesPerSample, ct);
            var wa0 = ToPose(startPose);
            AddSample(samples, wa0, first);

            // 초기 추정 T_A_B는 주행/재조준에만 사용한다. 최종 산출에는 모든 원시 표본을 사용한다.
            fixedCameraW = FrameMath.Multiply(
                FrameMath.Multiply(FrameMath.Multiply(FrameMath.PoseToMatrix(wa0),
                    FrameMath.PoseToMatrix(initialMount)), FrameMath.PoseToMatrix(first.TcpPose)),
                FrameMath.PoseToMatrix(toolToCamera));

            await CaptureViewVariantsAsync(samples, wa0, first.TcpPose, first.MarkerPose,
                marker, tool, toolToCamera, options, Report, ct);
            visited++;

            var targets = BuildTargets(startPose, options.LateralTravelM, options.LongitudinalTravelM,
                options.YawExcursionDeg);
            for (int i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                EnsureLiftUnchanged(liftStart);
                await MoveCobotAsync(travelPose, tool, "AMR 주행 안전자세", ct);

                var target = targets[i];
                try
                {
                    Report($"AMR {i + 1}/{targets.Count}: X {target.X:0.000}m, Y {target.Y:0.000}m, " +
                           $"Yaw {target.Angle * 180 / Math.PI:0.0}° 자동 주행…");
                    // 주행 실패는 그 정차점만 건너뛴다 — 한 곳이 막혀도 나머지로 산출할 수 있어야 한다.
                    var actual = await NavigateAndWaitAsync(target, options, Report, ct);
                    EnsureLiftUnchanged(liftStart);

                    var viewPose = CalculateViewToolPose(actual, initialMount, toolToCamera, fixedCameraW);
                    ValidateCobotMove(travelPose, viewPose, options);
                    await MoveCobotAsync(viewPose, tool, "마커 재조준", ct);
                    await Task.Delay(CobotSettleMs, ct);

                    var captured = await _capture.CaptureAsync(marker, tool, options.FramesPerSample, ct);
                    var wa = ToPose(actual);
                    AddSample(samples, wa, captured);
                    await CaptureViewVariantsAsync(samples, wa, viewPose, captured.MarkerPose,
                        marker, tool, toolToCamera, options, Report, ct);
                    visited++;
                    Report($"정차점 {i + 1} 완료 — 누적 표본 {samples.Count}개.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    skipped++;
                    Report($"정차점 {i + 1} 실패({ex.Message}) — 건너뜁니다.");
                }
                finally
                {
                    await MoveCobotAsync(travelPose, tool, "AMR 주행 안전자세 복귀", ct);
                }
            }

            if (options.ReturnAmrToStart)
            {
                Report("AMR 시작 자세로 복귀…");
                try { await NavigateAndWaitAsync(startPose, options, Report, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Report($"AMR 시작 자세 복귀 실패({ex.Message}) — 표본은 그대로 유효합니다."); }
            }
        }
        catch (OperationCanceledException)
        {
            error = "사용자 중지.";
            Report(error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "ArUco 장착 자동보정 실패");
            Report($"실패: {error}");
        }
        finally
        {
            // 중단/실패로 끝났으면 AMR 주행을 먼저 끊는다 — /robot/go 로 낸 이동은 토큰 취소만으로는
            // 멈추지 않는다(명령은 이미 로봇 큐에 들어가 있다). 정상 종료 경로는 이미 정차 상태다.
            if (error is not null)
            {
                try
                {
                    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    if (!await _drive.StopAsync(stopCts.Token))
                        Report("AMR 주행 정지 명령이 거부됐습니다 — 물리 비상정지를 확인하세요.");
                }
                catch (Exception ex) { Report($"AMR 주행 정지 실패: {ex.Message} — 물리 비상정지를 확인하세요."); }
            }

            // 취소 토큰과 분리해 코봇을 안전자세로 복귀시킨다.
            if (travelPose is not null)
                returned = await ReturnToTravelPoseAsync(travelPose, travelJoints, tool, Report);
            await RestoreEntryToolAsync(entryTool, tool, Report);
            _gate.Exit();
        }

        bool enough = samples.Count >= 8 && visited >= 3;
        if (error is null && !enough)
            error = $"유효 표본/정차점이 부족합니다(표본 {samples.Count}, 정차점 {visited}; 최소 8개/3곳).";
        return new(error is null, error, samples, visited, skipped, returned);
    }

    private async Task CaptureViewVariantsAsync(List<ArucoMountSample> samples, double[] amrPose,
        double[] viewPose, double[] markerPose, ArucoSettings marker, int tool, double[] toolToCamera,
        ArucoMountAutoOptions options, Action<string> report, CancellationToken ct)
    {
        var pivot = HandEyeAutoRoutine.EstimatePivotInTool(toolToCamera, markerPose);
        foreach (double angle in new[] { -options.CobotTiltDeg, options.CobotTiltDeg })
        {
            ct.ThrowIfCancellationRequested();
            var offset = HandEyeAutoRoutine.BuildPivotOffsetPose([1, 0, 0], angle, pivot,
                options.MaxCobotTranslationMm);
            var rc = await _cobot.Rpc.MoveByToolOffsetAsync(viewPose, user: 0, offset,
                tool: tool, vel: CobotVelocityPct, ct: ct);
            if (rc != 0)
            {
                report($"코봇 시점 {angle:+0;-0}° 이동 실패(rc={rc}) — 이 시점은 건너뜁니다.");
                continue;
            }
            await Task.Delay(CobotSettleMs, ct);
            try
            {
                var captured = await _capture.CaptureAsync(marker, tool, options.FramesPerSample, ct);
                AddSample(samples, amrPose, captured);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { report($"코봇 시점 {angle:+0;-0}° 캡처 실패({ex.Message}) — 건너뜁니다."); }
            finally { await MoveCobotAsync(viewPose, tool, "정차점 기준 시점 복귀", ct); }
        }
    }

    /// <summary>
    /// AMR 을 목표 자세로 자동 주행시키고 정차까지 기다린다. 반환값은 <b>지령이 아니라 실제 정차 pose</b> —
    /// 표본의 <c>T_W_A</c> 기준이 되므로 지령값을 쓰면 정차 오차가 그대로 T_A_B 오차가 된다.
    ///
    /// ⚠ 예전 구현은 Modbus <c>PoseSearch</c>(Holding 19~25) + <c>ExecutionControl=Start</c> 였다. 그것은
    /// 주행이 아니라 <b>재측위</b>(+Task/Job 실행)이라 로봇은 제자리에 있고 자기 위치 추정만 목표로 옮긴다 —
    /// 보정 표본이 전부 "가짜 AMR 자세"가 된다. 주행은 <see cref="AmrDriveService"/>(REST /robot/go)로만 낸다.
    /// </summary>
    private async Task<RobotPose> NavigateAndWaitAsync(RobotPose target, ArucoMountAutoOptions options,
        Action<string> report, CancellationToken ct)
    {
        var r = await _drive.DriveToAsync(target.X, target.Y, target.Angle, new AmrDriveOptions
        {
            PollInterval = options.StatusPollInterval,
            Timeout = options.NavigationTimeout,
            StablePollCount = options.StablePollCount,
            MaxResidualMm = options.MaxArrivalResidualMm,
            MaxResidualDeg = options.MaxArrivalResidualDeg,
        }, report, ct);

        if (!r.Success)
            throw new InvalidOperationException($"AMR 주행 실패: {r.Error}");
        return r.Pose;
    }

    private async Task MoveCobotAsync(double[] pose, int tool, string label, CancellationToken ct)
    {
        var rc = await _cobot.Rpc.MoveLAsync(pose, tool: tool, user: 0, vel: CobotVelocityPct, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException($"{label} 실패(rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
    }

    private void EnsureLiftUnchanged(double? start)
    {
        if (start is { } h0 && _lift.Latest is { HeightMm: >= 0 } now && Math.Abs(now.HeightMm - h0) > 1)
            throw new InvalidOperationException($"텔레스코픽 높이가 변했습니다({h0:0.0} → {now.HeightMm:0.0}mm).");
    }

    private static void ValidateCobotMove(double[] from, double[] to, ArucoMountAutoOptions options)
    {
        var delta = FrameMath.MatrixToPose(FrameMath.Multiply(
            FrameMath.Invert(FrameMath.PoseToMatrix(from)), FrameMath.PoseToMatrix(to)));
        double translation = Math.Sqrt(delta[0] * delta[0] + delta[1] * delta[1] + delta[2] * delta[2]);
        double rotation = Math.Sqrt(delta[3] * delta[3] + delta[4] * delta[4] + delta[5] * delta[5]);
        if (translation > options.MaxCobotTranslationMm || rotation > options.MaxCobotRotationDeg)
            throw new InvalidOperationException(
                $"재조준 코봇 이동량이 안전 한도를 초과합니다(병진 {translation:0}mm, 회전 {rotation:0.0}°).");
    }

    /// <summary>초기 AMR 좌표축 기준으로 좌/우/전/후 4개 목표를 만든다.</summary>
    public static IReadOnlyList<RobotPose> BuildTargets(RobotPose start, double lateralM,
        double longitudinalM, double yawExcursionDeg)
    {
        double yaw = start.Angle;
        double excursion = Math.Clamp(Math.Abs(yawExcursionDeg), 20, 60) * Math.PI / 180;
        double lx = Math.Clamp(Math.Abs(lateralM), .15, .6);
        double ly = Math.Clamp(Math.Abs(longitudinalM), .15, .6);
        return new[]
        {
            Offset(start, -lx, 0, -excursion),
            Offset(start, +lx, 0, +excursion),
            Offset(start, 0, +ly, +excursion),
            Offset(start, 0, -ly, -excursion),
        };

        RobotPose Offset(RobotPose p, double x, double y, double dyaw) => new(
            (float)(p.X + Math.Cos(yaw) * x - Math.Sin(yaw) * y),
            (float)(p.Y + Math.Sin(yaw) * x + Math.Cos(yaw) * y),
            (float)NormalizeRad(yaw + dyaw));
    }

    /// <summary>고정 월드 카메라 pose를 새 AMR 자세에서 재현하는 T_B_T를 계산한다.</summary>
    public static double[] CalculateViewToolPose(RobotPose amrPose, double[] initialMount,
        double[] toolToCamera, double[,] fixedCameraW)
    {
        var wab = FrameMath.Multiply(FrameMath.PoseToMatrix(ToPose(amrPose)),
            FrameMath.PoseToMatrix(initialMount));
        return FrameMath.MatrixToPose(FrameMath.Multiply(
            FrameMath.Multiply(FrameMath.Invert(wab), fixedCameraW),
            FrameMath.Invert(FrameMath.PoseToMatrix(toolToCamera))));
    }

    private static void AddSample(List<ArucoMountSample> samples, double[] wa, HandEyeSample sample) =>
        samples.Add(new(samples.Count, sample.CapturedAtUtc ?? DateTime.UtcNow,
            (double[])wa.Clone(), sample.TcpPose, sample.MarkerPose, sample.ReprojErrPx));

    private static double[] ToPose(RobotPose p) =>
        [p.X * 1000, p.Y * 1000, 0, 0, 0, p.Angle * 180 / Math.PI];

    private static double NormalizeRad(double value)
    {
        while (value > Math.PI) value -= 2 * Math.PI;
        while (value <= -Math.PI) value += 2 * Math.PI;
        return value;
    }
}

public sealed class ArucoMountAutoOptions
{
    public double LateralTravelM { get; init; } = .25;
    public double LongitudinalTravelM { get; init; } = .25;
    public double YawExcursionDeg { get; init; } = 45;
    public double CobotTiltDeg { get; init; } = 8;
    public int FramesPerSample { get; init; } = 5;
    /// <summary>정차 후 목표와의 허용 잔차. AMR 은 이 정도 오차로 서는 것이 정상이고, 표본 기준은
    /// <b>실제 정차 pose</b> 라 정밀 도달이 필요 없다 — 마커가 FOV 를 벗어날 만큼 빗나갔는지만 본다.</summary>
    public double MaxArrivalResidualMm { get; init; } = 500;
    public double MaxArrivalResidualDeg { get; init; } = 10;
    public int StablePollCount { get; init; } = 3;
    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public double MaxCobotTranslationMm { get; init; } = 600;
    public double MaxCobotRotationDeg { get; init; } = 75;
    public bool ReturnAmrToStart { get; init; } = true;
}

public sealed record ArucoMountAutoResult(bool Success, string? Error,
    IReadOnlyList<ArucoMountSample> Samples, int VisitedStations, int SkippedStations,
    bool ReturnedToTravelPose)
{
    public static ArucoMountAutoResult Fail(string error) =>
        new(false, error, Array.Empty<ArucoMountSample>(), 0, 0, true);
}
