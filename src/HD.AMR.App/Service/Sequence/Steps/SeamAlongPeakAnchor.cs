using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 다른 줄 좌표계 재사용에서 <b>용접선 방향 위치</b>를 실측으로 맞춘다 — 교시한 줄과 직각인 줄(예: 수평 줄을 교시한 뒤의 수직 줄)로
/// 처음 넘어갔을 때 한 번, 좌표계 원점 자리의 코루게이션 Peak 를 depth 로 찾아 원점이 그 위에 오게 프레임 X 로 옮길 양을 잰다.
///
/// 교시한 줄은 ⑤⑥ Peak 찾기·센터링으로 원점이 Peak 위에 놓이지만, 물려받은 직각 줄은 "교시 줄 비드 높이 + ACS 도면 거리"로만
/// 원점을 잡아 용접선 방향으로는 한 번도 재지 않는다(실기 2026-11-24 M2: 수직 줄 경로가 위로 약 10mm 밀려 시작이 코루게이션에서
/// 멀고 위 코루게이션 오르막에서 팁이 닿음). 같은 면의 다른 열은 같은 코루게이션을 지나므로 한 번 맞춘 좌표계를 그대로 옮겨 쓴다
/// (<see cref="InternalWorkpieceFrame.AlongAnchorBagKey"/>, 격자 기울기는 <see cref="InternalWorkpieceFrame.MoveToOtherLine"/> 가 따라간다).
///
/// ⑱ 검사 수행이 첫 경유점에 선 직후(순회 자세 — 용접선이 영상 가로, 검사 팁 진입 전) 부른다. 순서:
///   ① 원점 정면(지금 높이·자세 그대로)에서 검사캠 장착 오프셋만큼 옮겨 원점 자리를 depth 영상 중심에 둔다(④⁺⁺ 와 같은 이동).
///   ② Peak 를 찾아 진행축 이격을 잰다. 이격이 크면(<see cref="RefineMinMm"/>) 그만큼 옮겨 한 번 더 재서 환산 오차를 줄인다.
///   ③ 이격을 프레임 X 성분으로 바꿔 돌려준다. 한계(<see cref="MaxShiftKey"/>)를 넘거나 못 찾으면 null — 좌표계는 그대로.
/// 끝난 자리에서 돌아오는 이동은 부른 쪽이 한다(첫 경유점으로 다시 선다).
/// </summary>
public class SeamAlongPeakAnchor
{
    /// <summary>
    /// 사용 파라미터 키(true/false) — 비우면 검사 센서 모드로 정한다(가스 켬, 비전 끔).
    /// </summary>
    public const string EnableKey = "inspection.reuseFrameAlongPeakSearch";

    /// <summary>보정 한계 [mm] 파라미터 키 — 이보다 큰 이격은 옆 Peak 를 잡았거나 좌표계가 크게 틀린 것으로 보고 보정하지 않는다.</summary>
    public const string MaxShiftKey = "inspection.reuseFrameAlongPeakMaxMm";
    public const double DefaultMaxShiftMm = 40.0;
    internal const double MinMaxShiftMm = 5.0, MaxMaxShiftMm = 100.0;

    /// <summary>이보다 큰 이격은 그만큼 옮겨 다시 잰다 [mm] — 영상 중심에서 멀수록 mm 환산 오차가 커진다.</summary>
    internal const double RefineMinMm = 15.0;

    /// <summary>이보다 작은 보정은 하지 않는다 [mm] — Peak 측정 흔들림.</summary>
    internal const double MinShiftMm = 1.0;

    /// <summary>광축이 좌표계 면 법선에서 이만큼 넘게 기울면 재지 않는다 [도] — 영상 평면 이동이 면을 따라가지 않는다.</summary>
    internal const double MaxOpticalTiltDeg = 10.0;

