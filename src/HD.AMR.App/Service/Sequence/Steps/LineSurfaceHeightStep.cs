using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Motion;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑰⁻ 줄 시작·끝 면 높이 측정 — 다른 줄 좌표계 재사용(<see cref="InternalWorkpieceFrame.ReuseAcrossLinesKey"/>)에서
/// 수직·수평 용접선이 <b>하나의</b> 작업물 좌표계를 같이 쓰도록, 줄마다 레이저로 면 높이를 재서 좌표계를 맞춘다.
///
/// 두 가지 모드(오케스트레이터가 <see cref="RequestBagKey"/> 로 정한다):
///   · <see cref="ModeReference"/> — 좌표계를 새로 교시한 줄. 지금 자리에서 면 높이(좌표계 Z 기준)를 한 번 재서
///     <see cref="ReferenceHeightBagKey"/> 에 남긴다. 좌표계는 바꾸지 않는다.
///   · <see cref="ModeCorrect"/> — 좌표계를 물려받은 줄. 시작점과 (좌표계 X 를 따라 용접선 길이만큼 간) 끝점에서 면 높이를
///     재서 ① 기울기(시작→끝 높이 차)와 ② 높이(시작점이 교시 줄의 기준 높이와 같게)를 좌표계에 반영한다
///     (<see cref="InternalWorkpieceFrame.CorrectBySurfaceHeights"/>).
///
/// 측정 자리는 교시(점2)와 같은 방법으로 잡는다 — ① 좌표계 면에서 <see cref="MeasureHeightKey"/>(기본 0mm = 교시 높이, 레이저가
/// WD 근처를 읽는 높이)로 옮기고, ② depth 로 평탄면을 찾아 카메라를 그 위에 놓은 뒤(<see cref="FlatSurfaceCenteringService.RunAsync"/>),
/// ③ 레이저 중심 보정(툴 Y)만큼 옮겨 레이저 3점 거리를 잰다. 종전에는 서 있던 높이에서 30mm 더 띄워 정해진 자리(±40mm)만 쟀는데,
/// 레이저가 측정 범위(약 450mm)를 넘거나 코로게이션 경사에 걸려 한 번도 재지 못했다(2026-11-22 M2).
/// 측정점은 레이저 평면 중심(툴 좌표 X·Y·Z)을 BASE 로 바꾼 실제 면 위 점이라 툴 자세·장착 오프셋·평탄 자리까지의 옆 이동이
/// 높이에 섞이지 않는다(같은 방법으로 잰 기준과 비교하므로 일정한 오프셋은 상쇄된다). 레이저 평면 법선이 좌표계 Z 와
/// <see cref="MaxPlaneTiltDeg"/> 넘게 기울면 그 자리는 버리고 다른 평탄 자리로 다시 잰다. 평탄면 탐색을 쓸 수 없거나
/// (<see cref="FlatSearchKey"/>=false·카메라 미연결) 실패하면 종전처럼 옆 자리(<see cref="ProbeOffsetsMm"/>)를 차례로 잰다.
/// 다 실패하면 좌표계를 그대로 두고 진행한다(경고). 보정량이 한계(<see cref="MaxOffsetMm"/>·<see cref="MaxRiseMm"/>)를 넘으면
/// 좌표계가 크게 틀렸거나 다른 면을 본 것으로 보고 멈춘다 — 오케스트레이터가 좌표계를 버려 다음 줄은 다시 교시한다.
/// 끝나면 측정 전 자리로 정확히 돌아온다.
/// </summary>
public class LineSurfaceHeightStep : ISequenceStep
{
    public const string StepKey = "lineSurfaceHeight";

    /// <summary>모드 Bag 키(string) — <see cref="ModeReference"/> 또는 <see cref="ModeCorrect"/>. 없으면 생략.</summary>
    public const string RequestBagKey = "line.surfaceHeightMode";
    public const string ModeReference = "reference";
    public const string ModeCorrect = "correct";

