using System.Globalization;
using HD_AMR.Communication;
using HD_AMR.Models;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service;

/// <summary>캘리브레이션 실행 옵션. 범위 밖 값은 RunAsync 에서 안전 범위로 클램프된다.</summary>
public sealed class LaserHeadCalibrationOptions
{
    /// <summary>틸트 각도(°, 0.5~5). 클수록 S/N 유리하나 헤드가 측정범위를 벗어날 수 있다.</summary>
    public double TiltDeg { get; set; } = 2.0;

    /// <summary>측정점당 샘플 수(3~20).</summary>
    public int SamplesPerPoint { get; set; } = 5;

    /// <summary>샘플 간격(ms). 센서 RPI(50ms)보다 길게.</summary>
    public int SampleIntervalMs { get; set; } = 100;

    /// <summary>모션 완료 후 진동 안정화 대기(ms).</summary>
    public int SettleMs { get; set; } = 500;

    /// <summary>이동 속도(%, 1~30).</summary>
    public double VelPct { get; set; } = 5;

    /// <summary>공구 번호 — 기존 '보정 적용'과 동일하게 tool=1.</summary>
    public int Tool { get; set; } = 1;

    /// <summary>시작 전 툴 +Z 이동으로 TiltReadingSignForUp 부호를 실측 검증할지.</summary>
    public bool VerifyReadingSign { get; set; } = true;

    /// <summary>부호 검증 +Z 이동량(mm, 0.5~5).</summary>
    public double ZCheckMm { get; set; } = 2.0;

    /// <summary>표면 기울기가 크면 <b>측정된</b> 오프셋으로 자세를 보정하고 재프로브(자동 수평).
    /// 현재 설정 오프셋이 틀려도 동작한다 — 프로브가 측정한 기하만 사용.</summary>
    public bool AutoLevel { get; set; } = true;
}

/// <summary>
/// 레이저 변위센서 헤드 XY 오프셋(툴 좌표계, appsettings 규약) 자동 캘리브레이션 — 틸트 응답 측정법.
///
/// 원리: 헤드가 툴에 강체 고정이므로, 수평면 위에서 툴프레임 ±θ 회전 시 각 헤드의 거리 변화
/// 기울기 S = Δd/(2·tanθ) 가 헤드의 툴 XY 좌표(부호 포함)를 그대로 드러낸다:
///   지령 Rx=±θ → y_i = +σ·S_i,  지령 Ry=±θ → x_i = −σ·S_i  (σ = TiltReadingSignForUp ? +1 : −1)
/// 대칭(±θ) 차분으로 빔 신장(1/cosθ) 등 짝수차 공통항이 상쇄된다. 위 부호는 실기 검증된
/// 로봇 툴프레임↔설정 프레임의 툴 Z축 180° 관계(지령 +Rx → 측정 Rx gain −1, 지령 +Ry → gain +1,
/// LaserDisplacementSensor.razor 의 보정 부호 참조)를 전제하며, 산출 후 검증 항등식
/// (후보 오프셋 + 틸트 자세 거리 → ComputePose 가 지령 각도를 재현하는지)으로 재확인한다.
///
/// 자동 수평: 표면 잔여 기울기 a,b(rad)는 산출 오프셋에 y(1+b²)+b·c+a·b·x 오차를 만든다.
/// 공통항 b·c 는 무해(법선 불변)하나 교차항 a·b·x 는 비공통·치명적이므로 최종 산출은 실기울기가
/// 작을 때 해야 한다. 단 그 판단을 현재 설정 오프셋으로 하면 닭-달걀(설정이 틀려서 캘리브레이션
/// 하는데 그 설정으로 게이트) — 그래서 매 회차 <b>측정된</b> 오프셋으로 실기울기를 구하고,
/// 크면 측정값 기반 자세 보정 후 재프로브한다(최대 3회). 세 거리가 같으면 수평이라는 사실과
/// 프로브 기울기가 곧 오프셋이라는 사실은 설정과 무관하게 성립한다.
///
/// 결과는 화면 표시용으로만 반환하고 설정 파일에 쓰지 않는다(수동 반영).
/// </summary>
public sealed class LaserHeadCalibrationRoutine
{
    /// <summary>자동 수평 최대 반복 횟수.</summary>
    private const int MaxLevelIterations = 3;

