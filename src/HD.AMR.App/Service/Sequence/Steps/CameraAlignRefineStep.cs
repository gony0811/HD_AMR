using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ③-1 카메라 거리 재정렬 — ③ 이후 같은 ROI·광축으로 거리를 다시 재고, 목표 ±<see cref="AcceptToleranceMm"/> 안에
/// 들 때까지 최대 <see cref="MaxMoves"/>회 더 접근한다. 끝내 못 들면 NG — 먼 거리에서 ④가 도는 것을 막는다.
/// ③은 1회 이동 후 결과를 검증하지 않는다. ROI 최소 깊이(충돌 안전 기준)가 벽보다 앞의 구조물을 잡으면 그만큼 덜
/// 다가가는데(10-27 L4-R02C04 L7·L8: 594→487mm), 다가가며 그 구조물이 ROI 밖으로 빠지면 재측정이 벽 거리를 준다.
/// 거리 기준은 ③과 같은 ROI 최소 깊이라 충돌 판단은 종전과 동일하다 — 새 파라미터 없음.
/// </summary>
public class CameraAlignRefineStep : ISequenceStep
{
    public const string StepKey = "cameraAlignRefine";

    /// <summary>목표 거리 허용 오차(mm) — 이 안이면 이동 생략·성공. ④ 평탄 셀 판정이 300mm 기준이라 ±10mm 로 묶는다.</summary>
    internal const double AcceptToleranceMm = 10;

    /// <summary>추가 접근 최대 횟수.</summary>
    internal const int MaxMoves = 3;

    /// <summary>1회 추가 접근 한계(mm) — ③ 잔여 보정용이라 ③(1000mm)보다 작게 둔다.</summary>
    private const double MaxRefineTravelMm = 500;

    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly FlatSurfaceCenteringService _centering;
    private readonly ParameterService _param;
    private readonly ILogger<CameraAlignRefineStep> _logger;

    public CameraAlignRefineStep(CobotService cobot, CameraService camera,
        FlatSurfaceCenteringService centering,
        ParameterService param, ILogger<CameraAlignRefineStep> logger)
    {
        _cobot = cobot;
        _camera = camera;
        _centering = centering;
        _param = param;
        _logger = logger;
    }

    public string Key => StepKey;
    public string DisplayName => "카메라 거리 재정렬";
    public int DefaultOrder => 350;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        if (!_camera.IsConnected)
            return StepValidation.Fail("카메라 미연결 — depth 측정 불가");

        if (context.CameraTargetDistanceMm is < 100 or > 1000)
            return StepValidation.Fail("카메라 목표 거리 범위 초과 (100~1000mm).");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (_camera.LatestDepth is null)
            return StepResult.Fail("깊이 프레임이 없습니다.");

        var (rx, ry, rw, rh, roiSrc) = await CameraAlignStep.GetDepthRoiAsync(_param);
        var (depthAxis, _) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        var target = context.CameraTargetDistanceMm;
        _logger.LogInformation(
            "Sequence [카메라 거리 재정렬] 목표={Dist}±{Tol}mm, ROI={Roi}({Rx:0.00},{Ry:0.00},{Rw:0.00},{Rh:0.00}), 광축=툴{Axis}",
            target, AcceptToleranceMm, roiSrc, rx, ry, rw, rh, depthAxis);
        context.Progress?.Invoke($"거리 재정렬 시작 — 목표 {target:0}±{AcceptToleranceMm:0}mm (ROI={roiSrc})");

        var opts = new DepthDistanceMoveOptions
        {
            RoiX = rx, RoiY = ry, RoiW = rw, RoiH = rh,
            TargetDistanceMm = target,
            ToleranceMm = AcceptToleranceMm,
            MaxTravelMm = MaxRefineTravelMm,
            DepthAxis = depthAxis,
            Tool = context.Tool,
            Velocity = context.Velocity,
        };

        var (ok, msg) = await RefineAsync(
            () => _centering.MoveToDistanceAsync(opts, context.Progress, ct),
            () => _centering.MeasureRoiDistanceAsync(opts, ct),
            target);

        // 공용 루틴은 취소를 삼키고 실패 결과로 반환 — 스텝은 ③처럼 취소 예외로 전파한다.
        ct.ThrowIfCancellationRequested();
        if (!ok) _logger.LogWarning("카메라 거리 재정렬 실패: {Msg}", msg);
        return ok ? StepResult.Ok(msg) : StepResult.Fail(msg);
    }

    /// <summary>
    /// 재정렬 루프 — <paramref name="move"/> 는 허용 오차 안이면 이동 없이(MovedMm=0) 성공을 돌려준다.
    /// 최대 <see cref="MaxMoves"/>회 이동 후에도 범위 밖이면 <paramref name="measure"/> 로 최종 거리를 재서 NG.
    /// </summary>
    internal static async Task<(bool Ok, string Message)> RefineAsync(
        Func<Task<DepthDistanceMoveResult>> move, Func<Task<double?>> measure, double targetMm)
    {
        double? first = null;
        for (var i = 0; i < MaxMoves; i++)
        {
            var r = await move();
            if (!r.Success)
                return (false, $"재정렬 {i + 1}회차 접근 실패 — {r.Message}");
            first ??= r.MeasuredMm;

            if (r.MovedMm is null or 0)
                return (true, i == 0
                    ? $"이미 목표 범위 — 측정 {r.MeasuredMm:0.#}mm (목표 {targetMm:0}±{AcceptToleranceMm:0}mm, 이동 없음)"
                    : $"재정렬 완료 — {first:0.#}→{r.MeasuredMm:0.#}mm (목표 {targetMm:0}±{AcceptToleranceMm:0}mm, 추가 접근 {i}회)");
        }

        var d = await measure();
        if (d is null)
            return (false, $"재정렬 {MaxMoves}회 접근 후 거리 측정 실패 — 유효한 깊이 값이 없습니다.");
        if (Math.Abs(d.Value - targetMm) <= AcceptToleranceMm)
            return (true, $"재정렬 완료 — {first:0.#}→{d:0.#}mm (목표 {targetMm:0}±{AcceptToleranceMm:0}mm, 추가 접근 {MaxMoves}회)");
        return (false,
            $"목표 거리 미도달 — {MaxMoves}회 접근 후 {d:0.#}mm (목표 {targetMm:0}±{AcceptToleranceMm:0}mm). " +
            "ROI 안 구조물이 계속 가장 가깝게 잡혀 접근이 제한됐을 수 있습니다 — ROI 를 확인하세요.");
    }
}
