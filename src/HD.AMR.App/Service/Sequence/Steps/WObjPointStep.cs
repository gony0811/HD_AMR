using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 내부 작업물 좌표계 2점 참조점 기록 — 현재 TCP의 BASE 포즈를 실행 컨텍스트에만 보관한다.
/// 컨트롤러 작업물 좌표계 버퍼/등록 기능은 사용하지 않는다.
///   - 점1(Top 기준): ⑦⁺ Bead1 센터링 완료 위치 (order 760)
///   - 점2(X축 방향): ⑪⁺ Bead2 센터링 완료 위치 (order 1160) — 비드1→비드2 가 작업물 X축(용접 진행 방향)이 된다.
/// 점1·점2 모두 검사캠 시프트가 적용된 위치에서 기록되지만 같은 상수 오프셋이라 X축 방향(점2−점1)에는 영향이 없다.
///
/// 점1 교시 자세의 툴 +Z가 작업물 Z+가 되며, 등록 단계가 Top에서 Z-33.8mm인 바닥 원점을 계산한다.
///
/// 비드 잔차 보정(2026-11-10): 센터링 후에도 남은 cross 잔차 e1·e2 가 그대로 점1·점2 의 옆 오차가 되면 작업물 X축이
/// (e2−e1)/pitch 만큼 돌아가고, anchor 연속 LINE(V02~)은 그 프레임을 X 방향으로 옮겨 쓰므로 V01 에서 멀수록
/// 옆 편차가 비례해 커졌다(pitch 360mm·LINE 간격 ~360mm → V04 시작에서 약 3×(e2−e1)). 그래서 ⑦⁺/⑪⁺ 가 잰 잔차를
/// cross 툴축 오프셋(<see cref="PointCrossCorrectionBagKey"/>)으로 넘기고, 여기서 기록 포즈에 더해 비드 위 점으로 기록한다.
/// </summary>
public class WObjPointStep : ISequenceStep
{
    private readonly int _pointNum;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<WObjPointStep> _logger;

    /// <summary>레거시 UI/설정 호환용 내부 작업물 좌표계 식별 번호. 컨트롤러에는 등록하지 않는다.</summary>
    public const string WObjIdKey = "Sequence.WObj.Id";

    /// <summary>기록된 점 N 의 BASE 기준 TCP 포즈(double[6])를 담는 Bag 키.
    /// 등록 스텝(WObjRegisterStep)이 가상 점3과 함께 클라이언트 계산 경로에 쓴다.</summary>
    public static string PointPoseBagKey(int n) => $"wobj.point{n}Pose";

    private const int DefaultWObjId = 1;

    /// <summary>점 N 기록 시 더할 비드 잔차 보정(툴 프레임 오프셋 double[6]) Bag 키 — ⑦⁺(점1)·⑪⁺(점2) 센터링이 남긴다.</summary>
    public static string PointCrossCorrectionBagKey(int n) => $"wobj.point{n}CrossCorrection";

    /// <summary>잔차 보정 사용 여부 파라미터 키(기본 true). false 면 종전처럼 센터링 위치를 그대로 기록한다.</summary>
    public const string ResidualCorrectionKey = "Sequence.WObj.BeadResidualCorrection";

    /// <summary>이보다 큰 잔차는 보정하지 않는다(mm) — 재측정 오검출로 점을 엉뚱하게 옮기지 않게.</summary>
    public const double MaxResidualCorrectionMm = 30.0;

