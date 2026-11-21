using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑰⁺ 묶음 마지막 용접선 끝 WD 확인 — 묶음 좌표계는 점1(첫 용접선 시작)·점2(마지막 용접선 <b>시작</b>) 두 실측점으로 잡으므로
/// 마지막 용접선은 그 기울기를 연장(외삽)해 순회한다. 바닥이 그 구간에서 다르게 기울면 끝으로 갈수록 초점이 어긋난다
/// (실기 2026-11-17 16:52 B L1-R04C13 #4: 점1→점2 기울기 2.9° 를 360mm 더 연장 — 툴이 13mm 올라감).
///
/// 동작: 지금 자리(마지막 용접선 시작 = 점2, 연속 이동 직후)에서 레이저 평면 거리를 재고, 좌표계 X 를 따라 용접선 길이만큼
/// 같은 높이·자세로 옮겨 다시 잰 뒤 돌아온다. 두 값의 차이만큼 좌표계를 시작점 둘레로 기울여(Y 축 회전) 다음 ⑱ 이 그 기울기를
/// 따르게 한다. 차이가 <see cref="MaxEndDeltaMm"/> 를 넘으면 좌표계가 크게 틀린 것으로 보고 멈춘다. 레이저가 유효값을 못 내면
/// 좌표계를 그대로 두고 진행한다(종전 동작).
///
/// 오케스트레이터가 묶음 마지막 팔로워에만 <see cref="RequestBagKey"/> 를 켜고 이 스텝을 넣는다 — 그 밖에서는 아무것도 하지 않는다.
/// </summary>
public class BundleEndWdStep : ISequenceStep
{
    public const string StepKey = "bundleEndWd";

    /// <summary>이 스텝을 실제로 수행하라는 표시(bool true) — 묶음 마지막 팔로워에서 오케스트레이터가 켠다.</summary>
    public const string RequestBagKey = "bundle.endWdCheck";

    /// <summary>끝점과 시작점 레이저 거리 차 한계 [mm] — 넘으면 좌표계가 크게 틀렸거나 다른 면을 본 것으로 보고 멈춘다.</summary>
    internal const double MaxEndDeltaMm = 30.0;

    /// <summary>이보다 짧은 용접선은 기울기를 다시 재지 않는다 [mm].</summary>
    internal const double MinSeamLengthMm = 50.0;

    private const int StableWindow = 5;
    private const double StableBandMm = 0.5;
    private const int StabilizeTimeoutMs = 4000;
    private const int PollMs = 100;

    /// <summary>
    /// 끝 WD 확인 사용 여부 파라미터 — on/off(1/0, true/false). 비우면 검사 센서 모드로 정한다:
    /// 가스 검사기는 끄고(구역을 끊김 없이 이어서 순회 — 재측정 왕복이 사이클을 늘린다), 비전은 켠다.
    /// </summary>
    public const string EnabledKey = "Sequence.Bundle.EndWdCheck";

    private readonly CobotService _cobot;
    private readonly LaserDisplacementSensorService _laser;
    private readonly ParameterService _param;
    private readonly bool _useGas;
    private readonly ILogger<BundleEndWdStep> _logger;

    public BundleEndWdStep(CobotService cobot, LaserDisplacementSensorService laser, ParameterService param,
        IOptions<InspectionSensorSettings> sensor, ILogger<BundleEndWdStep> logger)
    {
        _cobot = cobot;
        _laser = laser;
        _param = param;
        _useGas = sensor.Value.UsesGasDetector;
        _logger = logger;
    }

