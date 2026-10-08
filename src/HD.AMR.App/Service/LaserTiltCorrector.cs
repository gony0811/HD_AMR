using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 레이저 3점 측정 틸트(rx, ry)를 툴 회전으로 0 에 맞추는 수평 보정 루프 — 시퀀스 ④ Phase C 와
/// /laser '보정 적용'이 공용으로 쓴다.
///
/// 보정량은 응답 행렬 J(행=측정 rx/ry, 열=툴 Rx/Ry)로 u = −J⁻¹·tilt 를 푼다.
///
/// <b>기본은 공칭 J = diag(−1, +1)</b> — 헤드 캘리브레이션(<see cref="LaserHeadCalibrationRoutine"/>)이 헤드
/// 오프셋을 이 응답이 되도록 정의하므로, 캘리브레이션 결과가 적용돼 있으면 J 는 이 값으로 고정된다
/// (u = [+rx, −ry], 종전 규약과 동일). 2026-10-07/08 발산의 원인은 규약이 아니라 appsettings 헤드 오프셋
/// (H1/H2 뒤바뀜)이었다. <see cref="TiltLevelOptions.MeasureResponse"/> 를 켜면 3° 프로브로 J 를 실측한다
/// (진단용 — 비평탄 면에서는 측정 잡음이 보정량으로 증폭된다).
///
/// 매 보정 후 예측 틸트(tilt + J·u)와 실측을 비교해 크게 어긋나면 "측정면 비평탄"으로 중단한다 —
/// 강체 평면이면 회전량만큼만 변해야 하므로, 어긋남은 스팟이 비드·요철을 넘나든다는 뜻이다.
/// </summary>
public sealed class LaserTiltCorrector
{
    private readonly CobotService _cobot;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ILogger<LaserTiltCorrector> _logger;

    public LaserTiltCorrector(CobotService cobot, LaserDisplacementSensorService laser, ILogger<LaserTiltCorrector> logger)
    {
        _cobot = cobot;
        _laser = laser;
        _logger = logger;
    }

