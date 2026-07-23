using HD_AMR.Communication;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service;

/// <summary>
/// 이미지 축 이동량을 실을 로봇 툴 좌표축(부호 포함). 카메라 장착 방향·툴 정의에 따라 다르므로
/// 코드에서 확정하지 않고 설정으로 지정한다 — 조그 팝업의 툴 프레임 조그로 실측 확인 후 선택.
/// </summary>
public enum ToolAxisDir { PlusX = 0, MinusX = 1, PlusY = 2, MinusY = 3, PlusZ = 4, MinusZ = 5 }

/// <summary>평탄 중심 정렬 옵션. ROI 는 정규화([0,1]) 좌표.</summary>
public sealed class FlatCenterAlignOptions
{
    public required double RoiX { get; init; }
    public required double RoiY { get; init; }
    public required double RoiW { get; init; }
    public required double RoiH { get; init; }

    /// <summary>그리드 한 변 셀 수 (예: 5 → 5×5=25셀).</summary>
    public int GridSize { get; init; } = 5;

    /// <summary>1회 검출당 FindFlattest 샘플 횟수 (뎁스 노이즈 평활화 — 최소 σ 채택).</summary>
    public int SamplesPerDetect { get; init; } = 10;

    /// <summary>mm 수렴 기준. |Δx|,|Δy| 가 모두 이 값 이하면 수렴. 0 이면 mm 판정 비활성.</summary>
    public double ToleranceMm { get; init; } = 2.0;

    /// <summary>정규화 픽셀 데드밴드. |Δu|,|Δv| 가 모두 이 값 미만이면 이동 생략(수렴). 0 이면 비활성.</summary>
    public double DeadbandUv { get; init; }

    /// <summary>최대 이동 횟수. 이 횟수만큼 이동해도 수렴하지 못하면 실패.</summary>
    public int MaxIterations { get; init; } = 5;

    /// <summary>true=이동 후 재검출해 수렴 확인(폐루프), false=1회 이동 후 종료(개루프, 시퀀스 스텝 호환).</summary>
    public bool VerifyAfterMove { get; init; } = true;

    /// <summary>
    /// 1회 횡이동 절대 상한(mm). ≤0 = 비활성. 활성 여부와 무관하게 "ROI 물리 반크기 × 1.1" 동적 한계는
    /// 항상 적용된다 (정상 검출 잔차는 ROI 반크기를 넘을 수 없으므로 변환 이상만 걸러짐).
    /// 초과 시 이동하지 않고 실패(클램프 아님).
    /// </summary>
    public double MaxLateralMoveMm { get; init; } = 100.0;

    /// <summary>이미지 +X(화면 오른쪽) 이동량을 실을 툴축. 기본 +X (레이저 캘리브레이션 실측 180° 관계 기반 추정).</summary>
    public ToolAxisDir ImageXAxis { get; init; } = ToolAxisDir.PlusX;

    /// <summary>이미지 +Y(화면 아래) 이동량을 실을 툴축. 기본 +Y.</summary>
    public ToolAxisDir ImageYAxis { get; init; } = ToolAxisDir.PlusY;

    /// <summary>
    /// 2회차 이후 재검출에 쓸 중앙 축소 ROI 비율(0~1). 직전 이동이 타깃을 ROI 중앙으로 보냈어야 하므로
    /// 중앙 근방에서만 재검출해, ROI 내 다른 평탄 후보로 갈아타는 왕복 진동을 차단한다. 0=비활성(항상 전체 ROI).
    /// </summary>
    public double RefineFraction { get; init; } = 0.6;

    public int Tool { get; init; } = 1;

    /// <summary>이동 속도(%).</summary>
    public double Velocity { get; init; } = 20;

    /// <summary>이동 후 진동/프레임 안정화 대기(ms).</summary>
    public int SettleMs { get; init; } = 500;
}

/// <summary>깊이 거리 접근 이동 옵션. ROI 는 거리 측정(최소 깊이 샘플 평균)에 사용.</summary>
public sealed class DepthDistanceMoveOptions
{
    public required double RoiX { get; init; }
    public required double RoiY { get; init; }
    public required double RoiW { get; init; }
    public required double RoiH { get; init; }

    /// <summary>목표 거리(mm).</summary>
    public double TargetDistanceMm { get; init; } = 400;