    /// <summary><see cref="EnabledKey"/> 값 해석 — 알 수 없는 값·빈 값이면 센서 모드 기본(가스=끔, 비전=켬).</summary>
    internal static bool ResolveEnabled(string? raw, bool usesGas)
        => raw?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => !usesGas,
        };

    public string Key => StepKey;
    public string DisplayName => "묶음 마지막 용접선 끝 WD 확인";
    public int DefaultOrder => 1190;   // ② 연속 이동(200) 뒤, ⑱ 검사 수행(1200) 앞

    public StepValidation Validate(SequenceContext context) => StepValidation.Ok();

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        if (!context.Bag.TryGetValue(RequestBagKey, out var req) || req is not true)
            return StepResult.Ok("묶음 마지막 용접선 아님 — 생략.");
        string? raw = null;
        try { raw = await _param.GetAsync(EnabledKey); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[끝 WD 확인] '{Key}' 읽기 실패 — 센서 모드 기본값을 씁니다: {Msg}", EnabledKey, ex.Message);
        }
        if (!ResolveEnabled(raw, _useGas))
        {
            _logger.LogInformation("[끝 WD 확인] 사용 안 함('{Key}'={Raw}, {Mode}) — 리더 좌표계를 그대로 연장해 순회합니다.",
                EnabledKey, string.IsNullOrWhiteSpace(raw) ? "빈 값" : raw.Trim(), _useGas ? "가스 기본 끔" : "설정값");
            return StepResult.Ok($"끝 WD 확인 사용 안 함('{EnabledKey}') — 좌표계 그대로.");
        }
        if (!context.Bag.TryGetValue(InternalWorkpieceFrame.BagKey, out var fv) || fv is not double[] { Length: 6 } frame)
            return StepResult.Ok("작업물 좌표계 없음 — 생략.");
        if (context.SeamStartW is not { Length: >= 3 } s || context.SeamEndW is not { Length: >= 3 } e)
            return StepResult.Ok("용접선 끝점 없음 — 생략.");
        var lengthMm = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (e[i] - s[i]) * (e[i] - s[i]))) * 1000.0;
        if (lengthMm < MinSeamLengthMm)
            return StepResult.Ok($"용접선 {lengthMm:0}mm — 짧아 생략.");
        if (!_laser.IsConnected)
        {
            _logger.LogWarning("[끝 WD 확인] 레이저 미연결 — 좌표계를 그대로 씁니다.");
            return StepResult.Ok("레이저 미연결 — 좌표계 그대로.");
        }

        var start = await _cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct);
        var z0 = await SampleStableZAsync(ct);
        if (z0 is null)
        {
            _logger.LogWarning("[끝 WD 확인] 시작점 레이저 유효값 없음 — 좌표계를 그대로 씁니다.");
            return StepResult.Ok("시작점 레이저 측정 실패 — 좌표계 그대로.");
        }

        var end = EndProbePose(start, frame, lengthMm);
        var rc = await _cobot.Rpc.MoveLAsync(end, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
        {
            _logger.LogWarning("[끝 WD 확인] 끝점 이동 실패 (rc={Rc}){Desc} — 시작점으로 돌아가 좌표계를 그대로 씁니다.",
                rc, FairinoErrorCodes.Suffix(rc));
            await ReturnAsync(start, context, ct);
            return StepResult.Ok($"끝점 이동 실패(rc={rc}) — 좌표계 그대로.");
        }
        var z1 = await SampleStableZAsync(ct);
        await ReturnAsync(start, context, ct);
        if (z1 is null)
        {
            _logger.LogWarning("[끝 WD 확인] 끝점 레이저 유효값 없음 — 좌표계를 그대로 씁니다.");
            return StepResult.Ok("끝점 레이저 측정 실패 — 좌표계 그대로.");
        }

        var delta = z1.Value - z0.Value;
        if (Math.Abs(delta) > MaxEndDeltaMm)
        {
            var msg = $"묶음 마지막 용접선 끝 WD 가 예상과 {delta:+0.0;-0.0}mm 다릅니다(시작 {z0:0.0} → 끝 {z1:0.0}mm, 한계 ±{MaxEndDeltaMm:0}mm) — " +
                      "좌표계가 크게 틀렸거나 끝에서 다른 면을 봤습니다. 검사하지 않고 멈춥니다.";
            _logger.LogWarning("[끝 WD 확인] {Msg}", msg);
            return StepResult.Fail(msg);
        }

        var axis = context.OpticalAxis ?? ToolAxisDir.MinusZ;
        var corrected = TiltFrameToEnd(frame, start, axis, lengthMm, delta, out var tiltDeg);
        context.Bag[InternalWorkpieceFrame.BagKey] = corrected;
        _logger.LogInformation(
            "[끝 WD 확인] 용접선 {L:0}mm 끝 레이저 {Z0:0.00} → {Z1:0.00}mm (차 {D:+0.00;-0.00}mm) — 좌표계를 시작점 둘레로 {T:+0.00;-0.00}° 기울임",
            lengthMm, z0, z1, delta, tiltDeg);
        return StepResult.Ok(
            $"끝 WD 확인 — 용접선 {lengthMm:0}mm 끝 레이저 차 {delta:+0.0;-0.0}mm, 좌표계 {tiltDeg:+0.00;-0.00}° 보정.");
    }

    private async Task ReturnAsync(double[] start, SequenceContext context, CancellationToken ct)
    {
        var rc = await _cobot.Rpc.MoveLAsync(start, tool: context.Tool, user: 0, vel: context.Velocity, ct: ct);
        if (rc != 0)
            throw new InvalidOperationException(
                $"끝 WD 확인 후 시작점 복귀 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
    }

    /// <summary>시작 자세 그대로 좌표계 X 방향으로 <paramref name="lengthMm"/> 옮긴 자세(BASE).</summary>
    internal static double[] EndProbePose(double[] start, double[] framePose, double lengthMm)
    {
        var m = FrameMath.PoseToMatrix(framePose);
        return new[]
        {
            start[0] + lengthMm * m[0, 0], start[1] + lengthMm * m[1, 0], start[2] + lengthMm * m[2, 0],
            start[3], start[4], start[5],
        };
    }

    /// <summary>
    /// 끝점 레이저 거리가 시작점보다 <paramref name="deltaMm"/> 크면(면이 그만큼 멀다) 좌표계 X 를 면 쪽으로 그만큼 내리도록
    /// Y 축 둘레로 기울인 좌표계. 원점은 그대로. 면 바깥 법선은 광축(면을 향함) 반대 쪽의 좌표계 Z 로 본다.
    /// </summary>
    internal static double[] TiltFrameToEnd(double[] framePose, double[] toolPose, ToolAxisDir opticalAxis,
        double lengthMm, double deltaMm, out double tiltDeg)
    {
        var f = FrameMath.PoseToMatrix(framePose);
        double[] x = { f[0, 0], f[1, 0], f[2, 0] };
        double[] y = { f[0, 1], f[1, 1], f[2, 1] };
        double[] z = { f[0, 2], f[1, 2], f[2, 2] };

        var t = FrameMath.PoseToMatrix(toolPose);
        var col = (int)opticalAxis / 2;
        var sgn = (int)opticalAxis % 2 == 0 ? 1.0 : -1.0;
        double[] optical = { sgn * t[0, col], sgn * t[1, col], sgn * t[2, col] };
        var outward = z[0] * optical[0] + z[1] * optical[1] + z[2] * optical[2] < 0 ? z : new[] { -z[0], -z[1], -z[2] };

        double[] nx = { lengthMm * x[0] - deltaMm * outward[0], lengthMm * x[1] - deltaMm * outward[1], lengthMm * x[2] - deltaMm * outward[2] };
        var n = Math.Sqrt(nx[0] * nx[0] + nx[1] * nx[1] + nx[2] * nx[2]);
        for (var i = 0; i < 3; i++) nx[i] /= n;
        // Y 에 남은 X 성분 제거(수치 직교) 후 Z = X × Y
        var dxy = nx[0] * y[0] + nx[1] * y[1] + nx[2] * y[2];
        double[] ny = { y[0] - dxy * nx[0], y[1] - dxy * nx[1], y[2] - dxy * nx[2] };
        var nn = Math.Sqrt(ny[0] * ny[0] + ny[1] * ny[1] + ny[2] * ny[2]);
        for (var i = 0; i < 3; i++) ny[i] /= nn;
        double[] nz = { nx[1] * ny[2] - nx[2] * ny[1], nx[2] * ny[0] - nx[0] * ny[2], nx[0] * ny[1] - nx[1] * ny[0] };

        var m = new double[4, 4];
        for (var i = 0; i < 3; i++)
        {
            m[i, 0] = nx[i]; m[i, 1] = ny[i]; m[i, 2] = nz[i]; m[i, 3] = f[i, 3];
        }
        m[3, 3] = 1;
        tiltDeg = -Math.Atan2(deltaMm, lengthMm) * 180.0 / Math.PI;
        var pose = FrameMath.MatrixToPose(m);
        pose[0] = framePose[0]; pose[1] = framePose[1]; pose[2] = framePose[2];
        return pose;
    }

    private async Task<double?> SampleStableZAsync(CancellationToken ct)
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
                    return window.Average();
            }
            else window.Clear();
            await Task.Delay(PollMs, ct);
        }
        return null;
    }
}