    /// <summary>이 잔여 기울기(°, 측정 오프셋 기준) 미만이면 수평으로 판정하고 최종 산출.</summary>
    private const double LevelThresholdDeg = 0.5;

    /// <summary>자동 수평 1회 보정 회전 클램프(°/축). 폭주 방지.</summary>
    private const double MaxLevelCorrectionDeg = 15.0;

    private readonly CobotService _cobot;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ILogger<LaserHeadCalibrationRoutine> _logger;

    public LaserHeadCalibrationRoutine(
        CobotService cobot,
        LaserDisplacementSensorService laser,
        ILogger<LaserHeadCalibrationRoutine> logger)
    {
        _cobot = cobot;
        _laser = laser;
        _logger = logger;
    }

    /// <summary>
    /// 전체 캘리브레이션 수행: 기준 측정 → (부호 검증) → [프로브 Rx±θ/Ry±θ → 오프셋 산출 →
    /// 실기울기 판정 → 필요 시 자동 수평 후 재프로브] → 최종 산출·검증 → 앵커 복귀.
    /// 예외를 던지지 않고 항상 결과 객체로 반환한다(취소 포함). <paramref name="progress"/> 는
    /// 진행 메시지 콜백(UI 로그용).
    /// </summary>
    public async Task<LaserHeadCalibrationResult> RunAsync(
        LaserHeadCalibrationOptions options, Action<string>? progress, CancellationToken ct)
    {
        double tiltDeg = Math.Clamp(options.TiltDeg, 0.5, 5.0);
        int samples = Math.Clamp(options.SamplesPerPoint, 3, 20);
        int intervalMs = Math.Max(50, options.SampleIntervalMs);
        int settleMs = Math.Max(100, options.SettleMs);
        double vel = Math.Clamp(options.VelPct, 1, 30);
        int tool = options.Tool;

        void Report(string msg)
        {
            _logger.LogInformation("헤드 캘리브레이션: {Msg}", msg);
            progress?.Invoke(msg);
        }

        // ── 전제조건 ──────────────────────────────────────────────────
        if (!_laser.IsConnected)
            return LaserHeadCalibrationResult.Fail("레이저 변위센서 미연결.");
        if (!_cobot.IsConnected)
            return LaserHeadCalibrationResult.Fail("코봇 RPC 미연결.");
        if (!_cobot.IsServoEnabled)
            return LaserHeadCalibrationResult.Fail("서보 OFF — 서보 ON 후 실행하세요.");

        var s = _laser.Settings;
        double sigma = s.TiltReadingSignForUp ? 1.0 : -1.0;
        double tan = Math.Tan(tiltDeg * Math.PI / 180.0);
        var warnings = new List<string>();

        double[]? anchor = null;   // 자동 수평 시 갱신 — finally 복귀는 마지막 앵커 기준.
        bool atAnchor = true;      // 앵커에서 벗어난 상태인지 추적 — finally 복귀 판단용.
        bool returned = true;
        string? error = null;
        LaserHeadCalibrationResult? result = null;

        try
        {
            Report("앵커 pose 조회(무모션)…");
            anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, ct);

            // ── 기준 측정 ────────────────────────────────────────────
            Report($"기준 측정 ({samples}회 평균)…");
            var (d0, _) = await SampleDistancesAsync(samples, intervalMs, ct);
            double meanDist = Mean3(d0);
            double spread = Math.Max(Math.Max(d0[0], d0[1]), d0[2]) - Math.Min(Math.Min(d0[0], d0[1]), d0[2]);
            Report($"기준 거리: CH1={d0[0]:0.###}, CH2={d0[1]:0.###}, CH3={d0[2]:0.###} mm (스프레드 {spread:0.###}mm)");

            // 참고용 — 현재 설정 오프셋 기준 자세. 오프셋이 틀리면(캘리브레이션 사유) 부정확하므로
            // 게이트로 쓰지 않는다. 실기울기 판정은 프로브 후 측정 오프셋으로 한다.
            var curHx = new[] { s.Head1OffsetXmm, s.Head2OffsetXmm, s.Head3OffsetXmm };
            var curHy = new[] { s.Head1OffsetYmm, s.Head2OffsetYmm, s.Head3OffsetYmm };
            var cfgPose = PlanePoseCalculator.ComputePose(curHx, curHy, d0, s.TiltStandoffMm, s.TiltReadingSignForUp);
            if (cfgPose.Valid)
                Report($"(참고) 현재 설정 기준 자세: Rx={cfgPose.Rx:0.###}°, Ry={cfgPose.Ry:0.###}° — 설정이 틀리면 부정확.");

            // ── (옵션) 부호 검증: 툴 +Z 이동 시 전 채널 Δd = −σ·Δz ───────
            // 오프셋과 무관한 검증이므로 표면이 기울어도 유효하다.
            bool signCheckPassed = false;
            if (options.VerifyReadingSign)
            {
                double zmm = Math.Clamp(options.ZCheckMm, 0.5, 5.0);
                Report($"부호 검증: 툴 +Z {zmm:0.#}mm 이동…");
                atAnchor = false;
                await MoveOffsetAsync(anchor, new[] { 0.0, 0.0, zmm, 0.0, 0.0, 0.0 }, tool, vel, "부호 검증(+Z)", ct);
                await Task.Delay(settleMs, ct);
                var (dz, _) = await SampleDistancesAsync(samples, intervalMs, ct);
                await MoveOffsetAsync(anchor, new double[6], tool, vel, "앵커 복귀", ct);
                atAnchor = true;
                await Task.Delay(settleMs, ct);

                double expected = -sigma * zmm;
                for (int i = 0; i < 3; i++)
                {
                    double delta = dz[i] - d0[i];
                    if (Math.Abs(delta - expected) > 0.5)
                        throw new InvalidOperationException(
                            $"부호 검증 실패: CH{i + 1} Δd={delta:+0.###;-0.###}mm (기대 {expected:+0.###;-0.###}mm) — " +
                            "TiltReadingSignForUp 설정이 실측과 불일치하거나 측정이 불안정합니다.");
                }
                signCheckPassed = true;
                Report("부호 검증 통과.");
            }

            // ── 프로브 + 자동 수평 루프 ──────────────────────────────
            var xMeas = new double[3];
            var yMeas = new double[3];
            var stdX = new double[3];
            var stdY = new double[3];
            double[] dxp = null!, dxm = null!, dyp = null!, dym = null!;
            bool xFlipped = false, yFlipped = false;
            double dRx = 0, dRy = 0;
            double finalRx = 0, finalRy = 0, finalTilt = double.MaxValue;
            int iterUsed = 0;

            for (int iter = 1; iter <= MaxLevelIterations; iter++)
            {
                iterUsed = iter;
                ct.ThrowIfCancellationRequested();

                // 4자세 프로브: Rx±θ → Ry±θ → 앵커 복귀.
                atAnchor = false;
                (dxp, var vxp) = await TiltAndSampleAsync(anchor, new[] { 0.0, 0.0, 0.0, +tiltDeg, 0.0, 0.0 },
                    $"[{iter}] Rx +{tiltDeg:0.#}°", d0, samples, intervalMs, settleMs, tool, vel, Report, ct);
                (dxm, var vxm) = await TiltAndSampleAsync(anchor, new[] { 0.0, 0.0, 0.0, -tiltDeg, 0.0, 0.0 },
                    $"[{iter}] Rx −{tiltDeg:0.#}°", d0, samples, intervalMs, settleMs, tool, vel, Report, ct);
                (dyp, var vyp) = await TiltAndSampleAsync(anchor, new[] { 0.0, 0.0, 0.0, 0.0, +tiltDeg, 0.0 },
                    $"[{iter}] Ry +{tiltDeg:0.#}°", d0, samples, intervalMs, settleMs, tool, vel, Report, ct);
                (dym, var vym) = await TiltAndSampleAsync(anchor, new[] { 0.0, 0.0, 0.0, 0.0, -tiltDeg, 0.0 },
                    $"[{iter}] Ry −{tiltDeg:0.#}°", d0, samples, intervalMs, settleMs, tool, vel, Report, ct);
                await MoveOffsetAsync(anchor, new double[6], tool, vel, "앵커 복귀", ct);
                atAnchor = true;
                await Task.Delay(settleMs, ct);

                // 산출: y_i = +σ·S_i(Rx), x_i = −σ·S_i(Ry).
                for (int i = 0; i < 3; i++)
                {
                    double slopeRx = (dxp[i] - dxm[i]) / (2.0 * tan);
                    double slopeRy = (dyp[i] - dym[i]) / (2.0 * tan);
                    yMeas[i] = sigma * slopeRx;
                    xMeas[i] = -sigma * slopeRy;
                    // 산출값 노이즈: 평균의 분산 var/N 두 개가 차분에 합산 → √((v₊+v₋)/N)/(2tanθ)
                    stdY[i] = Math.Sqrt((vxp[i] + vxm[i]) / samples) / (2.0 * tan);
                    stdX[i] = Math.Sqrt((vyp[i] + vym[i]) / samples) / (2.0 * tan);
                }

                // 검증 항등식(부호 확정): 지령 +Rx=θ → 측정 Rx = 기준−θ (gain −1),
                // 지령 +Ry=θ → 측정 Ry = 기준+θ (gain +1). 기울어진 상태에선 2차 오차가 있으므로
                // 루프 중에는 40% 허용, 수평 확보된 최종 회차만 아래에서 20% 재검한다.
                var refPose = PlanePoseCalculator.ComputePose(xMeas, yMeas, d0, s.TiltStandoffMm, s.TiltReadingSignForUp);
                double refRx = refPose.Valid ? refPose.Rx : 0;
                double refRy = refPose.Valid ? refPose.Ry : 0;

                var poseXp = PlanePoseCalculator.ComputePose(xMeas, yMeas, dxp, s.TiltStandoffMm, s.TiltReadingSignForUp);
                if (!poseXp.Valid)
                    throw new InvalidOperationException($"검증 계산 불가(Rx 패스): {poseXp.Note}");
                dRx = poseXp.Rx - refRx;
                if (dRx > 0)
                {
                    // 전 헤드 Y 반전은 평면 기울기 b 만 반전 → Rx 만 부호 반전(Ry 불변).
                    for (int i = 0; i < 3; i++) yMeas[i] = -yMeas[i];
                    yFlipped = true;
                    dRx = -dRx;
                    warnings.Add("Rx 검증 부호 불일치로 전 헤드 Y 를 반전했습니다 — 부호 규약 변동 여부를 확인하세요.");
                }
                if (Math.Abs(dRx - (-tiltDeg)) > 0.4 * tiltDeg)
                    throw new InvalidOperationException(
                        $"검증 항등식 불일치(Rx): 측정 ΔRx={dRx:0.###}° (기대 {-tiltDeg:0.###}°) — 부호 규약 변동/측정 불안정 의심.");

                var poseYp = PlanePoseCalculator.ComputePose(xMeas, yMeas, dyp, s.TiltStandoffMm, s.TiltReadingSignForUp);
                if (!poseYp.Valid)
                    throw new InvalidOperationException($"검증 계산 불가(Ry 패스): {poseYp.Note}");
                dRy = poseYp.Ry - refRy;
                if (dRy < 0)
                {
                    // 전 헤드 X 반전은 평면 기울기 a 만 반전 → Ry 만 부호 반전(Rx 불변).
                    for (int i = 0; i < 3; i++) xMeas[i] = -xMeas[i];
                    xFlipped = true;
                    dRy = -dRy;
                    warnings.Add("Ry 검증 부호 불일치로 전 헤드 X 를 반전했습니다 — 부호 규약 변동 여부를 확인하세요.");
                }
                if (Math.Abs(dRy - tiltDeg) > 0.4 * tiltDeg)
                    throw new InvalidOperationException(
                        $"검증 항등식 불일치(Ry): 측정 ΔRy={dRy:0.###}° (기대 {tiltDeg:0.###}°) — 부호 규약 변동/측정 불안정 의심.");

                // 실기울기 판정 — 측정 오프셋 기준(부호 반전 반영해 재계산).
                var realPose = PlanePoseCalculator.ComputePose(xMeas, yMeas, d0, s.TiltStandoffMm, s.TiltReadingSignForUp);
                if (!realPose.Valid)
                    throw new InvalidOperationException($"측정 오프셋 기준 자세 계산 불가: {realPose.Note}");
                finalRx = realPose.Rx;
                finalRy = realPose.Ry;
                finalTilt = Math.Max(Math.Abs(finalRx), Math.Abs(finalRy));
                Report($"[{iter}] 측정 오프셋 기준 실기울기: Rx={finalRx:0.###}°, Ry={finalRy:0.###}°");

                if (finalTilt < LevelThresholdDeg)
                    break;   // 수평 — 이 회차 산출이 최종.

                if (!options.AutoLevel)
                {
                    warnings.Add(
                        $"잔여 기울기 {finalTilt:0.##}° 상태에서 산출 — 교차 오차 최대 ≈ " +
                        $"{Math.Abs(Math.Tan(finalRx * Math.PI / 180.0) * Math.Tan(finalRy * Math.PI / 180.0)) * MaxAbs3(xMeas):0.##}mm 가능. " +
                        "'자동 수평'을 켜고 재실행을 권장합니다.");
                    break;
                }

                if (iter == MaxLevelIterations)
                {
                    warnings.Add(
                        $"자동 수평 {MaxLevelIterations}회 후에도 잔여 기울기 {finalTilt:0.##}° — 결과 신뢰도가 낮습니다. " +
                        "표면 평탄도/로봇 시작 자세를 확인 후 재실행하세요.");
                    break;
                }

                // 자동 수평: '보정 적용'과 동일 부호(offset = [+Rx, −Ry]) — 단 측정 오프셋 기반이라 신뢰 가능.
                double corrRx = Math.Clamp(finalRx, -MaxLevelCorrectionDeg, MaxLevelCorrectionDeg);
                double corrRy = Math.Clamp(-finalRy, -MaxLevelCorrectionDeg, MaxLevelCorrectionDeg);
                if (Math.Abs(finalRx) > MaxLevelCorrectionDeg || Math.Abs(finalRy) > MaxLevelCorrectionDeg)
                    warnings.Add($"자동 수평 보정량이 클램프(±{MaxLevelCorrectionDeg:0.#}°)됨 — 시작 자세가 크게 기울어 있습니다.");
                double corrDeg = Math.Max(Math.Abs(corrRx), Math.Abs(corrRy));
                Report($"[{iter}] 자동 수평: Rx={corrRx:0.###}°, Ry={corrRy:0.###}° 회전 적용 " +
                       $"(빔 스팟 횡이동 ≈ {meanDist * Math.Tan(corrDeg * Math.PI / 180.0):0.#}mm)…");
                atAnchor = false;
                await MoveOffsetAsync(anchor, new[] { 0.0, 0.0, 0.0, corrRx, corrRy, 0.0 }, tool, vel, "자동 수평", ct);
                await Task.Delay(settleMs, ct);

                // 새 자세가 새 기준: 앵커 재조회 + 기준 거리 재측정.
                anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, ct);
                atAnchor = true;
                Report($"[{iter}] 기준 재측정…");
                (d0, _) = await SampleDistancesAsync(samples, intervalMs, ct);
                meanDist = Mean3(d0);
            }

