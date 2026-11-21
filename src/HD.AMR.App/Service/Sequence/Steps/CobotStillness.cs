using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 레이저를 재기 전 코봇이 실제로 멈췄는지 상태 패킷(20004)의 관절각으로 확인한다.
/// 이동 RPC 가 rc=0 으로 돌아온 뒤에도 감속이 남아 있으면(가속도 배율이 낮을수록 길다) 레이저 값이 계속 변해 안정화 판정이
/// 8초 제한까지 실패한다(2026-11-22 M2 01:16 — 가속도 20→10% 직후 세로 피치 측정 4번 연속 8초 초과).
/// </summary>
public static class CobotStillness
{
    /// <summary>
    /// 정지 판정 창 파라미터 키 [ms] — 관절각이 이 시간 동안 <see cref="TolDeg"/> 안에 머물면 정지로 본다. 0 = 끔(확인 없이 바로 측정).
    /// 비우면 검사 센서 모드로 정한다 — 가스 <see cref="DefaultWindowMs"/>, 비전(용접검사기) 끔.
    /// </summary>
    public const string WindowKey = "Sequence.Laser.WaitCobotStillMs";
    internal const int DefaultWindowMs = 300;
    internal const int MaxWindowMs = 3000;

    /// <summary>정지로 볼 관절각 변화 한계 [도] — 창 안의 축별 최대−최소.</summary>
    internal const double TolDeg = 0.02;

    /// <summary>정지 대기 한도 [ms] — 넘으면 경고만 남기고 측정을 진행한다(종전 동작).</summary>
    internal const int TimeoutMs = 6000;

    /// <summary>상태 패킷이 이보다 오래되면 정지 확인을 할 수 없다고 본다 [ms].</summary>
    internal const int StaleMs = 1000;

    private const int PollMs = 30;

    /// <summary>파라미터 값 → 정지 판정 창 [ms]. 비거나 범위 밖이면 센서 모드 기본값(가스 300 / 비전 0).</summary>
    internal static int ResolveWindowMs(double? value, bool usesGas)
        => value is { } v && double.IsFinite(v) && v >= 0 && v <= MaxWindowMs ? (int)Math.Round(v)
         : usesGas ? DefaultWindowMs : 0;

    /// <summary>
    /// 창(<paramref name="windowMs"/>) 안의 샘플들이 모두 <paramref name="tolDeg"/> 안에 머무는지 — 가장 오래된 샘플이 창 길이만큼
    /// 지나야 판정한다(샘플이 창을 다 채우지 못하면 false).
    /// </summary>
    internal static bool IsStill(IReadOnlyList<(double TMs, double[] Joints)> samples, double windowMs, double tolDeg)
    {
        if (samples.Count < 2) return false;
        var last = samples[^1].TMs;
        var firstIdx = -1;
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            if (last - samples[i].TMs >= windowMs) { firstIdx = i; break; }
        }
        if (firstIdx < 0) return false;
        var n = samples[^1].Joints.Length;
        for (var j = 0; j < n; j++)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (var i = firstIdx; i < samples.Count; i++)
            {
                var v = samples[i].Joints[j];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (hi - lo > tolDeg) return false;
        }
        return true;
    }

    /// <summary>
    /// 코봇이 멈출 때까지 기다린다. 끔·상태 패킷 없음이면 바로 돌아오고, 한도를 넘으면 경고 후 진행한다.
    /// 오래 기다렸을 때만 로그를 남긴다.
    /// </summary>
    public static async Task WaitAsync(CobotService cobot, int windowMs, ILogger logger, string where, CancellationToken ct)
    {
        if (windowMs <= 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var samples = new List<(double TMs, double[] Joints)>();
        DateTime lastStamp = default;
        while (sw.ElapsedMilliseconds < TimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (cobot.State is not { } s || (DateTime.UtcNow - s.UpdatedAt).TotalMilliseconds > StaleMs)
            {
                if (samples.Count == 0)
                {
                    logger.LogDebug("[{Where}] 코봇 상태 패킷 없음 — 정지 확인 생략", where);
                    return;
                }
            }
            else if (s.UpdatedAt != lastStamp)
            {
                lastStamp = s.UpdatedAt;
                samples.Add((sw.Elapsed.TotalMilliseconds, (double[])s.JointPos.Clone()));
                if (samples.Count > 400) samples.RemoveRange(0, 200);
                if (IsStill(samples, windowMs, TolDeg))
                {
                    var waited = sw.ElapsedMilliseconds - windowMs;
                    if (waited > 200)
                        logger.LogInformation("[{Where}] 코봇 정지 확인 — 이동이 {Ms}ms 더 이어진 뒤 멈춤 ('{Key}')",
                            where, waited, WindowKey);
                    return;
                }
            }
            await Task.Delay(PollMs, ct);
        }
        logger.LogWarning("[{Where}] 코봇 정지 확인 {Sec:0.#}s 초과 — 관절이 계속 움직임(진동·감속) — 그대로 측정",
            where, TimeoutMs / 1000.0);
    }
}