    /// <summary>기준 면 높이 Bag 키(double, mm, 좌표계 Z 기준) — 교시 줄에서 이 스텝이 남기고, 물려받은 줄에서 오케스트레이터가 넣는다.</summary>
    public const string ReferenceHeightBagKey = "line.surfaceReferenceMm";

    /// <summary>
    /// 측정 높이 [mm] 파라미터 키 — 좌표계 면(교시 때 TCP 가 있던 평면) 바깥으로 이만큼 떨어진 높이에서 잰다.
    /// 0 이면 교시 높이(레이저가 WD 목표 근처를 읽는다). 비우면 <see cref="DefaultMeasureHeightMm"/>.
    /// </summary>
    public const string MeasureHeightKey = "Sequence.Line.SurfaceMeasureHeightMm";
    public const double DefaultMeasureHeightMm = 0.0;
    internal const double MinMeasureHeightMm = -20.0;
    internal const double MaxMeasureHeightMm = 40.0;

    /// <summary>평탄면 탐색 사용 파라미터 키(true/false) — 비우면 켬. 끄면 정해진 옆 자리만 잰다(종전).</summary>
    public const string FlatSearchKey = "Sequence.Line.SurfaceFlatSearch";

    /// <summary>측정 높이로 가는 이동 한계 [mm] — 넘으면 좌표계가 현재 자리와 맞지 않는 것으로 보고 재지 않는다.</summary>
    internal const double MaxHeightTravelMm = 150.0;

    /// <summary>한 측정점에서 평탄 자리를 바꿔 다시 재는 횟수 상한(첫 자리 포함).</summary>
    internal const int MaxFlatSites = 3;

    /// <summary>시작점 높이 보정 한계 [mm].</summary>
    internal const double MaxOffsetMm = 30.0;
    /// <summary>시작→끝 높이 차 한계 [mm].</summary>
    internal const double MaxRiseMm = 30.0;
    /// <summary>레이저 평면 법선이 좌표계 Z 에서 이만큼 넘게 기울면 코로게이션 경사로 보고 그 측정을 버린다 [도].</summary>
    internal const double MaxPlaneTiltDeg = 6.0;
    /// <summary>이보다 짧은 용접선은 기울기를 보정하지 않는다(높이만) [mm].</summary>
    internal const double MinSpanMm = 50.0;

    /// <summary>측정 자리 후보 — 좌표계 (X, Y) 오프셋 [mm]. 끝점에서는 X 를 용접선 안쪽으로 뒤집는다.</summary>
    internal static readonly (double X, double Y)[] ProbeOffsetsMm = { (0, 0), (40, 0), (0, 40), (0, -40), (-40, 0) };

    private const int StableWindow = 5;
    private const double StableBandMm = 0.5;
    private const int StabilizeTimeoutMs = 4000;
    private const int PollMs = 100;

    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ParameterService _param;
    private readonly PostureLimitsService _limits;
    private readonly ILogger<LineSurfaceHeightStep> _logger;

    public LineSurfaceHeightStep(CobotService cobot, CameraService camera, FlatSurfaceCenteringService centering,
        LaserDisplacementSensorService laser, ParameterService param, PostureLimitsService limits,
        ILogger<LineSurfaceHeightStep> logger)
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
    public string DisplayName => "줄 시작·끝 면 높이 측정 (좌표계 높이·기울기 보정)";
    public int DefaultOrder => 1185;   // ⑯⁺ 좌표계 계산(1170) 뒤, ⑰⁺ 묶음 끝 WD(1190)·검사 팁 진입(1195) 앞