    /// <summary>진행축 이격 중 프레임 X 성분이 이 비율보다 작으면 용접선이 영상 진행축과 나란하지 않은 것으로 본다.</summary>
    internal const double MinAlongRatio = 0.9;

    /// <summary>
    /// 측정하러 옆으로 움직일 때 좌표계 면(교시 때 TCP 평면)에서 바깥으로 떨어져 있어야 하는 최소 높이 [mm]. 첫 경유점이 이보다
    /// 낮으면 먼저 이만큼 들어 올린 뒤 옆으로 간다 — 교시의 피치 이동 들어올림(30mm)·줄 사이 연속 이동(면 위 약 30mm)과 같은 높이라
    /// 검사 팁이 코루게이션을 넘어간다. 첫 경유점이 더 높으면 그 높이 그대로 간다.
    /// </summary>
    internal const double MinViewHeightMm = 30.0;

    /// <summary>
    /// <paramref name="pose"/> 를 좌표계 면 바깥 방향(<paramref name="outward"/>)으로 옮겨 높이가 <see cref="MinViewHeightMm"/> 이상이
    /// 되게 한 자세. 이미 그보다 높으면 그대로(같은 값의 새 배열).
    /// </summary>
    internal static double[] LiftedPose(double[] frameBase, double[] pose, double[] outward)
    {
        var h = LineSurfaceHeightStep.HeightOutMm(frameBase, pose, outward);
        return LineSurfaceHeightStep.Offset(pose, outward, Math.Max(0.0, MinViewHeightMm - h));
    }

    /// <summary>
    /// 측정 뒤 첫 경유점으로 돌아갈 때 먼저 설 자리 — <paramref name="pose"/> 바로 위 측정 높이(<see cref="LiftedPose"/>).
    /// 옆 이동을 그 높이에서 끝낸 뒤 제자리에서 내려가게 한다. 첫 경유점이 이미 그 높이 이상이면 같은 자세.
    /// </summary>
    public async Task<double[]> LiftedOverAsync(SequenceContext context, double[] frame, double[] pose)
    {
        var (optical, _) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        return LiftedPose(frame, pose, LineSurfaceHeightStep.OutwardNormal(frame, pose, context.OpticalAxis ?? optical));
    }

    private const int SettleMs = 800;
    private const int RetryDelayMs = 500;

    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly WeldTrackingService _weld;
    private readonly ParameterService _param;
    private readonly ILogger<SeamAlongPeakAnchor> _logger;

    public SeamAlongPeakAnchor(CobotService cobot, CameraService camera, WeldTrackingService weld,
        ParameterService param, ILogger<SeamAlongPeakAnchor> logger)
    {
        _cobot = cobot;
        _camera = camera;
        _weld = weld;
        _param = param;
        _logger = logger;
    }

    /// <summary>파라미터 값 → 사용 여부. 비면 센서 모드 기본값(가스 켬 / 비전 끔).</summary>
    public static bool ResolveEnabled(bool? value, bool usesGas) => value ?? usesGas;

    /// <summary>파라미터 값 → 보정 한계 [mm]. 비었거나 범위 밖이면 기본값.</summary>
    public static double ResolveMaxShiftMm(double? value)
        => value is { } v && double.IsFinite(v) && v >= MinMaxShiftMm && v <= MaxMaxShiftMm ? v : DefaultMaxShiftMm;

