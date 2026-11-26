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

    /// <summary>
    /// 최대 접근 후에도 ±<see cref="AcceptToleranceMm"/> 에 못 들었을 때 그래도 넘기는 범위 [mm] — 목표보다 가까운 쪽
    /// <see cref="NearSlackMm"/>, 먼 쪽 <see cref="FarSlackMm"/>. 더 다가가지 않고 그 자리에서 다음 단계로 넘기며, 거리는 뒤의
    /// 레이저 WD 단계가 면을 직접 재서 다시 맞춘다. 깊이 거리는 ROI 최소 깊이(가장 가까운 물체)라 이 값보다 가까운 물체는 없다.
    /// 챔퍼 맨 아래(바닥과 만나는 구석)처럼 ROI 가 바닥·모서리를 잡아 이동만큼 거리가 줄지 않는 자리용
    /// (실기 2026-11-18 09:38 PL L1-R01C12 V7: 199·113·48·31mm 접근에 거리 499→413→348→320→285mm).
    /// </summary>
    internal const double NearSlackMm = 20, FarSlackMm = 30;

    /// <summary>
    /// 깊이 무응답을 경고로 넘길 때 코봇 실측 이동이 지령 대비 이 비율 이상이어야 한다 — 로봇이 실제로 움직였는데 깊이만 안 변한 경우만.
    /// </summary>
    internal const double MovedMinRatio = 0.8;

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
        var (depthAxis, axisFromParam) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        if (!axisFromParam)
            return StepResult.Fail(WeldSequenceSupport.OpticalAxisMissingMessage(DisplayName));
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
        else if (msg.StartsWith('⚠'))
        {
            _logger.LogWarning("카메라 거리 재정렬: {Msg}", msg);
            context.Progress?.Invoke(msg);
        }
        return ok ? StepResult.Ok(msg) : StepResult.Fail(msg);
    }

    /// <summary>
    /// 재정렬 접근에서 "이동했는데 깊이 거리가 기대만큼 안 변함"이 났을 때 경고로 넘길지. 넘기면 그 메시지(⚠ 로 시작), 아니면 null.
    /// 깊이 거리는 ROI 에서 가장 가까운 점이라, 면이 아닌 물체(천장 아래 구조물·케이블 등)가 ROI 에 걸리면 코봇이 다가가도
    /// 값이 줄지 않는다(실기 2026-11-26 11:03 T L4-R09C24-4: +43.2mm 이동에 343→344mm, 11-24 이후 T·SU 에서 9회).
    /// 넘기는 조건 — 둘 다 만족할 때만:
    ///  · 코봇 실측 이동이 지령의 <see cref="MovedMinRatio"/> 이상(로봇이 실제로 움직였다 — 정지·막힘이면 종전대로 실패)
    ///  · 가장 가까운 물체 거리가 목표 − <see cref="NearSlackMm"/> 이상(근접 허용과 같은 기준 — 그보다 가까운 물체는 없다)
    /// 더 다가가지 않고 그 자리에서 다음 단계로 넘긴다 — 면까지 거리는 뒤의 레이저 WD 단계가 면을 직접 재서 맞춘다.
    /// </summary>
    internal static string? AcceptDepthNoResponse(DepthDistanceMoveResult r, double targetMm, int attempt)
    {
        if (!r.NoResponse || r.CommandedMm is not { } cmd || r.ActualMovedMm is not { } moved || r.MeasuredMm is not { } nearest)
            return null;
        if (moved < MovedMinRatio * Math.Abs(cmd)) return null;
        if (nearest < targetMm - NearSlackMm) return null;
        return $"⚠ 재정렬 {attempt}회차 깊이 무응답을 경고로 넘김 — 코봇은 {moved:0.#}mm 이동(지령 {cmd:+0.#;-0.#}mm)했는데 " +
               $"깊이 거리(ROI 가장 가까운 점)는 {nearest:0.#}mm. ROI 안 가장 가까운 점이 면이 아닌 물체로 보고 더 다가가지 않습니다 " +
               $"(가장 가까운 물체 {nearest:0}mm ≥ 목표 {targetMm:0}−{NearSlackMm:0}mm). 면 거리는 레이저 WD 단계에서 다시 맞춥니다. [{r.Message}]";
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
            {
                if (AcceptDepthNoResponse(r, targetMm, i + 1) is { } warn)
                    return (true, warn);
                return (false, $"재정렬 {i + 1}회차 접근 실패 — {r.Message}");
            }
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
        if (d.Value >= targetMm - NearSlackMm && d.Value <= targetMm + FarSlackMm)
            return (true,
                $"재정렬 근접 허용 — {MaxMoves}회 접근 후 {first:0.#}→{d:0.#}mm (목표 {targetMm:0}±{AcceptToleranceMm:0}mm 밖이지만 " +
                $"-{NearSlackMm:0}/+{FarSlackMm:0}mm 안 — 더 다가가지 않고 레이저 WD 단계에서 다시 맞춤). " +
                "ROI 가 면이 아닌 바닥·모서리를 잡았을 수 있습니다.");
        return (false,
            $"목표 거리 미도달 — {MaxMoves}회 접근 후 {d:0.#}mm (목표 {targetMm:0}±{AcceptToleranceMm:0}mm). " +
            "ROI 안 구조물이 계속 가장 가깝게 잡혀 접근이 제한됐을 수 있습니다 — ROI 를 확인하세요.");
    }
}