    /// <summary>
    /// 측정 → (첫 보정 전 1회) 응답 측정 → 보정 회전 → 재측정을 기준 통과·발산·횟수 초과까지 반복한다.
    /// <paramref name="onMeasured"/> 는 매 측정마다(회차, pose) 호출된다 — 모니터 표시용.
    /// </summary>
    public async Task<TiltLevelResult> LevelAsync(TiltLevelOptions o, Action<string>? progress,
        Action<int, PlanePose>? onMeasured, CancellationToken ct)
    {
        void Report(string m)
        {
            _logger.LogInformation("틸트 보정: {Message}", m);
            progress?.Invoke(m);
        }

        double[,]? jac = o.MeasureResponse ? null : NominalJacobian();
        double prevTilt = double.PositiveInfinity;
        (double Rx, double Ry)? predicted = null;
        double lastAppliedMag = 0;

        for (var iter = 0; ; iter++)
        {
            ct.ThrowIfCancellationRequested();

            var pose = await SampleAsync(ct);
            if (!pose.Valid)
                return TiltLevelResult.Fail($"레이저 평면 측정 실패: {pose.Note}", iter, null, jac);

            onMeasured?.Invoke(iter, pose);
            var ch = _laser.GetReadings();
            var chText = ch.Count >= 3 ? $" [CH {ch[0].Value:0.##}/{ch[1].Value:0.##}/{ch[2].Value:0.##}mm]" : "";
            Report($"측정 {iter + 1}: rx={pose.Rx:0.###}°, ry={pose.Ry:0.###}°, z={pose.Z:0.#}mm{chText}");

            // 반응 확인 — 강체 평면이면 직전 보정의 예측대로 변해야 한다.
            if (predicted is { } pr && !IsResponseConsistent(pr.Rx, pr.Ry, pose.Rx, pose.Ry, lastAppliedMag))
                return TiltLevelResult.Fail(
                    $"레이저 측정면 비평탄 — 예측 (rx={pr.Rx:0.##}°, ry={pr.Ry:0.##}°) vs 실측 (rx={pose.Rx:0.##}°, ry={pose.Ry:0.##}°). " +
                    "스팟이 비드/요철에 걸렸을 수 있습니다 — 평탄 셀 위치를 확인하세요.", iter, pose, jac);

            if (Math.Abs(pose.Rx) < o.ThresholdDeg && Math.Abs(pose.Ry) < o.ThresholdDeg)
                return new TiltLevelResult(true, $"틸트 기준 통과 (|rx|,|ry| < {o.ThresholdDeg}°) — 보정 {iter}회",
                    iter, pose, jac);

            // 발산 가드 — 보정 후 오히려 커지면 더 돌리지 않는다.
            var tilt = Math.Max(Math.Abs(pose.Rx), Math.Abs(pose.Ry));
            if (iter > 0 && tilt > prevTilt * o.DivergenceFactor)
                return TiltLevelResult.Fail(
                    $"틸트 보정 발산: {prevTilt:0.##}° → {tilt:0.##}° (rx={pose.Rx:0.###}°, ry={pose.Ry:0.###}°) — " +
                    "측정면이 평탄하지 않거나 레이저 헤드 기하(/laser) 확인이 필요합니다.", iter, pose, jac);
            prevTilt = tilt;

            if (iter >= o.MaxCorrections)
                return TiltLevelResult.Fail(
                    $"틸트 보정 {o.MaxCorrections}회 후에도 평탄 기준 미달 " +
                    $"(rx={pose.Rx:0.###}°, ry={pose.Ry:0.###}°, 기준={o.ThresholdDeg}°) — " +
                    "헤드 위치 캘리브레이션(/laser)으로 헤드 기하 확인이 필요합니다.", iter, pose, jac);

            // 첫 보정 전 1회: 응답 행렬. 툴 Rx +δ → 측정, Rx −δ·Ry +δ → 측정 (끝나면 Ry +δ 상태).
            if (jac is null)
            {
                Report($"틸트 응답 측정: 툴 Rx/Ry 각 {o.ProbeDeg}° 프로브 회전");
                var rcA = await RotateToolAsync(o.Tool, o.ProbeDeg, 0, o, Report, ct);
                if (rcA != 0)
                    return TiltLevelResult.Fail($"틸트 응답 프로브 이동 실패 (rc={rcA}){FairinoErrorCodes.Suffix(rcA)}.", iter, pose, null);
                var pA = await SampleAsync(ct);
                var rcB = await RotateToolAsync(o.Tool, -o.ProbeDeg, o.ProbeDeg, o, Report, ct);
                if (rcB != 0)
                    return TiltLevelResult.Fail($"틸트 응답 프로브 이동 실패 (rc={rcB}){FairinoErrorCodes.Suffix(rcB)}.", iter, pose, null);
                var pB = await SampleAsync(ct);
                if (!pA.Valid || !pB.Valid)
                    return TiltLevelResult.Fail($"틸트 응답 측정 실패: {(pA.Valid ? pB.Note : pA.Note)}", iter, pose, null);

                jac = BuildJacobian(pose.Rx, pose.Ry, pA.Rx, pA.Ry, pB.Rx, pB.Ry, o.ProbeDeg);
                var det = Det(jac);
                Report($"틸트 응답 J=[[{jac[0, 0]:0.###}, {jac[0, 1]:0.###}], [{jac[1, 0]:0.###}, {jac[1, 1]:0.###}]] " +
                       $"(det={det:0.###}; 행=측정 rx/ry, 열=툴 Rx/Ry)");
                if (Math.Abs(det) < o.MinJacobianDet)
                    return TiltLevelResult.Fail(
                        $"틸트 응답이 축을 구분하지 못합니다 (det={det:0.###}) — 측정면 평탄도·레이저 스팟 위치를 확인하세요.",
                        iter, pB, jac);

                pose = pB;   // 현재 자세(Ry +δ)의 측정값에서 보정량을 계산
            }

            var (uRx, uRy) = Solve(jac, pose.Rx, pose.Ry);
            var applyRx = Math.Clamp(uRx, -o.MaxCorrectionDeg, o.MaxCorrectionDeg);
            var applyRy = Math.Clamp(uRy, -o.MaxCorrectionDeg, o.MaxCorrectionDeg);
            Report($"보정 {iter + 1}: 툴 Rx={applyRx:+0.###;-0.###}°, Ry={applyRy:+0.###;-0.###}° 회전" +
                   (applyRx != uRx || applyRy != uRy ? $" (계산 {uRx:0.###}°, {uRy:0.###}° → ±{o.MaxCorrectionDeg}° 클램프)" : ""));

            var rc = await RotateToolAsync(o.Tool, applyRx, applyRy, o, Report, ct);
            if (rc != 0)
                return TiltLevelResult.Fail($"틸트 보정 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.", iter, pose, jac);

            predicted = (pose.Rx + jac[0, 0] * applyRx + jac[0, 1] * applyRy,
                         pose.Ry + jac[1, 0] * applyRx + jac[1, 1] * applyRy);
            lastAppliedMag = Math.Sqrt(applyRx * applyRx + applyRy * applyRy);
        }
    }