            // 수평 확보된 최종 회차는 검증 항등식을 엄격(20%)하게 재검한다.
            if (finalTilt < LevelThresholdDeg)
            {
                if (Math.Abs(dRx - (-tiltDeg)) > 0.2 * tiltDeg)
                    throw new InvalidOperationException(
                        $"검증 항등식 불일치(Rx, 최종): 측정 ΔRx={dRx:0.###}° (기대 {-tiltDeg:0.###}°) — 측정 불안정 의심.");
                if (Math.Abs(dRy - tiltDeg) > 0.2 * tiltDeg)
                    throw new InvalidOperationException(
                        $"검증 항등식 불일치(Ry, 최종): 측정 ΔRy={dRy:0.###}° (기대 {tiltDeg:0.###}°) — 측정 불안정 의심.");
                Report($"검증 통과: ΔRx={dRx:0.###}°(기대 {-tiltDeg:0.###}), ΔRy={dRy:0.###}°(기대 {tiltDeg:0.###}).");

                if (finalTilt > 0.3)
                    warnings.Add(
                        $"최종 잔여 기울기 {finalTilt:0.##}° — 전 헤드 공통 바이어스 ≈ " +
                        $"{Math.Tan(finalTilt * Math.PI / 180.0) * meanDist:0.##}mm 가능(평면 Rx/Ry 계산에는 무해).");
            }