    /// <summary>거리 측정 샘플 횟수 (ROI 최소 깊이, 100ms 간격, 유효값 평균).</summary>
    public int Samples { get; init; } = 10;

    /// <summary>잔차가 이내면 이동 생략(mm).</summary>
    public double ToleranceMm { get; init; } = 1.0;

    /// <summary>1회 접근 이동 한계(mm). 초과 시 이동하지 않고 실패.</summary>
    public double MaxTravelMm { get; init; } = 600;

    /// <summary>광축(전방 = 대상을 향하는 방향)에 해당하는 툴축. 방향이 반대면 부호 반전 값 선택.</summary>
    public ToolAxisDir DepthAxis { get; init; } = ToolAxisDir.PlusZ;

    public int Tool { get; init; } = 1;

    /// <summary>이동 속도(%).</summary>
    public double Velocity { get; init; } = 20;

    /// <summary>이동 후 진동/프레임 안정화 대기(ms).</summary>
    public int SettleMs { get; init; } = 500;
}

/// <summary>깊이 거리 접근 이동 결과.</summary>
public sealed record DepthDistanceMoveResult(
    bool Success, double? MeasuredMm, double? MovedMm, string Message);

/// <summary>평탄 중심 정렬 결과. Success=false 는 오류/미수렴, Converged 는 잔차가 기준 이내였는지.</summary>
public sealed record FlatCenterAlignResult(
    bool Success,
    bool Converged,
    int MovesExecuted,
    double? ResidualXMm,
    double? ResidualYMm,
    double? SigmaMm,
    double? MeanDepthMm,
    double? FlatU,
    double? FlatV,
    string Message);

/// <summary>
/// 깊이 ROI 에서 가장 평평한 셀을 찾아 그 중심이 ROI 중심에 오도록 코봇을 횡이동시키는 공용 루틴.
/// <see cref="Sequence.Steps.FlatSurfaceAlignStep"/>(개루프 1회 이동)과 카메라 페이지(폐루프 반복 수렴)가 공유한다.
///
/// 이동은 <b>툴 좌표계</b>(MoveByToolOffset, offset_flag=2) 기준이다. 카메라는 툴에 강체 고정이라
/// 이미지 평면이 툴 좌표계에서 고정 축 쌍과 일치하고, 그 두 축으로만 이동하면 로봇 자세·AMR 정차
/// 방향과 무관하게 대상면과의 거리를 유지한 채 평행 이동한다 (BASE 축 이동은 헤드 방향에 따라
/// 광축 성분이 섞여 벽으로 접근하는 문제가 있었음).
///
/// 이미지 축 ↔ 툴 축 대응은 카메라 장착 방향·툴 정의에 따라 달라 코드에서 확정할 수 없으므로
/// <see cref="FlatCenterAlignOptions.ImageXAxis"/>/<see cref="FlatCenterAlignOptions.ImageYAxis"/> 설정으로
/// 지정한다. 매핑이 틀리면 반복 모드의 발산 가드가 중단시키며, 각 이동의 실측 BASE Δ·깊이 변화를
/// 진행 로그로 남겨 실제 축 방향을 확인할 수 있게 한다.
/// </summary>
public class FlatSurfaceCenteringService
{
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly ILogger<FlatSurfaceCenteringService> _logger;

    /// <summary>Z(평탄 셀 평균 깊이) 확보 실패 시 폴백 거리(mm).</summary>
    private const double FallbackDepthMm = 400.0;

    /// <summary>발산 판정 배율 — 이동 후 잔차 크기가 직전 대비 이 배율을 넘으면 축 매핑 오류로 보고 중단.</summary>
    private const double DivergenceFactor = 1.2;

