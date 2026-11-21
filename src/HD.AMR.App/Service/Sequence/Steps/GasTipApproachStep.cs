using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑰⁺⁺ 검사 팁 진입 — 가스 검사기(스니퍼) 전용. 도면 순회(⑱)가 TCP 를 벽면 쪽으로
/// <see cref="Communication.GasDetector.GasDetectorSettings.TipApproachMm"/> 만큼 들여 순회하도록 진입 깊이를 정한다.
///
/// 방향: 내부 작업물 좌표계의 Z(면 법선) 중 광축과 같은 쪽(면을 향하는 쪽) — <see cref="BundleEndWdStep"/> 의 면 바깥 법선 판정과 같다.
/// 이 스텝은 움직이지 않는다. 깊이는 <see cref="FrameDzBagKey"/> 로 남기고, ⑱ 이 첫 경유점(코로게이션 시작점)에 티칭 높이로
/// 스테이징한 뒤 그 자리에서 진입 속도로 들어가, 모든 경유점을 같은 깊이만큼 당겨 순회하고 끝나면 이만큼 물러난다
/// — 작업물 좌표계 자체는 바꾸지 않으므로 anchor·묶음 캐시에 진입 깊이가 쌓이지 않는다.
///
/// 비전 모드이거나 진입 거리가 0 이면 아무것도 하지 않는다.
/// </summary>
public class GasTipApproachStep : ISequenceStep
{
    public const string StepKey = "gasTipApproach";

    /// <summary>Bag 키 — 진입한 깊이를 작업물 좌표계 Z 성분으로(부호 포함, mm). ⑱ 이 경유점 Z 에 더하고 순회 뒤 되돌린다.</summary>
    public const string FrameDzBagKey = "gas.tipApproachFrameDz";

    /// <summary>
    /// Bag 키(bool true) — 순회를 마친 뒤 물러나지 않고 진입 깊이를 유지한다. 오케스트레이터가 묶음에서 다음 구역이 곧바로
    /// 이어질 때(묶음 마지막이 아님) 켠다. 다음 구역의 ⑱ 은 이미 진입 깊이에 있으면 다시 빠졌다 들어오지 않는다.
    /// </summary>
    public const string KeepDepthBagKey = "gas.tipKeepDepth";

    /// <summary>Bag 키(double, mm) — 이번 순회가 물러나지 않고 유지한 진입 깊이. 오케스트레이터가 다음 구역에 넘긴다.</summary>
    public const string KeptDepthMmBagKey = "gas.tipKeptDepthMm";

    /// <summary>Bag 키(double, mm) — 앞 구역이 유지한 진입 깊이. 연속 이동(②)이 그만큼 면에 붙어 있는 것을 정상으로 보고 띄우지 않는다.</summary>
    public const string PreviousKeptDepthBagKey = "gas.previousTipKeptDepthMm";

    /// <summary>
    /// 지금 TCP 가 진입 깊이의 첫 경유점에서 이 거리 [mm] 안이고 티칭 높이 쪽보다 가까우면 "이미 진입한 상태"로 보고
    /// 빠졌다 들어오지 않고 그 깊이로 바로 첫 경유점에 선다.
    /// </summary>
    public const double AlreadyInTolMm = 15.0;

    /// <summary>현재 TCP 가 이미 진입 깊이에 있는지 — <see cref="AlreadyInTolMm"/> 참조.</summary>
    internal static bool IsAlreadyIn(double[] currentPose, double[] tipTarget, double[] rawTarget)
    {
        static double Dist(double[] a, double[] b)
            => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i])));
        var toTip = Dist(currentPose, tipTarget);
        return toTip <= AlreadyInTolMm && toTip < Dist(currentPose, rawTarget);
    }

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

        // 여기서는 진입 깊이(방향 포함)만 정하고 움직이지 않는다 — 실제 진입은 ⑱ 이 첫 경유점(코로게이션 시작점)에
        // 티칭 높이로 선 뒤에 한다. 지금 자리(점2 등)에서 먼저 들어가면 첫 경유점까지 면을 따라 더 붙은 채 이동한다
        // (실기 2026-11-21 15:10: 점2 자리에서 진입 후 360mm 이동).
        var here = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var axis = context.OpticalAxis ?? ToolAxisDir.MinusZ;
        var dz = WallwardFrameDz(frame, here, axis, d);

        _logger.LogInformation(
            "[검사 팁 진입] 진입 깊이 {D:0.#}mm (작업물 Z {Dz:+0.#;-0.#}mm, 속도 {Vel}%) — 첫 경유점 도착 후 진입",
            d, dz, ApproachVel);
        context.Bag[FrameDzBagKey] = dz;
        return StepResult.Ok($"검사 팁 진입 {d:0.#}mm 준비 — 첫 경유점에 선 뒤 벽면 쪽으로 진입(순회 끝나면 이탈).");
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