    /// <summary>
    /// cross 잔차 <paramref name="residualMm"/>(d 규약: 타깃 − 센터, 양수 = cross 축 + 방향)를 툴 프레임 오프셋으로 바꾼다.
    /// 센터링이 d 만큼 <see cref="FlatSurfaceCenteringService.ApplyAxis"/> 로 움직여 비드를 센터에 놓으므로, 남은 잔차도
    /// 같은 규약으로 더하면 비드 위 점이 된다. |잔차| 가 <see cref="MaxResidualCorrectionMm"/> 를 넘거나 유한하지 않으면 null.
    /// </summary>
    public static double[]? CrossCorrectionOffset(ToolAxisDir crossAxis, double residualMm)
    {
        if (!double.IsFinite(residualMm) || Math.Abs(residualMm) > MaxResidualCorrectionMm) return null;
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, crossAxis, residualMm);
        return offset;
    }

    /// <summary>기록 포즈에 툴 프레임 보정 오프셋을 더한다(자세 유지).</summary>
    public static double[] ApplyCorrection(double[] poseBase, double[] toolOffset)
        => FrameMath.FromFrame(toolOffset, poseBase);

    /// <param name="pointNum">1(원점) 또는 2(X축 방향). DI 에서 <c>ActivatorUtilities.CreateInstance</c> 로 주입되므로 첫 인자여야 한다.</param>
    private readonly LaserDisplacementSensorService _laser;
    private readonly CameraService _camera;

    private readonly bool _useGas;

    /// <summary>
    /// 점2 면 거리 보정 파라미터 키(true/false) — 점2 기록을 광축 방향으로 옮겨 점1 과 면 거리를 맞춘다
    /// (<see cref="WObjPointProbe.Point2DistanceCorrection"/>). 로봇은 움직이지 않고 기록 포즈만 바꾼다.
    /// 비우면 검사 센서 모드로 정한다 — 가스 켬, 비전(용접검사기) 끔.
    /// </summary>
    public const string Point2DistanceCorrectionKey = "Sequence.WObj.Point2DistanceCorrection";

    /// <summary>파라미터 값 → 점2 면 거리 보정 사용 여부. 비면 센서 모드 기본값(가스 켬 / 비전 끔).</summary>
    internal static bool ResolvePoint2DistanceCorrection(bool? value, bool usesGas) => value ?? usesGas;

    /// <summary>면 거리 보정량(양수 = 점2 가 더 멀다)을 툴 프레임 오프셋으로 — 광축 + 방향이 면 쪽 접근이다.</summary>
    internal static double[] DistanceCorrectionOffset(ToolAxisDir depthAxis, double mm)
    {
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, depthAxis, mm);
        return offset;
    }

    public WObjPointStep(int pointNum, CobotService cobot, ParameterService param,
        LaserDisplacementSensorService laser, CameraService camera,
        Microsoft.Extensions.Options.IOptions<InspectionSensorSettings> sensor, ILogger<WObjPointStep> logger)
    {
        _useGas = sensor.Value.UsesGasDetector;
        _pointNum = pointNum;
        _cobot = cobot;
        _param = param;
        _laser = laser;
        _camera = camera;
        _logger = logger;
    }

    /// <summary>
    /// 점 기록 순간의 면 거리 스냅샷을 남기고, 점2 에서는 점1 과 비교한다(<see cref="WObjPointProbe"/>) — 읽기 전용 진단.
    /// 센서를 못 읽어도 기록 단계는 그대로 진행한다.
    /// </summary>
    private async Task LogProbeAsync(SequenceContext context, double[] sensorPose)
    {
        try
        {
            var probe = new WObjPointProbe(
                (double[])sensorPose.Clone(),
                WObjPointProbe.LaserOf(_laser.IsConnected ? _laser.GetReadings() : Array.Empty<Models.LaserChannelReading>()),
                WObjPointProbe.CenterDepthMm(_camera.LatestDepth));
            context.Bag[WObjPointProbe.BagKey(_pointNum)] = probe;
            _logger.LogInformation("{Line}", probe.Describe(_pointNum));

            if (_pointNum == 2
                && context.Bag.TryGetValue(WObjPointProbe.BagKey(1), out var v) && v is WObjPointProbe first)
            {
                var (depthAxis, _) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
                _logger.LogInformation("{Line}", WObjPointProbe.Compare(first, probe, depthAxis));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[점 거리 진단] 기록 실패(점{N}): {Msg}", _pointNum, ex.Message);
        }
    }

    private string PointName => _pointNum == 1 ? "점1(Top 기준)" : "점2(X+방향)";

    public string Key => $"wobjPoint{_pointNum}";
    public string DisplayName => $"작업물 좌표계 {PointName} 기록";
    public int DefaultOrder => _pointNum == 1 ? 760 : 1160;

    public StepValidation Validate(SequenceContext context)
        // 순수 기록 단계 — 코봇 연결만 필요하다.
        => _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var wobjId = (int)(await GetWObjIdAsync());
        if (wobjId is < 0 or > 14)
            return StepResult.Fail($"'{WObjIdKey}' 범위 초과 ({wobjId}) — 0~14 로 설정하세요.");

        // 컨트롤러의 SetWObjCoordPoint 버퍼는 사용하지 않는다. 선택된 TCP의 BASE 포즈만 내부 보관한다.
        var pose = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        await LogProbeAsync(context, pose);   // 진단 — 보정 전(센서가 실제로 있는 자리) 포즈 기준
        string correctionNote = "";
        if (context.Bag.TryGetValue(PointCrossCorrectionBagKey(_pointNum), out var cv) && cv is double[] { Length: 6 } corr)
        {
            if (await IsCorrectionEnabledAsync())
            {
                var corrected = ApplyCorrection(pose, corr);
                _logger.LogInformation(
                    "내부 작업물 좌표계 {Point} 비드 잔차 보정: 툴 오프셋 [{Ox:+0.0;-0.0}, {Oy:+0.0;-0.0}, {Oz:+0.0;-0.0}]mm → " +
                    "TCP [{X0:0.0}, {Y0:0.0}, {Z0:0.0}] → [{X:0.0}, {Y:0.0}, {Z:0.0}]",
                    PointName, corr[0], corr[1], corr[2], pose[0], pose[1], pose[2], corrected[0], corrected[1], corrected[2]);
                correctionNote = $" 비드 잔차 보정 툴 [{corr[0]:+0.0;-0.0}, {corr[1]:+0.0;-0.0}, {corr[2]:+0.0;-0.0}]mm 반영.";
                pose = corrected;
            }
            else
                correctionNote = " (비드 잔차 보정 꺼짐 — 센터링 위치 그대로 기록)";
        }
        correctionNote += await CorrectPoint2DistanceAsync(context, pose);
        context.Bag[PointPoseBagKey(_pointNum)] = (double[])pose.Clone();

        _logger.LogInformation(
            "내부 작업물 좌표계 {Point} 기록: TCP [{X:0.0}, {Y:0.0}, {Z:0.0}] (tool {Tool}, 내부 id {Id})",
            PointName, pose[0], pose[1], pose[2], context.Tool, wobjId);

        var remaining = _pointNum == 1 ? "점2(X+방향)" : "내부 프레임 계산";
        return StepResult.Ok(
            $"내부 작업물 좌표계 {PointName} 기록 — BASE TCP [{pose[0]:0.0}, {pose[1]:0.0}, {pose[2]:0.0}] " +
            $"(tool {context.Tool}, 내부 id {wobjId}. 다음: {remaining}).{correctionNote}");
    }

    /// <summary>
    /// 점2 기록을 광축 방향으로 옮겨 점1 과 면 거리를 맞춘다(<paramref name="pose"/> 를 제자리에서 고친다). 점1 은 그대로라
    /// 순회 전체의 면 거리(점1 기준)는 바뀌지 않고 작업물 X축의 면 쪽 기울기만 바로잡힌다. 메모를 돌려준다.
    /// </summary>
    private async Task<string> CorrectPoint2DistanceAsync(SequenceContext context, double[] pose)
    {
        if (_pointNum != 2) return "";
        try
        {
            if (!ResolvePoint2DistanceCorrection(await _param.GetBoolAsync(Point2DistanceCorrectionKey), _useGas)) return "";
            if (!context.Bag.TryGetValue(WObjPointProbe.BagKey(1), out var v1) || v1 is not WObjPointProbe p1
                || !context.Bag.TryGetValue(WObjPointProbe.BagKey(2), out var v2) || v2 is not WObjPointProbe p2)
            {
                _logger.LogWarning("내부 작업물 좌표계 점2 면 거리 보정 생략 — 점1·점2 거리 스냅샷 없음");
                return " (점2 면 거리 보정 생략 — 거리 스냅샷 없음)";
            }

            var (mm, note) = WObjPointProbe.Point2DistanceCorrection(p1, p2);
            if (mm is not { } d)
            {
                _logger.LogInformation("내부 작업물 좌표계 점2 면 거리 보정 안 함 — {Note} ('{Key}')", note, Point2DistanceCorrectionKey);
                return $" (점2 면 거리 보정 안 함 — {note})";
            }

            var (depthAxis, _) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
            var corrected = ApplyCorrection(pose, DistanceCorrectionOffset(depthAxis, d));
            _logger.LogInformation(
                "내부 작업물 좌표계 점2 면 거리 보정: 점2 가 점1 보다 {D:+0.0;-0.0}mm {Dir} ({Note}) → 광축 방향으로 그만큼 옮겨 기록 — " +
                "TCP [{X0:0.0}, {Y0:0.0}, {Z0:0.0}] → [{X:0.0}, {Y:0.0}, {Z:0.0}] ('{Key}')",
                d, d > 0 ? "멀다" : "가깝다", note, pose[0], pose[1], pose[2], corrected[0], corrected[1], corrected[2],
                Point2DistanceCorrectionKey);
            Array.Copy(corrected, pose, 6);
            return $" 점2 면 거리 보정 {d:+0.0;-0.0}mm({note}) 반영.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("내부 작업물 좌표계 점2 면 거리 보정 실패 — 보정 없이 기록: {Msg}", ex.Message);
            return " (점2 면 거리 보정 실패 — 보정 없이 기록)";
        }
    }

    private async Task<bool> IsCorrectionEnabledAsync()
    {
        try { return await _param.GetBoolAsync(ResidualCorrectionKey) ?? true; }
        catch { return true; }
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjIdKey) ?? DefaultWObjId; }
        catch { return DefaultWObjId; }
    }
}