    /// <summary>공칭 응답 — 헤드 캘리브레이션 규약(툴 +Rx → 측정 rx gain −1, 툴 +Ry → 측정 ry gain +1).</summary>
    public static double[,] NominalJacobian() => new[,] { { -1.0, 0.0 }, { 0.0, 1.0 } };

    /// <summary>보정 후 실측이 예측과 맞는가 — 허용 오차 max(2°, 0.5×|적용 회전|), 축별.</summary>
    public static bool IsResponseConsistent(double predRx, double predRy, double rx, double ry, double appliedMagDeg)
    {
        var tol = Math.Max(2.0, 0.5 * appliedMagDeg);
        return Math.Abs(rx - predRx) <= tol && Math.Abs(ry - predRy) <= tol;
    }

    /// <summary>레이저 3점 측정을 3회 샘플링해 평균 pose 반환(100ms 간격).</summary>
    public async Task<PlanePose> SampleAsync(CancellationToken ct)
    {
        double rxSum = 0, rySum = 0, rzSum = 0, zSum = 0;
        int validCount = 0;
        string? lastNote = null;

        for (var i = 0; i < 3; i++)
        {
            var p = _laser.GetPlanePose();
            if (p.Valid)
            {
                rxSum += p.Rx;
                rySum += p.Ry;
                rzSum += p.Rz;
                zSum += p.Z;
                validCount++;
            }
            else
            {
                lastNote = p.Note;
            }
            if (i < 2) await Task.Delay(100, ct);
        }

        if (validCount == 0)
            return PlanePose.Invalid(lastNote ?? "3회 측정 모두 무효");

        return new PlanePose(
            0, 0, zSum / validCount,
            rxSum / validCount, rySum / validCount, rzSum / validCount,
            new double[] { 0, 0, 1 }, true, null);
    }