    public StepValidation Validate(SequenceContext context) => StepValidation.Ok();

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var mode = context.Bag.TryGetValue(RequestBagKey, out var mv) ? mv as string : null;
        if (mode is not (ModeReference or ModeCorrect))
            return StepResult.Ok("다른 줄 좌표계 재사용 아님 — 생략.");
        if (!context.Bag.TryGetValue(InternalWorkpieceFrame.BagKey, out var fv) || fv is not double[] { Length: 6 } frame)
            return StepResult.Ok("작업물 좌표계 없음 — 생략.");
        if (!_laser.IsConnected)
        {
            _logger.LogWarning("[줄 면 높이] 레이저 미연결 — 좌표계를 그대로 씁니다.");
            return StepResult.Ok("레이저 미연결 — 좌표계 그대로.");
        }

        var measureHeightMm = await GetMeasureHeightMmAsync();
        var start = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var outward = OutwardNormal(frame, start, context.OpticalAxis ?? ToolAxisDir.MinusZ);
        var heightNowMm = HeightOutMm(frame, start, outward);
        var travelMm = measureHeightMm - heightNowMm;
        if (Math.Abs(travelMm) > MaxHeightTravelMm)
        {
            _logger.LogWarning(
                "[줄 면 높이] 현재 높이 {Now:+0.0;-0.0}mm → 측정 높이 {H:+0.0;-0.0}mm 이동량 {T:+0.0;-0.0}mm 가 한계 ±{Max:0}mm 초과 — 좌표계를 그대로 씁니다.",
                heightNowMm, measureHeightMm, travelMm, MaxHeightTravelMm);
            return StepResult.Ok($"측정 높이 이동량 {travelMm:+0;-0}mm 한계 초과 — 좌표계 그대로.");
        }
        var lifted = Offset(start, outward, travelMm);   // 측정 높이의 시작 자리
        var flat = await LoadFlatSetupAsync(context);
        _logger.LogInformation(
            "[줄 면 높이] 측정 높이 {H:+0.0;-0.0}mm(좌표계 면 기준, '{Key}') — 현재 {Now:+0.0;-0.0}mm 에서 {T:+0.0;-0.0}mm 이동, 평탄면 탐색 {Flat}",
            measureHeightMm, MeasureHeightKey, heightNowMm, travelMm, flat is null ? "안 씀(정해진 옆 자리)" : "사용");
        var rc = await _cobot.Rpc.MoveLAsync(lifted, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
        {
            _logger.LogWarning("[줄 면 높이] 측정 높이로 {T:+0.0;-0.0}mm 이동 실패 (rc={Rc}){Desc} — 좌표계를 그대로 씁니다.",
                travelMm, rc, FairinoErrorCodes.Suffix(rc));
            await ReturnAsync(start, context, ct);
            return StepResult.Ok($"측정 높이 이동 실패(rc={rc}) — 좌표계 그대로.");
        }

        try
        {
            if (mode == ModeReference)
            {
                var p = await MeasureAsync(context, frame, lifted, 0, +1, "기준", flat, ct);
                if (p is null)
                {
                    context.Bag.Remove(ReferenceHeightBagKey);
                    _logger.LogWarning("[줄 면 높이] 교시 줄 기준 높이를 재지 못함 — 다른 줄에서는 기울기만 보정합니다.");
                    return StepResult.Ok("기준 면 높이 측정 실패 — 다른 줄은 기울기만 보정.");
                }
                var (_, h) = InternalWorkpieceFrame.AlongAndHeight(frame, p);
                context.Bag[ReferenceHeightBagKey] = h;
                _logger.LogInformation("[줄 면 높이] 교시 줄 기준 면 높이 {H:+0.00;-0.00}mm (좌표계 Z 기준) 기록", h);
                return StepResult.Ok($"기준 면 높이 {h:+0.0;-0.0}mm 기록.");
            }

            // 물려받은 줄 — 시작·끝 측정 후 보정.
            var lengthMm = SeamLengthMm(context);
            var startPoint = await MeasureAsync(context, frame, lifted, 0, +1, "시작", flat, ct);
            if (startPoint is null)
            {
                _logger.LogWarning("[줄 면 높이] 시작점 유효 측정 없음 — 물려받은 좌표계를 그대로 씁니다(높이·기울기 미보정).");
                return StepResult.Ok("시작점 면 높이 측정 실패 — 좌표계 그대로.");
            }
            double[]? endPoint = null;
            if (lengthMm >= MinSpanMm)
                endPoint = await MeasureAsync(context, frame, lifted, lengthMm, -1, "끝", flat, ct);
            if (endPoint is null)
                _logger.LogWarning("[줄 면 높이] 끝점 측정 {Why} — 높이만 보정합니다.",
                    lengthMm < MinSpanMm ? $"생략(용접선 {lengthMm:0}mm)" : "실패");

            double? reference = context.Bag.TryGetValue(ReferenceHeightBagKey, out var rv) && rv is double r ? r : null;
            var (corrected, offsetMm, tiltDeg, riseMm, spanMm) =
                InternalWorkpieceFrame.CorrectBySurfaceHeights(frame, startPoint, endPoint, reference, MinSpanMm);
            if (Problem(offsetMm, riseMm) is { } problem)
            {
                _logger.LogWarning("[줄 면 높이] {Msg}", problem);
                return StepResult.Fail(problem);
            }
            context.Bag[InternalWorkpieceFrame.BagKey] = corrected;
            _logger.LogInformation(
                "[줄 면 높이] 좌표계 보정 — 시작점 높이 {Off:+0.00;-0.00}mm{Ref}, 시작→끝 {Rise:+0.00;-0.00}mm / {Span:0}mm → 기울기 {Tilt:+0.00;-0.00}° " +
                "— 원점 [{X:0.0}, {Y:0.0}, {Z:0.0}] → [{X2:0.0}, {Y2:0.0}, {Z2:0.0}]",
                -offsetMm, reference is null ? " (기준 없음 — 높이 유지)" : "", riseMm, spanMm, tiltDeg,
                frame[0], frame[1], frame[2], corrected[0], corrected[1], corrected[2]);
            return StepResult.Ok(
                $"줄 면 높이 보정 — 높이 {-offsetMm:+0.0;-0.0}mm{(reference is null ? "(기준 없음)" : "")}, " +
                $"기울기 {tiltDeg:+0.00;-0.00}° (시작→끝 {riseMm:+0.0;-0.0}mm / {spanMm:0}mm).");
        }
        finally
        {
            await ReturnAsync(start, context, ct);
        }
    }

