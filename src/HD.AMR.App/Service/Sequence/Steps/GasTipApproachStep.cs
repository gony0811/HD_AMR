using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑰⁺⁺ 검사 팁 진입 — 가스 검사기(스니퍼) 전용. 도면 순회(⑱) 바로 전에 TCP 를 벽면 쪽으로
/// <see cref="Communication.GasDetector.GasDetectorSettings.TipApproachMm"/> 만큼 천천히 근접시킨다.
///
/// 방향: 내부 작업물 좌표계의 Z(면 법선) 중 광축과 같은 쪽(면을 향하는 쪽) — <see cref="BundleEndWdStep"/> 의 면 바깥 법선 판정과 같다.
/// 자세는 그대로, 위치만 옮긴다(MoveL, user 0). 옮긴 깊이는 <see cref="FrameDzBagKey"/> 로 남겨 ⑱ 이 모든 경유점을 같은 깊이만큼
/// 당겨 순회하고, 순회를 마치면 이만큼 다시 물러난다 — 작업물 좌표계 자체는 바꾸지 않으므로 anchor·묶음 캐시에 진입 깊이가 쌓이지 않는다.
///
/// 비전 모드이거나 진입 거리가 0 이면 아무것도 하지 않는다.
/// </summary>
public class GasTipApproachStep : ISequenceStep
{
    public const string StepKey = "gasTipApproach";

    /// <summary>Bag 키 — 진입한 깊이를 작업물 좌표계 Z 성분으로(부호 포함, mm). ⑱ 이 경유점 Z 에 더하고 순회 뒤 되돌린다.</summary>
    public const string FrameDzBagKey = "gas.tipApproachFrameDz";

    /// <summary>진입 거리 상한 [mm] — 오입력으로 벽에 박지 않도록.</summary>
    public const double MaxApproachMm = 100.0;

    private readonly CobotService _cobot;
    private readonly GasDetectorService _gas;
    private readonly bool _useGas;
    private readonly ILogger<GasTipApproachStep> _logger;

    public GasTipApproachStep(CobotService cobot, GasDetectorService gas, IOptions<InspectionSensorSettings> sensor,
        ILogger<GasTipApproachStep> logger)
    {
        _cobot = cobot;
        _gas = gas;
        _useGas = sensor.Value.UsesGasDetector;
        _logger = logger;
    }

    public string Key => StepKey;
    public string DisplayName => "검사 팁 진입 (벽면 근접)";
    public int DefaultOrder => 1195;   // ⑰⁺ 끝 WD 확인(1190) 뒤, ⑱ 검사 수행(1200) 바로 앞

    private double ApproachMm => Math.Clamp(_gas.Settings.TipApproachMm, 0, MaxApproachMm);
    private int ApproachVel => Math.Clamp(_gas.Settings.TipApproachVelPercent, 1, 100);

    public StepValidation Validate(SequenceContext context)
    {
        if (!_useGas || ApproachMm <= 0) return StepValidation.Ok();
        return _cobot.IsConnected ? StepValidation.Ok() : StepValidation.Fail("코봇 RPC 미연결");
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        context.Bag.Remove(FrameDzBagKey);
        if (!_useGas)
            return StepResult.Ok("가스 검사기 아님 — 생략.");
        var d = ApproachMm;
        if (d <= 0)
            return StepResult.Ok("검사 팁 진입 거리 0 — 생략.");
        if (!context.Bag.TryGetValue(InternalWorkpieceFrame.BagKey, out var fv) || fv is not double[] { Length: 6 } frame)
            return StepResult.Fail(
                "내부 작업물 좌표계가 없습니다 — 점1·점2 기록과 '내부 작업물 좌표계 계산' 단계를 먼저 실행하세요.");

        var start = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var axis = context.OpticalAxis ?? ToolAxisDir.MinusZ;
        var dz = WallwardFrameDz(frame, start, axis, d);
        var target = ShiftAlongFrameZ(start, frame, dz);

        _logger.LogInformation(
            "[검사 팁 진입] 벽면 쪽으로 {D:0.#}mm 근접 (작업물 Z {Dz:+0.#;-0.#}mm, 속도 {Vel}%) — [{X:0.0},{Y:0.0},{Z:0.0}] → [{TX:0.0},{TY:0.0},{TZ:0.0}]",
            d, dz, ApproachVel, start[0], start[1], start[2], target[0], target[1], target[2]);
        context.Progress?.Invoke($"검사 팁 진입 — 벽면 쪽으로 {d:0.#}mm");
        var rc = await _cobot.Rpc.MoveLAsync(target, tool: context.Tool, user: 0,
            vel: ApproachVel, acc: 100, ovl: 100, blendR: -1, ct: ct);
        if (rc != 0)
            return StepResult.Fail($"검사 팁 진입 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");

        context.Bag[FrameDzBagKey] = dz;
        return StepResult.Ok($"검사 팁 진입 — 벽면 쪽으로 {d:0.#}mm 근접 (도면 순회도 이 깊이로 진행, 끝나면 이탈).");
    }

    /// <summary>
    /// 벽면 쪽으로 <paramref name="distanceMm"/> 옮기는 작업물 좌표계 Z 변위(부호 포함). 면을 향하는 쪽 = 좌표계 Z 중 광축과 같은 방향.
    /// </summary>
    internal static double WallwardFrameDz(double[] framePose, double[] toolPose, ToolAxisDir opticalAxis, double distanceMm)
    {
        var f = FrameMath.PoseToMatrix(framePose);
        var t = FrameMath.PoseToMatrix(toolPose);
        var col = (int)opticalAxis / 2;
        var sgn = (int)opticalAxis % 2 == 0 ? 1.0 : -1.0;
        var dot = 0.0;
        for (var i = 0; i < 3; i++) dot += f[i, 2] * sgn * t[i, col];
        return dot >= 0 ? distanceMm : -distanceMm;
    }

    /// <summary>BASE 자세 <paramref name="pose"/> 를 작업물 좌표계 Z 축을 따라 <paramref name="dz"/> mm 옮긴 자세(회전 그대로).</summary>
    internal static double[] ShiftAlongFrameZ(double[] pose, double[] framePose, double dz)
    {
        var f = FrameMath.PoseToMatrix(framePose);
        var r = (double[])pose.Clone();
        for (var i = 0; i < 3; i++) r[i] += dz * f[i, 2];
        return r;
    }
}