    /// <summary>
    /// 원점 정면 자세 — <paramref name="stagedPose"/> 의 자세와 좌표계 면 기준 높이를 그대로 두고 위치만 원점 위로 옮긴 뒤,
    /// 툴 좌표 오프셋 <paramref name="camShiftTool"/>(검사캠 장착 오프셋, 영상 +방향)만큼 옮긴다 — 원점 자리가 depth 영상 중심에 온다.
    /// </summary>
    internal static double[] ViewPose(double[] frameBase, double[] stagedPose, double[] camShiftTool)
    {
        var f = FrameMath.PoseToMatrix(frameBase);
        var h = (stagedPose[0] - frameBase[0]) * f[0, 2] + (stagedPose[1] - frameBase[1]) * f[1, 2] + (stagedPose[2] - frameBase[2]) * f[2, 2];
        var over = new[]
        {
            frameBase[0] + h * f[0, 2], frameBase[1] + h * f[1, 2], frameBase[2] + h * f[2, 2],
            stagedPose[3], stagedPose[4], stagedPose[5],
        };
        return FrameMath.FromFrame(new[] { camShiftTool[0], camShiftTool[1], camShiftTool[2], 0.0, 0.0, 0.0 }, over);
    }

    /// <summary>
    /// 툴 좌표 이동량 <paramref name="toolOffset"/>(Peak 를 영상 중심에 놓으려면 툴이 가야 하는 양)을 좌표계 축 성분으로 —
    /// (프레임 X = 용접선 방향, 프레임 Y = 옆) [mm].
    /// </summary>
    internal static (double AlongMm, double LateralMm) ToFrameComponents(double[] frameBase, double[] toolPose, double[] toolOffset)
    {
        var t = FrameMath.PoseToMatrix(toolPose);
        var f = FrameMath.PoseToMatrix(frameBase);
        var b = new double[3];
        for (var i = 0; i < 3; i++) b[i] = t[i, 0] * toolOffset[0] + t[i, 1] * toolOffset[1] + t[i, 2] * toolOffset[2];
        return (b[0] * f[0, 0] + b[1] * f[1, 0] + b[2] * f[2, 0], b[0] * f[0, 1] + b[1] * f[1, 1] + b[2] * f[2, 1]);
    }

    /// <summary>광축(툴 축)과 좌표계 면 법선 사이 각 [도] — 방향 부호는 보지 않는다.</summary>
    internal static double OpticalTiltDeg(double[] frameBase, double[] toolPose, ToolAxisDir opticalAxis)
    {
        var t = FrameMath.PoseToMatrix(toolPose);
        var f = FrameMath.PoseToMatrix(frameBase);
        var col = (int)opticalAxis / 2;
        var dot = t[0, col] * f[0, 2] + t[1, col] * f[1, 2] + t[2, col] * f[2, 2];
        return Math.Acos(Math.Clamp(Math.Abs(dot), 0, 1)) * 180.0 / Math.PI;
    }

    /// <summary>잰 이격을 보정량으로 — 한계를 넘으면 null(사유), 너무 작으면 0.</summary>
    internal static (double? Mm, string? Reject) Decide(double alongMm, double lateralMm, double measuredMm, double maxShiftMm)
    {
        if (!double.IsFinite(alongMm))
            return (null, "측정값 이상");
        if (Math.Abs(measuredMm) >= MinShiftMm && Math.Abs(alongMm) < MinAlongRatio * Math.Abs(measuredMm))
            return (null, $"진행축 이격 {measuredMm:+0.0;-0.0}mm 중 용접선 방향 성분이 {alongMm:+0.0;-0.0}mm 뿐(옆 {lateralMm:+0.0;-0.0}mm) — 용접선이 영상 진행축과 나란하지 않음");
        if (Math.Abs(alongMm) > maxShiftMm)
            return (null, $"이격 {alongMm:+0.0;-0.0}mm 가 한계 ±{maxShiftMm:0}mm 초과 — 옆 Peak 를 잡았거나 좌표계가 크게 틀림");
        return (Math.Abs(alongMm) < MinShiftMm ? 0.0 : alongMm, null);
    }