    /// <summary>보정량 한계 검사 — 넘으면 사유, 아니면 null. offset 은 측정 높이 − 기준(+ = 면이 생각보다 바깥).</summary>
    internal static string? Problem(double offsetMm, double riseMm)
    {
        if (Math.Abs(offsetMm) > MaxOffsetMm)
            return $"물려받은 좌표계의 면 높이가 실측과 {offsetMm:+0.0;-0.0}mm 다릅니다(한계 ±{MaxOffsetMm:0}mm) — " +
                   "좌표계가 크게 틀렸거나 다른 면을 봤습니다. 검사하지 않고 멈춥니다(다음 줄은 다시 교시).";
        if (Math.Abs(riseMm) > MaxRiseMm)
            return $"줄 시작→끝 면 높이 차 {riseMm:+0.0;-0.0}mm 가 한계 ±{MaxRiseMm:0}mm 를 넘습니다 — " +
                   "끝에서 다른 면을 봤거나 측정 이상입니다. 검사하지 않고 멈춥니다(다음 줄은 다시 교시).";
        return null;
    }

    /// <summary>평탄면 탐색에 쓰는 설정 묶음 — 시퀀스 ④·점2 평탄면 탐색과 같은 파라미터.</summary>
    private sealed record FlatSetup(
        double RoiX, double RoiY, double RoiW, double RoiH, ToolAxisDir AxisX, ToolAxisDir AxisY, double JointVelScale,
        FlatCandidateOptions Candidates, LaserFootprintOptions Footprint, PostureLimits Limits, double ShiftYmm);

