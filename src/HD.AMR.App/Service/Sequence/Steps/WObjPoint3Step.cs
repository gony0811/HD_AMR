using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑯⁻ 작업물 좌표계 점3(면 기울기) 측정 — CROSS4 가 든 묶음·용접선에서만 돈다(오케스트레이터가 <see cref="RequiredBagKey"/> 를 켜고
/// 이 스텝을 붙인다).
///
/// 2점 좌표계는 X(점1→점2)는 두 점으로 실측하지만, 용접선과 수직인 면 안 방향(Y)의 기울기는 점1 한 곳의 레이저 수평 자세에만
/// 기댄다. CROSS4 교차 arm 은 Y 로 수백 mm 가므로 그 기울기 오차가 거리 오차가 되어 초점이 나간다(2026-11-21 현장 지적).
/// 점2 자리에서 작업물 Y 로 <see cref="OffsetMm"/> 옮겨 평탄면을 찾고 레이저로 면 거리를 재, 점1 대비 면 높이 차로 Y 기울기를
/// 잡아 <see cref="NormalBagKey"/> 에 남긴다 — <see cref="WObjRegisterStep"/> 이 Z 기준으로 쓴다. 끝나면 출발 자세로 돌아온다.
/// 측정을 못 하면 경고만 남기고 종전 2점 좌표계로 진행한다(검사를 멈추지 않는다). 출발 자세 복귀만 실패하면 단계 실패.
/// </summary>
public class WObjPoint3Step : ISequenceStep
{
    public const string StepKey = "wobjPoint3";

    /// <summary>오케스트레이터가 켜는 실행 요청 플래그(bool).</summary>
    public const string RequiredBagKey = "wobj.point3Required";

    /// <summary>점3 으로 보정한 면 법선(double[3], BASE, 바깥쪽).</summary>
    public const string NormalBagKey = "wobj.point3Normal";

    /// <summary>점3 보정각(double, 도 — 작업물 X축 둘레).</summary>
    public const string RollBagKey = "wobj.point3RollDeg";

    /// <summary>점2 에서 작업물 Y 로 옮길 거리 [mm] — CROSS4 교차 arm 길이 수준.</summary>
    internal const double OffsetMm = 250.0;

    /// <summary>이보다 크게 기울었다고 나오면 측정 이상으로 보고 쓰지 않는다 [도].</summary>
    internal const double MaxRollDeg = 5.0;

    /// <summary>점3 레이저 측정면이 이보다 기울면(3채널) 평탄면이 아닌 것으로 보고 쓰지 않는다 [도].</summary>
    internal const double MaxLaserTiltDeg = 5.0;

    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ParameterService _param;
    private readonly PostureLimitsService _limits;
    private readonly ILogger<WObjPoint3Step> _logger;

    public WObjPoint3Step(CobotService cobot, CameraService camera, FlatSurfaceCenteringService centering,
        LaserDisplacementSensorService laser, ParameterService param, PostureLimitsService limits,
        ILogger<WObjPoint3Step> logger)
    {
        _cobot = cobot;
        _camera = camera;
        _centering = centering;
        _laser = laser;
        _param = param;
        _limits = limits;
        _logger = logger;
    }

    public string Key => StepKey;
    public string DisplayName => "작업물 좌표계 점3(면 기울기) 측정";
    public int DefaultOrder => 1165;   // 점2 기록(1160) 뒤, 좌표계 계산(1170) 앞

    public StepValidation Validate(SequenceContext context)
        => _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");

    /// <summary>점2 위치에서 점3 으로 옮길 BASE 이동량 — 2점 좌표계의 작업물 Y × <paramref name="offsetMm"/>.</summary>
    internal static double[]? LateralOffsetBase(double[] p1, double[] p2, double offsetMm)
    {
        var x = new[] { p2[0] - p1[0], p2[1] - p1[1], p2[2] - p1[2] };
        var xl = Math.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]);
        if (xl < 1e-6) return null;
        for (var i = 0; i < 3; i++) x[i] /= xl;
        var zp = FrameMath.FromFrame(new[] { 0.0, 0.0, 50.0, 0.0, 0.0, 0.0 }, p1);
        var z = new[] { zp[0] - p1[0], zp[1] - p1[1], zp[2] - p1[2] };
        var zx = z[0] * x[0] + z[1] * x[1] + z[2] * x[2];
        for (var i = 0; i < 3; i++) z[i] -= zx * x[i];
        var zl = Math.Sqrt(z[0] * z[0] + z[1] * z[1] + z[2] * z[2]);
        if (zl < 1e-6) return null;
        for (var i = 0; i < 3; i++) z[i] /= zl;
        var y = new[] { z[1] * x[2] - z[2] * x[1], z[2] * x[0] - z[0] * x[2], z[0] * x[1] - z[1] * x[0] };
        return new[] { y[0] * offsetMm, y[1] * offsetMm, y[2] * offsetMm };
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        context.Bag.Remove(NormalBagKey);
        context.Bag.Remove(RollBagKey);
        if (!(context.Bag.TryGetValue(RequiredBagKey, out var req) && req is true))
            return StepResult.Ok("CROSS4 없음 — 점3 생략(2점 좌표계).");
        if (!(context.Bag.TryGetValue(WObjPointStep.PointPoseBagKey(1), out var a) && a is double[] { Length: 6 } p1)
            || !(context.Bag.TryGetValue(WObjPointStep.PointPoseBagKey(2), out var b) && b is double[] { Length: 6 } p2))
            return Skip("점1·점2 기록 없음");
        if (!_camera.IsConnected || _camera.LatestDepth is null) return Skip("깊이 카메라 없음");
        if (!_laser.IsConnected) return Skip("레이저 변위센서 미연결");

        var start = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        string? lastError = null;
        foreach (var sign in new[] { +1, -1 })
        {
            if (LateralOffsetBase(p1, p2, sign * OffsetMm) is not { } move) return Skip("점1·점2 가 겹쳐 방향을 정할 수 없음");
            _logger.LogInformation("[점3] 작업물 Y {Sign}{Off:0}mm 로 이동 (BASE [{Dx:+0.0;-0.0}, {Dy:+0.0;-0.0}, {Dz:+0.0;-0.0}])",
                sign > 0 ? "+" : "−", OffsetMm, move[0], move[1], move[2]);
            context.Progress?.Invoke($"점3(면 기울기): 작업물 Y {(sign > 0 ? "+" : "−")}{OffsetMm:0}mm 로 이동");
            var rc = await _cobot.Rpc.MoveByOffsetAsync(start, user: 0, new[] { move[0], move[1], move[2], 0.0, 0.0, 0.0 },
                tool: context.Tool, vel: context.Velocity, ct: ct);
            if (rc != 0)
            {
                lastError = $"Y {(sign > 0 ? "+" : "−")} 이동 실패 (rc={rc}{FairinoErrorCodes.Suffix(rc)})";
                _logger.LogWarning("[점3] {Msg} — 반대쪽 시도", lastError);
                if (await ReturnAsync(start, context, ct) is { } back) return back;
                continue;
            }

            var (measured, error) = await MeasureHereAsync(context, ct);
            if (await ReturnAsync(start, context, ct) is { } failBack) return failBack;
            if (measured is null)
            {
                lastError = error;
                _logger.LogWarning("[점3] Y {Sign} 측정 실패: {Msg}", sign > 0 ? "+" : "−", error);
                continue;
            }

            var (laserPos, laserZ) = measured.Value;
            var (wd, wdSrc) = await LaserWorkingDistanceSetting.ResolveAsync(_param, LaserWorkingDistanceSetting.WallOf(context));
            if (InternalWorkpieceFrame.Point3Normal(p1, p2, laserPos, laserZ, wd) is not { } r)
                return Skip("점3 이 작업물 Y 로 충분히 떨어지지 않음");
            if (Math.Abs(r.RollDeg) > MaxRollDeg)
                return Skip($"보정각 {r.RollDeg:+0.00;-0.00}° 가 한계 ±{MaxRollDeg:0}° 초과 — 측정 이상으로 보고 쓰지 않음 " +
                            $"(높이 {r.HeightMm:+0.0;-0.0}mm / Y {r.LateralMm:+0;-0}mm)");

            context.Bag[NormalBagKey] = r.NormalBase;
            context.Bag[RollBagKey] = r.RollDeg;
            _logger.LogInformation(
                "[점3] 면 기울기 실측: Y {Lat:+0;-0}mm 에서 면 높이 {H:+0.0;-0.0}mm → X축 둘레 {Roll:+0.00;-0.00}° 보정 " +
                "(레이저 {Z:0.0}mm, WD 목표 {Wd:0.0}mm {Src})",
                r.LateralMm, r.HeightMm, r.RollDeg, laserZ, wd, wdSrc);
            return StepResult.Ok(
                $"점3 면 기울기 실측 — Y {r.LateralMm:+0;-0}mm 에서 높이 {r.HeightMm:+0.0;-0.0}mm, " +
                $"X축 둘레 {r.RollDeg:+0.00;-0.00}° 보정해 좌표계를 잡습니다.");
        }
        return Skip(lastError ?? "측정 실패");
    }

    private StepResult Skip(string reason)
    {
        _logger.LogWarning("[점3] 생략 — {Reason}. 2점 좌표계로 진행합니다.", reason);
        return StepResult.Ok($"점3 생략 — {reason}. 2점 좌표계로 진행.");
    }

    /// <summary>출발 자세로 직선 복귀. 실패하면 위치가 틀어진 채라 단계 실패.</summary>
    private async Task<StepResult?> ReturnAsync(double[] start, SequenceContext context, CancellationToken ct)
    {
        var rc = await _cobot.Rpc.MoveLAsync(start, tool: context.Tool, user: 0,
            vel: context.Velocity, acc: 100, ovl: 100, blendR: -1, ct: ct);
        return rc == 0 ? null
            : StepResult.Fail($"점3 측정 뒤 점2 자리 복귀 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — 코봇 위치를 확인하세요.");
    }

    /// <summary>현재 자리에서 평탄면을 찾아 레이저 중심으로 옮긴 뒤 면 거리를 잰다(자세는 바꾸지 않는다).</summary>
    private async Task<((double[] Pos, double Z)? Result, string? Error)> MeasureHereAsync(SequenceContext context, CancellationToken ct)
    {
        await Task.Delay(800, ct);   // 진동·깊이 프레임 갱신 대기
        var (rx, ry, rw, rh, roiSrc) = await CameraAlignStep.GetDepthRoiAsync(_param);
        var (axisX, _) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
        var (axisY, _) = await WeldSequenceSupport.GetImageYAxisAsync(_param);
        var shiftY = context.CameraToLaserShiftYmm;
        var footprint = await LaserFootprintOptions.LoadAsync(_param);
        var jointVelScale = await FlatMoveSettings.LoadJointVelocityScaleAsync(_param);
        var options = new FlatCenterAlignOptions
        {
            RoiX = rx, RoiY = ry, RoiW = rw, RoiH = rh,
            GridSize = 5,
            SamplesPerDetect = 10,
            DeadbandUv = footprint.Enabled ? 0.0 : 0.02,
            ToleranceMm = footprint.Enabled ? 1.5 : 0.0,
            MaxIterations = 1, VerifyAfterMove = false,
            ImageXAxis = axisX, ImageYAxis = axisY,
            Tool = context.Tool, Velocity = context.Velocity,
            Source = "점3",
            Candidates = await FlatCandidateOptions.LoadAsync(_param),
            Reach = new FlatReachOptions
            {
                Limits = await _limits.GetAsync(),
                ImageXAxis = axisX, ImageYAxis = axisY,
                CameraToLaserShiftYmm = shiftY,
                TiltMarginDeg = await _param.GetDoubleAsync(FlatReachOptions.TiltMarginKey) ?? FlatReachOptions.DefaultTiltMarginDeg,
                Tool = context.Tool,
            },
            Footprint = new FlatFootprintContext
            {
                Options = footprint,
                ImageXAxis = axisX, ImageYAxis = axisY,
                Excluded = Array.Empty<(double U, double V)>(),
            },
            UseJointMove = true,
            JointVelocityScale = jointVelScale,
        };
        var align = await _centering.RunAsync(options, context.Progress, ct);
        ct.ThrowIfCancellationRequested();
        if (!align.Success) return (null, $"평탄면 탐색 실패 (ROI={roiSrc}) — {align.Message}");

        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var shift = await _centering.MoveToolOffsetAsync(anchor, new[] { 0.0, shiftY, 0.0, 0.0, 0.0, 0.0 },
            context.Tool, context.Velocity, joint: true, ct, jointVelScale);
        if (!shift.Ok) return (null, $"레이저 중심 이동 실패 {shift.Describe()}");

        await Task.Delay(500, ct);
        await _laser.WaitForSettledAsync(20, 50, 3000, 0.05, 2.0, ct);
        var (pose, noise) = await _laser.MeasurePlaneAsync(20, 50, ct);
        if (!pose.Valid) return (null, $"레이저 측정 실패 — {pose.Note}");
        if (Math.Abs(pose.Rx) > MaxLaserTiltDeg || Math.Abs(pose.Ry) > MaxLaserTiltDeg)
            return (null, $"레이저 측정면 기울기 rx={pose.Rx:0.##}°, ry={pose.Ry:0.##}° — 평탄면이 아님(한계 ±{MaxLaserTiltDeg:0}°)");
        var pos = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        _logger.LogInformation("[점3] 레이저 z={Z:0.00}mm rx={Rx:0.##}° ry={Ry:0.##}° ({Noise}), TCP [{X:0.0}, {Y:0.0}, {Zt:0.0}]",
            pose.Z, pose.Rx, pose.Ry, noise.ShortText(), pos[0], pos[1], pos[2]);
        return ((pos, pose.Z), null);
    }
}