            // ── 신뢰도: 짝수모드 잔차(공통항 제거) + 노이즈 전파 ─────────
            var evenX = new double[3];
            var evenY = new double[3];
            for (int i = 0; i < 3; i++)
            {
                evenX[i] = (dxp[i] + dxm[i]) / 2.0 - d0[i];
                evenY[i] = (dyp[i] + dym[i]) / 2.0 - d0[i];
            }
            double medX = Median3(evenX);
            double medY = Median3(evenY);

            var heads = new HeadCalibrationEntry[3];
            for (int i = 0; i < 3; i++)
            {
                double residual = Math.Max(Math.Abs(evenX[i] - medX), Math.Abs(evenY[i] - medY));
                double worstStd = Math.Max(stdX[i], stdY[i]);
                string conf = worstStd > 2.0 ? "불량"
                    : worstStd > 0.5 || residual > 0.3 ? "주의"
                    : "양호";
                // 수평 미달 상태 산출은 신뢰도 강등(교차 오차 위험).
                if (finalTilt >= LevelThresholdDeg)
                    conf = finalTilt > 2.0 ? "불량" : conf == "양호" ? "주의" : conf;
                if (worstStd > 0.25 * Math.Max(1.0, Math.Max(Math.Abs(xMeas[i]), Math.Abs(yMeas[i]))))
                    warnings.Add($"CH{i + 1} 노이즈 대비 신호 부족(σ={worstStd:0.###}mm) — 틸트 각도 또는 샘플 수 증가를 권장합니다.");
                heads[i] = new HeadCalibrationEntry(
                    i + 1, xMeas[i], yMeas[i], curHx[i], curHy[i], stdX[i], stdY[i], residual, conf);
            }