    /// <summary>
    /// 좌표계 원점을 프레임 X 로 옮길 양 [mm](+ = 용접선 시작→끝 방향)을 잰다. 0 = 이미 맞음. null = 재지 못함(좌표계 그대로, Note 에 사유).
    /// 코봇은 측정 자리에 남는다 — 부른 쪽이 첫 경유점으로 다시 세운다.
    /// </summary>
    public async Task<(double? AlongMm, string Note)> MeasureAsync(
        SequenceContext context, double[] frame, double[] stagedPose, CancellationToken ct)
    {
        if (!_camera.IsConnected || _camera.LatestIr is null)
            return (null, "카메라 미연결/IR 프레임 없음");

        var (axX, _) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
        var (axY, _) = await WeldSequenceSupport.GetImageYAxisAsync(_param);
        var (optical, _) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        if (IsDepthAxis(axX) || IsDepthAxis(axY))
            return (null, "영상 축 매핑이 툴 Z 를 가리킴");
        var tiltDeg = OpticalTiltDeg(frame, stagedPose, context.OpticalAxis ?? optical);
        if (tiltDeg > MaxOpticalTiltDeg)
            return (null, $"첫 경유점 자세의 광축이 좌표계 면 법선에서 {tiltDeg:0.0}° 기움(한계 {MaxOpticalTiltDeg:0}°)");

        var maxShiftMm = DefaultMaxShiftMm;
        try { maxShiftMm = ResolveMaxShiftMm(await _param.GetDoubleAsync(MaxShiftKey)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* DB 미준비 — 기본값 */ }

        var (offX, offY) = await WeldSequenceSupport.GetInspectCamOffsetAsync(_param);
        var camShift = WeldSequenceSupport.ImageShiftToToolOffset(axX, axY, offX, offY);
        // 옆으로 가기 전에 높이부터 — 첫 경유점이 낮으면 제자리에서 들어 올린 뒤 그 높이로 간다(검사 팁이 코루게이션을 넘게).
        var outward = LineSurfaceHeightStep.OutwardNormal(frame, stagedPose, context.OpticalAxis ?? optical);
        var lifted = LiftedPose(frame, stagedPose, outward);
        var liftMm = LineSurfaceHeightStep.HeightOutMm(frame, lifted, outward) - LineSurfaceHeightStep.HeightOutMm(frame, stagedPose, outward);
        var view = ViewPose(frame, lifted, camShift);
        _logger.LogInformation(
            "[용접선 방향 Peak 맞춤] 원점 [{X:0.0}, {Y:0.0}, {Z:0.0}] 정면으로 이동 — 면 위 높이 {H:0.0}mm{Lift}, 검사캠 오프셋 툴 [{Ox:+0.0;-0.0}, {Oy:+0.0;-0.0}]mm, 목표 TCP [{Vx:0.0}, {Vy:0.0}, {Vz:0.0}]",
            frame[0], frame[1], frame[2], LineSurfaceHeightStep.HeightOutMm(frame, lifted, outward),
            liftMm > 0.05 ? $"(첫 경유점에서 {liftMm:0.0}mm 들어 올림, 최소 {MinViewHeightMm:0}mm)" : "",
            camShift[0], camShift[1], view[0], view[1], view[2]);
        context.Progress?.Invoke("용접선 방향 Peak 맞춤 — 원점 자리 코루게이션 찾기");
        if (liftMm > 0.05)
        {
            var rcLift = await _cobot.Rpc.MoveLAsync(lifted, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
            if (rcLift != 0)
                return (null, $"측정 높이로 들어 올리기 실패 (rc={rcLift}){FairinoErrorCodes.Suffix(rcLift)}");
        }
        var rc = await _cobot.Rpc.MoveLAsync(view, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
            return (null, $"원점 정면 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}");
        await Task.Delay(SettleMs, ct);

        var (progressAxis, axisSrc) = await WeldSequenceSupport.ApplyProgressAxisAsync(_weld, context, _cobot, _param, ct);
        var (toolAxis, _) = await WeldSequenceSupport.GetProgressToolAxisAsync(_param, progressAxis);
        var first = await FindAsync(ct);
        if (first is not { } m1)
            return (null, "Peak 미검출");

        var offset1 = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset1, toolAxis, m1.Mm);
        var here = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var (along, lateral) = ToFrameComponents(frame, here, offset1);
        var measured = m1.Mm;
        _logger.LogInformation(
            "[용접선 방향 Peak 맞춤] Peak 이격 {Mm:+0.0;-0.0}mm ({Px:+0.0;-0.0}px, 깊이 {D}mm, conf {C:0.00}, ROI 내 Peak {N}개, {Scale}) — 진행축={Axis}({Src}), 툴{Tool} → 용접선 방향 {A:+0.0;-0.0}mm / 옆 {L:+0.0;-0.0}mm",
            m1.Mm, m1.Result.OffsetPx, m1.Result.DepthMm, m1.Result.Confidence, m1.Result.PeakCount, m1.Scale,
            progressAxis, axisSrc, FlatSurfaceCenteringService.AxisName(toolAxis), along, lateral);

        // 이격이 크면 Peak 를 영상 중심으로 옮겨 다시 잰다 — 옮긴 양 + 남은 이격이 실제 이격이다.
        if (Math.Abs(m1.Mm) >= RefineMinMm && Decide(along, lateral, measured, maxShiftMm).Reject is null)
        {
            var rcMove = await _cobot.Rpc.MoveByToolOffsetAsync(here, user: 0, offset1, tool: context.Tool, vel: context.Velocity, ct: ct);
            if (rcMove != 0)
                _logger.LogWarning("[용접선 방향 Peak 맞춤] 재측정 이동 실패 (rc={Rc}){Desc} — 첫 측정값을 씁니다.", rcMove, FairinoErrorCodes.Suffix(rcMove));
            else
            {
                await Task.Delay(SettleMs, ct);
                if (await FindAsync(ct) is { } m2 && Math.Abs(m2.Mm) < Math.Abs(m1.Mm))
                {
                    var offset2 = new double[6];
                    FlatSurfaceCenteringService.ApplyAxis(offset2, toolAxis, m1.Mm + m2.Mm);
                    (along, lateral) = ToFrameComponents(frame, here, offset2);
                    measured = m1.Mm + m2.Mm;
                    _logger.LogInformation(
                        "[용접선 방향 Peak 맞춤] {Move:+0.0;-0.0}mm 옮겨 재측정 — 남은 이격 {Res:+0.0;-0.0}mm → 용접선 방향 {A:+0.0;-0.0}mm",
                        m1.Mm, m2.Mm, along);
                }
                else
                    _logger.LogWarning("[용접선 방향 Peak 맞춤] 재측정에서 Peak 를 놓쳤거나 이격이 줄지 않음 — 첫 측정값을 씁니다.");
            }
        }

        var (mm, reject) = Decide(along, lateral, measured, maxShiftMm);
        if (mm is not { } shift)
            return (null, reject!);
        return (shift, shift == 0
            ? $"Peak 이격 {along:+0.0;-0.0}mm — 보정 불필요"
            : $"Peak 이격 {along:+0.0;-0.0}mm(한계 ±{maxShiftMm:0}mm)");
    }

    private async Task<(double Mm, PeakFindResult Result, string Scale)?> FindAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryDelayMs, ct);
            var (roi, _) = await WeldSequenceSupport.GetPeakRoiAsync(_param, _camera);
            if (roi is null) continue;
            var r = await _weld.FindPeakAsync(roi, ct);
            if (!r.Found)
            {
                _logger.LogInformation("[용접선 방향 Peak 맞춤] Peak 미검출({N}회차) — {Msg}", attempt + 1, r.Message);
                continue;
            }
            var (mm, scale) = WeldSequenceSupport.ResolveOffsetMm(r, _weld, _camera);
            return (mm, r, scale);
        }
        return null;
    }

    private static bool IsDepthAxis(ToolAxisDir axis) => axis is ToolAxisDir.PlusZ or ToolAxisDir.MinusZ;
}