    /// <summary>평탄면 탐색 설정을 읽는다. 끔·카메라 미연결·깊이 프레임 없음·설정 이상이면 null(정해진 옆 자리만 잰다).</summary>
    private async Task<FlatSetup?> LoadFlatSetupAsync(SequenceContext context)
    {
        try
        {
            if (await _param.GetBoolAsync(FlatSearchKey) == false)
                return null;
            if (!_camera.IsConnected || _camera.LatestDepth is null)
            {
                _logger.LogWarning("[줄 면 높이] 카메라 미연결/깊이 프레임 없음 — 평탄면 탐색 없이 정해진 옆 자리만 잽니다.");
                return null;
            }
            var shiftY = context.CameraToLaserShiftYmm;
            if (Math.Abs(shiftY) > 200)
            {
                _logger.LogWarning("[줄 면 높이] 카메라→레이저 보정량 {S:0.#}mm 가 한계 ±200mm 초과 — 평탄면 탐색 없이 잽니다.", shiftY);
                return null;
            }
            var (rx, ry, rw, rh, _) = await CameraAlignStep.GetDepthRoiAsync(_param);
            var (ax, _) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
            var (ay, _) = await WeldSequenceSupport.GetImageYAxisAsync(_param);
            return new FlatSetup(rx, ry, rw, rh, ax, ay,
                await FlatMoveSettings.LoadJointVelocityScaleAsync(_param),
                await FlatCandidateOptions.LoadAsync(_param),
                await LaserFootprintOptions.LoadAsync(_param),
                await _limits.GetAsync(), shiftY);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[줄 면 높이] 평탄면 탐색 설정 읽기 실패 — 정해진 옆 자리만 잽니다: {Msg}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 측정 높이의 시작 자리(<paramref name="lifted"/>)에서 좌표계 X 로 <paramref name="alongMm"/> 간 곳의 면 점(BASE mm).
    /// 평탄면 탐색 → 레이저 측정을 먼저 하고, 쓸 수 없거나 실패하면 정해진 옆 자리(<see cref="ProbeOffsetsMm"/>)를 잰다.
    /// </summary>
    private async Task<double[]?> MeasureAsync(SequenceContext context, double[] frame, double[] lifted,
        double alongMm, int inwardSign, string label, FlatSetup? flat, CancellationToken ct)
    {
        if (flat is not null)
        {
            var p = await FlatProbeAsync(context, frame, ProbePose(lifted, frame, alongMm, 0), label, flat, ct);
            if (p is not null) return p;
            _logger.LogWarning("[줄 면 높이] {Label} 평탄면 탐색 측정 실패 — 정해진 옆 자리로 다시 잽니다.", label);
        }
        return await ProbeAsync(context, frame, lifted, alongMm, inwardSign, label, ct);
    }

    /// <summary>
    /// 교시(점2)와 같은 순서로 잰다 — <paramref name="site"/> 에서 depth 평탄 자리 탐색·카메라 이동 → 레이저 중심 보정(툴 Y) →
    /// 레이저 3점 측정. 측정이 무효거나 평면이 기울면 그 자리를 빼고 <see cref="MaxFlatSites"/> 곳까지 다시 고른다.
    /// </summary>
    private async Task<double[]?> FlatProbeAsync(SequenceContext context, double[] frame, double[] site, string label,
        FlatSetup f, CancellationToken ct)
    {
        var excluded = new List<(double U, double V)>();
        var sites = Math.Clamp(1 + (f.Footprint.Enabled ? f.Footprint.MaxSiteRetries : 0), 1, MaxFlatSites);
        for (var attempt = 0; attempt < sites; attempt++)
        {
            // 탐색은 늘 같은 자리(화면)에서 시작한다 — 제외 자리 좌표가 그 화면 기준이다.
            var rc = await _cobot.Rpc.MoveLAsync(site, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
            if (rc != 0)
            {
                _logger.LogWarning("[줄 면 높이] {Label} 측정 자리 이동 실패 (rc={Rc}){Desc}", label, rc, FairinoErrorCodes.Suffix(rc));
                return null;
            }
            await Task.Delay(800, ct);   // 진동·깊이 프레임 갱신 대기

            context.Progress?.Invoke($"줄 면 높이 {label}: 평탄면 탐색{(attempt > 0 ? $" (다른 자리 {attempt + 1}/{sites})" : "")}");
            var align = await _centering.RunAsync(new FlatCenterAlignOptions
            {
                RoiX = f.RoiX, RoiY = f.RoiY, RoiW = f.RoiW, RoiH = f.RoiH,
                GridSize = 5,
                SamplesPerDetect = 10,
                DeadbandUv = f.Footprint.Enabled ? 0.0 : 0.02,
                ToleranceMm = f.Footprint.Enabled ? 1.5 : 0.0,
                MaxIterations = 1, VerifyAfterMove = false,
                MaxLateralMoveMm = 0.0,
                ImageXAxis = f.AxisX, ImageYAxis = f.AxisY,
                Tool = context.Tool, Velocity = context.Velocity,
                Source = $"줄 면 높이 {label}{(attempt > 0 ? $" 재시도 {attempt}" : "")}",
                Candidates = f.Candidates,
                Reach = new FlatReachOptions
                {
                    Limits = f.Limits,
                    ImageXAxis = f.AxisX, ImageYAxis = f.AxisY,
                    CameraToLaserShiftYmm = f.ShiftYmm,
                    TiltMarginDeg = 0,
                    Tool = context.Tool,
                },
                Footprint = new FlatFootprintContext
                {
                    Options = f.Footprint,
                    ImageXAxis = f.AxisX, ImageYAxis = f.AxisY,
                    Excluded = excluded.ToArray(),
                },
                UseJointMove = true,
                JointVelocityScale = f.JointVelScale,
            }, context.Progress, ct);
            ct.ThrowIfCancellationRequested();   // 공용 루틴은 취소를 실패 결과로 돌려준다
            if (!align.Success)
            {
                _logger.LogWarning("[줄 면 높이] {Label} 평탄면 탐색 실패: {Msg}", label, align.Message);
                return null;
            }

            var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
            var shift = await _centering.MoveToolOffsetAsync(anchor, new[] { 0.0, f.ShiftYmm, 0.0, 0.0, 0.0, 0.0 },
                context.Tool, context.Velocity, joint: true, ct, f.JointVelScale);
            if (!shift.Ok)
            {
                _logger.LogWarning("[줄 면 높이] {Label} 레이저 중심 보정 이동 실패 {Why}", label, shift.Describe());
                return null;
            }

            var where = $"평탄 자리 {attempt + 1}(σ={align.SigmaMm:0.##}mm)";
            var point = await MeasureHereAsync(context, frame, label, where, ct);
            if (point is not null) return point;
            if (align.FlatU is not { } u || align.FlatV is not { } v) return null;
            excluded.Add((u, v));
        }
        return null;
    }

    /// <summary>
    /// 측정 높이의 시작 자리에서 좌표계 X 로 <paramref name="alongMm"/> 간 곳 주변을 후보 순서대로 재서 첫 유효 면 점(BASE mm)을 돌려준다.
    /// <paramref name="inwardSign"/> 는 X 오프셋 부호 — 끝점은 −1(용접선 안쪽부터).
    /// </summary>
    private async Task<double[]?> ProbeAsync(SequenceContext context, double[] frame, double[] lifted,
        double alongMm, int inwardSign, string label, CancellationToken ct)
    {
        foreach (var (ox, oy) in ProbeOffsetsMm)
        {
            var pose = ProbePose(lifted, frame, alongMm + inwardSign * ox, oy);
            var rc = await _cobot.Rpc.MoveLAsync(pose, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
            if (rc != 0)
            {
                _logger.LogWarning("[줄 면 높이] {Label} 측정 자리 (X{X:+0;-0}, Y{Y:+0;-0})mm 이동 실패 (rc={Rc}){Desc}",
                    label, inwardSign * ox, oy, rc, FairinoErrorCodes.Suffix(rc));
                continue;
            }
            var point = await MeasureHereAsync(context, frame, label, $"(X{inwardSign * ox:+0;-0}, Y{oy:+0;-0})mm", ct);
            if (point is not null) return point;
        }
        return null;
    }

    /// <summary>지금 자리에서 레이저 평면을 재서 면 점(BASE mm)을 돌려준다. 무효·경사면이면 사유를 남기고 null.</summary>
    private async Task<double[]?> MeasureHereAsync(SequenceContext context, double[] frame, string label, string where,
        CancellationToken ct)
    {
        var plane = await SampleStablePlaneAsync(ct);
        if (plane is null)
        {
            _logger.LogInformation("[줄 면 높이] {Label} {Where} — 레이저 유효값 없음 ({Ch}), 다음 자리", label, where, ChannelText());
            return null;
        }
        var tcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var tilt = PlaneTiltToFrameDeg(plane, tcp, frame);
        if (tilt > MaxPlaneTiltDeg)
        {
            _logger.LogInformation(
                "[줄 면 높이] {Label} {Where} — 레이저 평면이 좌표계 면과 {T:0.0}° 기움(한계 {Max:0}°, 코로게이션 경사 의심, {Ch}), 다음 자리",
                label, where, tilt, MaxPlaneTiltDeg, ChannelText());
            return null;
        }
        var point = SurfacePointBase(plane, tcp);
        var (a, h) = InternalWorkpieceFrame.AlongAndHeight(frame, point);
        _logger.LogInformation(
            "[줄 면 높이] {Label} {Where} — 면 점 좌표계 X {A:0.0}mm, 높이 {H:+0.00;-0.00}mm (레이저 z={Z:0.00}mm, 평면 기울기 {T:0.0}°)",
            label, where, a, h, plane.Z, tilt);
        return point;
    }

    /// <summary>채널별 레이저 값 — 유효값이 없을 때 범위 밖인지 흔들린 것인지 가려 보게 남긴다.</summary>
    private string ChannelText()
    {
        var r = _laser.GetReadings();
        return r.Count == 0
            ? "채널 값 없음"
            : string.Join(" ", r.Select((c, i) => $"CH{i + 1}={c.Value:0.00}{(c.Enabled ? "" : "(범위 밖)")}"));
    }

    private async Task ReturnAsync(double[] start, SequenceContext context, CancellationToken ct)
    {
        var rc = await _cobot.Rpc.MoveLAsync(start, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException(
                $"줄 면 높이 측정 후 원위치 복귀 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
    }

    private async Task<double> GetMeasureHeightMmAsync()
    {
        try
        {
            var v = await _param.GetDoubleAsync(MeasureHeightKey);
            if (v is { } x && double.IsFinite(x) && x >= MinMeasureHeightMm && x <= MaxMeasureHeightMm) return x;
            if (v is not null)
                _logger.LogWarning("[줄 면 높이] '{Key}'={V} 범위 밖({Min:0}~{Max:0}) — {Def:0}mm 사용",
                    MeasureHeightKey, v, MinMeasureHeightMm, MaxMeasureHeightMm, DefaultMeasureHeightMm);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[줄 면 높이] '{Key}' 읽기 실패 — {Def:0}mm 사용: {Msg}", MeasureHeightKey, DefaultMeasureHeightMm, ex.Message);
        }
        return DefaultMeasureHeightMm;
    }

    /// <summary>자세의 위치가 좌표계 면에서 바깥(<paramref name="outward"/>)으로 떨어진 거리 [mm].</summary>
    internal static double HeightOutMm(double[] framePose, double[] pose, double[] outward)
        => (pose[0] - framePose[0]) * outward[0] + (pose[1] - framePose[1]) * outward[1] + (pose[2] - framePose[2]) * outward[2];

    /// <summary>ACS 시작·끝점(m) 사이 길이 [mm]. 끝점이 없으면 0.</summary>
    internal static double SeamLengthMm(SequenceContext context)
        => context.SeamStartW is { Length: >= 3 } s && context.SeamEndW is { Length: >= 3 } e
            ? Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (e[i] - s[i]) * (e[i] - s[i]))) * 1000.0
            : 0.0;

    /// <summary>좌표계 Z 중 면 바깥쪽(광축 반대) 단위벡터 — <see cref="BundleEndWdStep.TiltFrameToEnd"/> 와 같은 판정.</summary>
    internal static double[] OutwardNormal(double[] framePose, double[] toolPose, ToolAxisDir opticalAxis)
    {
        var f = FrameMath.PoseToMatrix(framePose);
        double[] z = { f[0, 2], f[1, 2], f[2, 2] };
        var t = FrameMath.PoseToMatrix(toolPose);
        var col = (int)opticalAxis / 2;
        var sgn = (int)opticalAxis % 2 == 0 ? 1.0 : -1.0;
        double[] optical = { sgn * t[0, col], sgn * t[1, col], sgn * t[2, col] };
        return z[0] * optical[0] + z[1] * optical[1] + z[2] * optical[2] < 0 ? z : new[] { -z[0], -z[1], -z[2] };
    }

    internal static double[] Offset(double[] pose, double[] dir, double mm)
        => new[] { pose[0] + mm * dir[0], pose[1] + mm * dir[1], pose[2] + mm * dir[2], pose[3], pose[4], pose[5] };

    /// <summary>측정 높이 자세 그대로 좌표계 X·Y 방향으로 옮긴 자세(BASE).</summary>
    internal static double[] ProbePose(double[] lifted, double[] framePose, double xMm, double yMm)
    {
        var m = FrameMath.PoseToMatrix(framePose);
        var p = (double[])lifted.Clone();
        for (var i = 0; i < 3; i++) p[i] += xMm * m[i, 0] + yMm * m[i, 1];
        return p;
    }

    /// <summary>레이저 평면 중심(툴 좌표)을 BASE 점으로.</summary>
    internal static double[] SurfacePointBase(PlanePose plane, double[] tcp)
    {
        var p = FrameMath.FromFrame(new[] { plane.X, plane.Y, plane.Z, 0.0, 0.0, 0.0 }, tcp);
        return new[] { p[0], p[1], p[2] };
    }

    /// <summary>레이저 평면 법선(툴 좌표)과 좌표계 Z 사이 각 [도] — 방향 부호는 보지 않는다.</summary>
    internal static double PlaneTiltToFrameDeg(PlanePose plane, double[] tcp, double[] framePose)
    {
        var t = FrameMath.PoseToMatrix(tcp);
        var f = FrameMath.PoseToMatrix(framePose);
        var nt = plane.Normal;
        double dot = 0, len = 0;
        for (var i = 0; i < 3; i++)
        {
            var nb = t[i, 0] * nt[0] + t[i, 1] * nt[1] + t[i, 2] * nt[2];
            dot += nb * f[i, 2];
            len += nb * nb;
        }
        if (len < 1e-12) return 90;
        return Math.Acos(Math.Clamp(Math.Abs(dot) / Math.Sqrt(len), 0, 1)) * 180.0 / Math.PI;
    }

    private async Task<PlanePose?> SampleStablePlaneAsync(CancellationToken ct)
    {
        var window = new Queue<double>(StableWindow);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(300, ct);
        while (sw.ElapsedMilliseconds < StabilizeTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var p = _laser.GetPlanePose();
            if (p.Valid)
            {
                window.Enqueue(p.Z);
                if (window.Count > StableWindow) window.Dequeue();
                if (window.Count == StableWindow && window.Max() - window.Min() < StableBandMm)
                    return p with { Z = window.Average() };
            }
            else window.Clear();
            await Task.Delay(PollMs, ct);
        }
        return null;
    }
}