            // 퇴화 판정: 측정 오프셋 삼각형 면적(현 배치 기대 ≈ 576mm²).
            double area = 0.5 * Math.Abs(
                (xMeas[1] - xMeas[0]) * (yMeas[2] - yMeas[0]) -
                (xMeas[2] - xMeas[0]) * (yMeas[1] - yMeas[0]));
            if (area < 100)
                warnings.Add($"측정 헤드 배치가 거의 일직선(면적 {area:0.#}mm²) — 결과를 신뢰할 수 없습니다.");

            result = new LaserHeadCalibrationResult(
                true, null, heads, finalRx, finalRy, meanDist,
                signCheckPassed, xFlipped, yFlipped, area,
                iterUsed, finalTilt, warnings,
                BuildJsonSnippet(xMeas, yMeas), ReturnedToAnchor: true);
            Report($"캘리브레이션 완료 (프로브 {iterUsed}회, 최종 잔여 기울기 {finalTilt:0.###}°).");
        }
        catch (OperationCanceledException)
        {
            error = "중단됨 (사용자 취소).";
            Report(error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "헤드 캘리브레이션 실패");
            error = ex.Message;
            Report($"실패: {error}");
        }
        finally
        {
            // 성공/실패/취소와 무관하게 앵커 복귀 보장(자동 수평 후에는 수평 자세인 마지막 앵커 기준).
            // 사용자 취소 토큰과 분리된 자체 15초 한도 사용.
            if (anchor is not null && !atAnchor)
            {
                try
                {
                    Report("앵커 복귀 시도…");
                    using var homeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, new double[6],
                        tool: tool, vel: vel, ct: homeCts.Token);
                    returned = rc == 0;
                    if (rc != 0)
                        Report($"앵커 복귀 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)} — 로봇 위치를 수동 확인하세요.");
                }
                catch (Exception ex)
                {
                    returned = false;
                    Report($"앵커 복귀 실패: {ex.Message} — 로봇 위치를 수동 확인하세요.");
                }
            }
        }

        return result ?? LaserHeadCalibrationResult.Fail(error ?? "알 수 없는 오류.", returned);
    }

    /// <summary>앵커 기준 툴프레임 오프셋 이동. rc≠0 이면 사유를 붙여 예외.</summary>
    private async Task MoveOffsetAsync(
        double[] anchor, double[] offset, int tool, double vel, string what, CancellationToken ct)
    {
        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset, tool: tool, vel: vel, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException($"{what} 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
    }

    /// <summary>틸트 자세로 이동 → 안정화 → 샘플링. 기준 대비 Δd 를 진행 로그로 남긴다.</summary>
    private async Task<(double[] Mean, double[] Var)> TiltAndSampleAsync(
        double[] anchor, double[] offset, string label, double[] d0,
        int samples, int intervalMs, int settleMs, int tool, double vel,
        Action<string> report, CancellationToken ct)
    {
        report($"{label} 틸트…");
        await MoveOffsetAsync(anchor, offset, tool, vel, label, ct);
        await Task.Delay(settleMs, ct);
        var (mean, var_) = await SampleDistancesAsync(samples, intervalMs, ct);
        report($"{label}: Δd CH1={mean[0] - d0[0]:+0.000;-0.000}, CH2={mean[1] - d0[1]:+0.000;-0.000}, " +
               $"CH3={mean[2] - d0[2]:+0.000;-0.000} mm");
        return (mean, var_);
    }

    /// <summary>
    /// 3채널 모두 유효(Enabled)한 스냅샷만 <paramref name="n"/>개 수집해 채널별 평균/분산 반환.
    /// 무효 스냅샷 포함 총 시도가 2n 을 넘으면 실패 처리(범위 이탈 안내).
    /// </summary>
    private async Task<(double[] Mean, double[] Var)> SampleDistancesAsync(
        int n, int intervalMs, CancellationToken ct)
    {
        var sum = new double[3];
        var sumSq = new double[3];
        int got = 0, attempts = 0;
        while (got < n)
        {
            ct.ThrowIfCancellationRequested();
            if (++attempts > n * 2)
                throw new InvalidOperationException(
                    "유효 샘플 부족 — 채널이 측정 범위를 벗어났습니다. 틸트 각도를 줄이거나, " +
                    "자동 수평 직후라면 표면 중앙으로 재배치 후 재시도하세요.");
            var r = _laser.GetReadings();
            if (r.Count >= 3 && r[0].Enabled && r[1].Enabled && r[2].Enabled)
            {
                for (int i = 0; i < 3; i++)
                {
                    sum[i] += r[i].Value;
                    sumSq[i] += r[i].Value * r[i].Value;
                }
                got++;
            }
            await Task.Delay(intervalMs, ct);
        }

        var mean = new double[3];
        var var_ = new double[3];
        for (int i = 0; i < 3; i++)
        {
            mean[i] = sum[i] / n;
            var_[i] = Math.Max(0.0, sumSq[i] / n - mean[i] * mean[i]);
        }
        return (mean, var_);
    }

    private static double Mean3(double[] v) => (v[0] + v[1] + v[2]) / 3.0;

    private static double MaxAbs3(double[] v) =>
        Math.Max(Math.Abs(v[0]), Math.Max(Math.Abs(v[1]), Math.Abs(v[2])));

    private static double Median3(double[] v)
    {
        var c = new[] { v[0], v[1], v[2] };
        Array.Sort(c);
        return c[1];
    }

    /// <summary>appsettings.json 에 그대로 붙여넣을 Head*Offset* 6줄(후행 콤마 포함).</summary>
    private static string BuildJsonSnippet(double[] x, double[] y)
    {
        string J(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        return string.Join("\n",
            $"\"Head1OffsetXmm\": {J(x[0])},",
            $"\"Head1OffsetYmm\": {J(y[0])},",
            $"\"Head2OffsetXmm\": {J(x[1])},",
            $"\"Head2OffsetYmm\": {J(y[1])},",
            $"\"Head3OffsetXmm\": {J(x[2])},",
            $"\"Head3OffsetYmm\": {J(y[2])},");
    }
}