    /// <summary>현재 자세 기준 툴 회전 오프셋(위치 고정, Rz=0 — 3점 거리로 yaw 미결정) 후
    /// 정착 대기 + 레이저 판독 안정화 대기(앰프 평균화 필터 지연 — 덜 수렴한 값은 가짜 틸트가 된다).</summary>
    private async Task<int> RotateToolAsync(int tool, double rx, double ry, TiltLevelOptions o,
        Action<string> report, CancellationToken ct)
    {
        var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, ct);
        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0,
            new[] { 0.0, 0.0, 0.0, rx, ry, 0.0 }, tool: tool, vel: o.Velocity, ct: ct);
        if (rc != 0) return rc;

        await Task.Delay(o.SettleMs, ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stable = await _laser.WaitForStableReadingsAsync(100, o.StabilizeTimeoutMs, ct);
        report(stable
            ? $"판독 안정 ({(o.SettleMs + sw.ElapsedMilliseconds) / 1000.0:0.0}s)"
            : $"판독 안정화 한도({o.StabilizeTimeoutMs}ms) 초과 — 필터 지연/진동 가능, 측정을 계속합니다.");
        return 0;
    }

    /// <summary>프로브 측정으로 응답 행렬 J(측정 [rx,ry] / 툴 [Rx,Ry], 소각 선형화)를 만든다.
    /// p0 = 시작, pA = 툴 Rx +δ, pB = 시작 대비 툴 Ry +δ (Rx 는 원복).</summary>
    public static double[,] BuildJacobian(
        double rx0, double ry0, double rxA, double ryA, double rxB, double ryB, double probeDeg) => new[,]
    {
        { (rxA - rx0) / probeDeg, (rxB - rx0) / probeDeg },
        { (ryA - ry0) / probeDeg, (ryB - ry0) / probeDeg },
    };

    /// <summary>측정 틸트를 0 으로 만드는 툴 회전 u = −J⁻¹·[rx, ry].</summary>
    public static (double Rx, double Ry) Solve(double[,] j, double rx, double ry)
    {
        var det = Det(j);
        return (-(j[1, 1] * rx - j[0, 1] * ry) / det,
                -(-j[1, 0] * rx + j[0, 0] * ry) / det);
    }

    private static double Det(double[,] j) => j[0, 0] * j[1, 1] - j[0, 1] * j[1, 0];
}

/// <summary>수평 보정 루프 옵션.</summary>
public sealed class TiltLevelOptions
{
    public int Tool { get; init; } = 1;
    public double Velocity { get; init; } = 10;

    /// <summary>판정 임계값(°). |rx|, |ry| 모두 이 값 미만이면 완료.</summary>
    public double ThresholdDeg { get; init; } = 1.0;

    /// <summary>보정 회전 최대 횟수(프로브 제외).</summary>
    public int MaxCorrections { get; init; } = 3;

    /// <summary>1회 보정 클램프(°/축).</summary>
    public double MaxCorrectionDeg { get; init; } = 10.0;

    /// <summary>응답 측정 프로브 회전량(°/축).</summary>
    public double ProbeDeg { get; init; } = 3.0;

    /// <summary>|det J| 하한 — 이하면 프로브 응답이 축을 구분하지 못한 것(이상적 장착 = 1).</summary>
    public double MinJacobianDet { get; init; } = 0.2;

    /// <summary>보정 후 틸트가 직전보다 이 배수 이상 커지면 발산으로 보고 중단.</summary>
    public double DivergenceFactor { get; init; } = 1.2;

    /// <summary>회전 후 정착 대기(ms) — 이후 판독 안정화 대기가 이어진다.</summary>
    public int SettleMs { get; init; } = 500;

    /// <summary>판독 안정화 대기 한도(ms, 0=생략). 헤드 캘리브레이션과 같은 기준(|Δ|&lt;0.05mm).</summary>
    public int StabilizeTimeoutMs { get; init; } = 3000;

    /// <summary>true = 첫 보정 전 3° 프로브로 응답 J 를 실측(진단용). false(기본) = 공칭 J=diag(−1,+1) —
    /// 헤드 캘리브레이션 결과가 적용돼 있으면 성립한다.</summary>
    public bool MeasureResponse { get; init; }
}

/// <summary>수평 보정 결과. <see cref="Jacobian"/> 은 측정된 응답 행렬(측정 전 실패면 null).</summary>
public sealed record TiltLevelResult(bool Success, string Message, int Corrections, PlanePose? FinalPose,
    double[,]? Jacobian)
{
    public static TiltLevelResult Fail(string message, int corrections, PlanePose? pose, double[,]? jac)
        => new(false, message, corrections, pose, jac);
}