    private static readonly string[] AxisNames = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };

    /// <summary>이동량(mm)을 매핑된 툴축 성분에 더한다. enum 값/2 = 축 인덱스, 짝수=+/홀수=−.</summary>
    private static void ApplyAxis(double[] offset, ToolAxisDir dir, double mm)
        => offset[(int)dir / 2] += ((int)dir % 2 == 0 ? 1.0 : -1.0) * mm;

    public FlatSurfaceCenteringService(
        CobotService cobot, CameraService camera, ILogger<FlatSurfaceCenteringService> logger)
    {
        _cobot = cobot;
        _camera = camera;
        _logger = logger;
    }

    /// <summary>정렬 실행. 예외를 던지지 않고 항상 결과를 반환한다(취소 포함).</summary>
    public async Task<FlatCenterAlignResult> RunAsync(
        FlatCenterAlignOptions o, Action<string>? progress, CancellationToken ct)
    {
        if (!_cobot.IsConnected)
            return Fail("코봇 RPC 미연결");
        if (!_camera.IsConnected)
            return Fail("카메라 미연결");
        if (_camera.LatestDepth is null)
            return Fail("깊이 프레임 없음 — 스트림을 먼저 시작하세요.");
        if (o.RoiW <= 0 || o.RoiH <= 0)
            return Fail("ROI 영역이 비어 있습니다.");
        if ((int)o.ImageXAxis / 2 == (int)o.ImageYAxis / 2)
            return Fail(
                $"이미지 X/Y 매핑이 같은 툴축입니다 (X→{AxisNames[(int)o.ImageXAxis]}, Y→{AxisNames[(int)o.ImageYAxis]}) — 설정을 확인하세요.");

        double centerU = o.RoiX + o.RoiW / 2.0;
        double centerV = o.RoiY + o.RoiH / 2.0;
        double prevMagnitude = double.MaxValue;
        double prevDx = 0, prevDy = 0;
        int moves = 0;

        // 시작 진단: ROI 깊이 분포 — 거리대가 넓으면 여러 면이 섞인 것(타깃 불안정의 흔한 원인).
        var startStats = _camera.ComputeDepthRoiStats(o.RoiX, o.RoiY, o.RoiW, o.RoiH);
        if (startStats is { ValidCount: > 0 })
        {
            Report(progress,
                $"ROI 깊이 {startStats.MinMm}~{startStats.MaxMm}mm (평균 {startStats.AvgMm:0}mm, 유효율 {startStats.ValidRatio:P0})");
            if (startStats.MaxMm - startStats.MinMm > 300)
                Report(progress, "⚠ ROI 에 거리가 다른 면이 섞여 있습니다 — 대상 면 하나만 포함하도록 ROI 를 좁히면 안정적입니다.");
        }

        try
        {
            for (var iter = 0; ; iter++)
            {
                ct.ThrowIfCancellationRequested();

                // ── 검출: N회 샘플 중 σ 최소 셀 채택 (뎁스 노이즈 평활화) ──
                // 2회차부터는 중앙 축소 영역에서만 재검출 — 직전 이동이 타깃을 ROI 중앙으로 보냈어야
                // 하므로, 다른 평탄 후보로 갈아타는 것(왕복 진동 원인)을 차단한다. 셀 크기 유지 위해 grid 도 축소.
                double dRx = o.RoiX, dRy = o.RoiY, dRw = o.RoiW, dRh = o.RoiH;
                int dGrid = o.GridSize;
                bool refined = iter > 0 && o.RefineFraction is > 0 and < 1;
                if (refined)
                {
                    dRw = o.RoiW * o.RefineFraction;
                    dRh = o.RoiH * o.RefineFraction;
                    dRx = centerU - dRw / 2;
                    dRy = centerV - dRh / 2;
                    dGrid = Math.Max(3, (int)Math.Round(o.GridSize * o.RefineFraction));
                }

                CameraService.DepthFlatnessResult? best = null;
                for (var i = 0; i < o.SamplesPerDetect; i++)
                {
                    var flat = _camera.FindFlattest(dRx, dRy, dRw, dRh, dGrid);
                    if (flat is not null && (best is null || flat.SigmaMm < best.SigmaMm))
                        best = flat;
                    if (i < o.SamplesPerDetect - 1) await Task.Delay(100, ct);
                }

                if (best is null)
                    return Fail(refined
                        ? "중앙 재검출 실패 — 타깃이 중앙에 오지 않았거나 ROI 내 평탄면이 불안정합니다."
                        : "깊이 프레임에서 유효한 평탄영역을 찾을 수 없습니다.", moves);

                // ── 잔차: 평탄 셀 중심 − ROI 중심 (정규화) ──
                double deltaU = best.U - centerU;   // 양수 = 오른쪽
                double deltaV = best.V - centerV;   // 양수 = 아래쪽

                Report(progress,
                    $"검출 {iter + 1}: 셀 u={best.U:0.###}, v={best.V:0.###}, σ={best.SigmaMm:0.##}mm, " +
                    $"Δuv=({deltaU:+0.###;-0.###}, {deltaV:+0.###;-0.###})");

                if (o.DeadbandUv > 0 && Math.Abs(deltaU) < o.DeadbandUv && Math.Abs(deltaV) < o.DeadbandUv)
                    return Ok(best, converged: true, moves, null, null,
                        "평탄 셀이 이미 ROI 중심 근방 — 이동 생략");

                double z = best.MeanMm > 0 ? best.MeanMm : FallbackDepthMm;
                var (dxMm, dyMm) = _camera.PixelDeltaToMm(deltaU, deltaV, z);

                if (o.ToleranceMm > 0 && Math.Abs(dxMm) <= o.ToleranceMm && Math.Abs(dyMm) <= o.ToleranceMm)
                    return Ok(best, converged: true, moves, dxMm, dyMm,
                        $"수렴 완료: 잔차 ({dxMm:0.#}, {dyMm:0.#})mm ≤ {o.ToleranceMm}mm ({moves}회 이동)");

                if (iter >= o.MaxIterations)
                    return new FlatCenterAlignResult(false, false, moves, dxMm, dyMm,
                        best.SigmaMm, best.MeanMm, best.U, best.V,
                        $"{o.MaxIterations}회 이동 후에도 잔차 ({dxMm:0.#}, {dyMm:0.#})mm > 기준 {o.ToleranceMm}mm");

                // ── 가드: 과대 이동은 이동하지 않고 실패 (클램프 아님) ──
                // ROI 물리 반크기(현재 z 기준) — 정상 검출이면 잔차가 이를 넘을 수 없다. 1.1 = 노이즈 여유.
                var (roiHalfX, roiHalfY) = _camera.PixelDeltaToMm(o.RoiW / 2.0, o.RoiH / 2.0, z);
                double limX = Math.Abs(roiHalfX) * 1.1, limY = Math.Abs(roiHalfY) * 1.1;
                if (o.MaxLateralMoveMm > 0)   // 절대 상한(옵션) — 자동 시퀀스의 추가 안전벽.
                {
                    limX = Math.Min(limX, o.MaxLateralMoveMm);
                    limY = Math.Min(limY, o.MaxLateralMoveMm);
                }
                if (Math.Abs(dxMm) > limX || Math.Abs(dyMm) > limY)
                    return Fail(
                        $"횡이동량 ({dxMm:0.#}, {dyMm:0.#})mm가 한계 (X±{limX:0.#}, Y±{limY:0.#})mm 초과.", moves);

                // ── 발산 가드: 이동했는데 잔차가 커지면 축 매핑/장착 방향 불일치 ──
                double mag = Math.Sqrt(dxMm * dxMm + dyMm * dyMm);
                if (moves > 0 && mag > prevMagnitude * DivergenceFactor)
                    return Fail(
                        $"발산 감지: 잔차 {mag:0.#}mm > 직전 {prevMagnitude:0.#}mm — 축 매핑/카메라 장착 방향을 확인하세요.",
                        moves);

                // ── 진동 가드: 직전과 반대 방향으로 비슷한 거리 왕복 = 반복마다 타깃이 갈아타는 중 ──
                if (moves > 0
                    && dxMm * prevDx + dyMm * prevDy < 0
                    && mag > 0.6 * prevMagnitude && mag < 1.4 * prevMagnitude)
                    return Fail(
                        "진동 감지: 반복마다 반대 방향으로 유사 거리를 왕복합니다 — ROI 안에 유사한 평탄면 " +
                        "후보가 여러 개일 가능성이 큽니다. ROI 를 대상 면 하나만 포함하도록 좁혀 주세요.",
                        moves);

                prevMagnitude = mag;
                prevDx = dxMm;
                prevDy = dyMm;

                // ── 이동: 툴 프레임 오프셋 — 설정된 축 매핑으로 이미지 평면과 평행 이동 ──
                var offset = new double[6];
                ApplyAxis(offset, o.ImageXAxis, dxMm);
                ApplyAxis(offset, o.ImageYAxis, dyMm);
                var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(o.Tool, ct);
                var zBefore = _camera.ComputeDepthRoiStats(o.RoiX, o.RoiY, o.RoiW, o.RoiH)?.AvgMm;
                Report(progress,
                    $"이동 {moves + 1}: Δ=({dxMm:+0.#;-0.#}, {dyMm:+0.#;-0.#})mm, " +
                    $"매핑 X→툴{AxisNames[(int)o.ImageXAxis]}/Y→툴{AxisNames[(int)o.ImageYAxis]} → " +
                    $"툴 오프셋 [{offset[0]:0.#}, {offset[1]:0.#}, {offset[2]:0.#}]");
                var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
                    tool: o.Tool, vel: o.Velocity, ct: ct);
                if (rc != 0)
                    return Fail($"횡이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.", moves);
                moves++;

                // 이동 후 진동/프레임 안정화 대기
                await Task.Delay(o.SettleMs, ct);

                // 진단: 실측 BASE 변위·ROI 평균 깊이 변화 — 축 매핑/평행 이동 여부를 로그로 검증 가능하게.
                try
                {
                    var after = await _cobot.Rpc.GetTcpPoseInBaseAsync(o.Tool, ct);
                    var zAfter = _camera.ComputeDepthRoiStats(o.RoiX, o.RoiY, o.RoiW, o.RoiH)?.AvgMm;
                    Report(progress,
                        $"이동 {moves} 실측: BASE Δ=[{after[0] - anchor[0]:+0.#;-0.#}, " +
                        $"{after[1] - anchor[1]:+0.#;-0.#}, {after[2] - anchor[2]:+0.#;-0.#}]mm, " +
                        $"ROI 평균깊이 {(zBefore is > 0 ? $"{zBefore:0}" : "?")}→{(zAfter is > 0 ? $"{zAfter:0}" : "?")}mm");
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 진단 실패는 정렬을 막지 않음 */ }

                if (!o.VerifyAfterMove)
                    return Ok(best, converged: false, moves, dxMm, dyMm,
                        $"횡이동 1회 완료: Δ=({dxMm:0.#}, {dyMm:0.#})mm (개루프)");
            }
        }
        catch (OperationCanceledException)
        {
            return Fail("사용자 취소", moves);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "평탄 중심 정렬 중 오류");
            return Fail($"정렬 오류: {ex.Message}", moves);
        }
    }

    /// <summary>
    /// 깊이 ROI 측정 거리(최소 깊이 N회 샘플 평균)가 목표가 되도록 툴 광축(<see cref="DepthDistanceMoveOptions.DepthAxis"/>)
    /// 방향으로 1회 접근 이동한다. 예외를 던지지 않고 항상 결과를 반환한다(취소 포함).
    /// </summary>
    public async Task<DepthDistanceMoveResult> MoveToDistanceAsync(
        DepthDistanceMoveOptions o, Action<string>? progress, CancellationToken ct)
    {
        if (!_cobot.IsConnected)
            return DistFail("코봇 RPC 미연결");
        if (!_camera.IsConnected)
            return DistFail("카메라 미연결");
        if (_camera.LatestDepth is null)
            return DistFail("깊이 프레임 없음 — 스트림을 먼저 시작하세요.");
        if (o.RoiW <= 0 || o.RoiH <= 0)
            return DistFail("ROI 영역이 비어 있습니다.");

        try
        {
            // ── 측정: ROI 최소 깊이 N회 샘플 평균 (시퀀스 ③ 기존 방식 유지) ──
            var samples = new List<int>(o.Samples);
            for (var i = 0; i < o.Samples; i++)
            {
                var mm = _camera.ComputeDepthRoiStats(o.RoiX, o.RoiY, o.RoiW, o.RoiH)?.MinMm ?? 0;
                if (mm > 0) samples.Add(mm);
                if (i < o.Samples - 1) await Task.Delay(100, ct);
            }
            if (samples.Count == 0)
                return DistFail("유효한 깊이 측정값이 없습니다.");

            double d = samples.Average();
            double delta = d - o.TargetDistanceMm;
            Report(progress, $"거리 측정 {d:0.#}mm (목표 {o.TargetDistanceMm:0}mm, Δ={delta:+0.#;-0.#}mm)");

            if (Math.Abs(delta) > o.MaxTravelMm)
                return DistFail($"보정량 {Math.Abs(delta):0.#}mm가 한계({o.MaxTravelMm:0}mm) 초과.", d);
            if (Math.Abs(delta) < o.ToleranceMm)
                return DistOk(d, 0,
                    $"측정 {d:0.#}mm — 이미 목표 {o.TargetDistanceMm:0}mm 범위(이동 생략, 광축 툴{AxisNames[(int)o.DepthAxis]}).");

            // ── 이동: 툴 광축(전방) 방향 — d > 목표면 +Δ 전진해 거리가 목표로 줄어든다 ──
            var offset = new double[6];
            ApplyAxis(offset, o.DepthAxis, delta);
            var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(o.Tool, ct);
            Report(progress,
                $"광축 접근: 툴{AxisNames[(int)o.DepthAxis]} 방향 {delta:+0.#;-0.#}mm → " +
                $"툴 오프셋 [{offset[0]:0.#}, {offset[1]:0.#}, {offset[2]:0.#}]");
            var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset,
                tool: o.Tool, vel: o.Velocity, ct: ct);
            if (rc == 112)
                return DistFail(
                    $"접근 이동 실패 (rc=112: 목표 자세 도달 불가) — 광축 매핑(툴{AxisNames[(int)o.DepthAxis]})/방향을 확인하세요.", d);
            if (rc != 0)
                return DistFail($"접근 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.", d);

            await Task.Delay(o.SettleMs, ct);

            // 진단: 실측 BASE 변위·재측정 거리 — 광축 매핑이 맞는지 로그로 검증 가능하게.
            double? d2 = null;
            try
            {
                var after = await _cobot.Rpc.GetTcpPoseInBaseAsync(o.Tool, ct);
                var stat = _camera.ComputeDepthRoiStats(o.RoiX, o.RoiY, o.RoiW, o.RoiH);
                d2 = stat is { MinMm: > 0 } ? stat.MinMm : null;
                Report(progress,
                    $"접근 실측: BASE Δ=[{after[0] - anchor[0]:+0.#;-0.#}, {after[1] - anchor[1]:+0.#;-0.#}, " +
                    $"{after[2] - anchor[2]:+0.#;-0.#}]mm, 거리 {d:0}→{(d2 is not null ? $"{d2:0}" : "?")}mm");
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 진단 실패는 결과에 영향 없음 */ }

            return DistOk(d2 ?? d, delta,
                $"거리 접근 완료: {d:0.#}→{(d2 is not null ? $"{d2:0.#}" : "?")}mm " +
                $"(목표 {o.TargetDistanceMm:0}mm, 이동 {delta:+0.#;-0.#}mm, 광축 툴{AxisNames[(int)o.DepthAxis]})");
        }
        catch (OperationCanceledException)
        {
            return DistFail("사용자 취소");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "거리 접근 이동 중 오류");
            return DistFail($"거리 접근 오류: {ex.Message}");
        }
    }

    private DepthDistanceMoveResult DistFail(string msg, double? measured = null)
    {
        _logger.LogWarning("거리 접근 실패: {Msg}", msg);
        return new DepthDistanceMoveResult(false, measured, null, msg);
    }

    private DepthDistanceMoveResult DistOk(double measured, double moved, string msg)
    {
        _logger.LogInformation("거리 접근: {Msg}", msg);
        return new DepthDistanceMoveResult(true, measured, moved, msg);
    }

    private void Report(Action<string>? progress, string msg)
    {
        _logger.LogInformation("평탄 중심 정렬: {Msg}", msg);
        progress?.Invoke(msg);
    }

    private FlatCenterAlignResult Fail(string msg, int moves = 0)
    {
        _logger.LogWarning("평탄 중심 정렬 실패: {Msg}", msg);
        return new FlatCenterAlignResult(false, false, moves, null, null, null, null, null, null, msg);
    }

    private FlatCenterAlignResult Ok(
        CameraService.DepthFlatnessResult best, bool converged, int moves,
        double? residualX, double? residualY, string msg)
    {
        _logger.LogInformation("평탄 중심 정렬 완료: {Msg}", msg);
        return new FlatCenterAlignResult(true, converged, moves, residualX, residualY,
            best.SigmaMm, best.MeanMm, best.U, best.V, msg);
    }
}
