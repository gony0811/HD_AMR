using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 무정지 통과 중 실제 TCP 속도 측정 — 상태 패킷(20004)의 TCP 위치를 시각과 함께 모아 이동 명령별 소요 시간·평균/최고 속도를 로그로 남긴다.
/// 명령을 보낸 시각(블렌딩 명령은 큐에 쌓여 실제 이동과 어긋남)이 아니라 코봇이 실제로 움직인 궤적으로 잰다.
/// 명령 경계는 누적 이동거리를 계획 길이 비율로 맞춰 나눈다(블렌딩으로 모서리가 깎이는 만큼 전체 비율로 보정).
/// 상태 패킷 TCP 는 활성 작업물 좌표계일 수 있지만 거리·속도는 좌표계와 무관하다.
/// </summary>
public sealed class TcpSpeedProbe : IAsyncDisposable
{
    /// <summary>켜기/끄기 파라미터 키 — "0"/"off"/"false" 면 끔, 비우면 가스 켬·비전 끔.</summary>
    public const string EnableKey = "Sequence.Gas.LogPassTcpSpeed";

    internal static bool ResolveEnabled(string? raw, bool usesGas)
        => raw?.Trim().ToLowerInvariant() switch
        {
            null or "" => usesGas,
            "0" or "off" or "false" or "no" => false,
            _ => true,
        };

    public readonly record struct Sample(double TSec, double X, double Y, double Z);

    public readonly record struct CommandSpeed(int Index, double PlannedMm, double MovedMm, double Sec, double AvgMmPerSec, double PeakMmPerSec,
        IReadOnlyList<double>? Profile = null);

    /// <summary>속도 곡선 구간 폭 [s] — 긴 명령의 속도를 이 간격 평균으로 나열한다(가속·정속·감속 모양 확인용).</summary>
    internal const double ProfileBinSec = 0.5;

    public sealed record Report(double TotalMm, double PlannedMm, double Sec, double PeakMmPerSec, IReadOnlyList<CommandSpeed> Commands);

    private const int PollMs = 5;
    private const double StaleSec = 1.0;
    private const double MoveEpsMm = 0.05;
    private const double PeakWindowSec = 0.2;

    private readonly CobotService _cobot;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Sample> _samples = new();
    private readonly Task _loop;
    private DateTime _t0;

    private TcpSpeedProbe(CobotService cobot)
    {
        _cobot = cobot;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>상태 패킷이 들어오고 있으면 측정을 시작한다(아니면 null).</summary>
    public static TcpSpeedProbe? TryStart(CobotService cobot)
        => cobot.State is { } s && (DateTime.UtcNow - s.UpdatedAt).TotalSeconds < StaleSec ? new TcpSpeedProbe(cobot) : null;

    private async Task LoopAsync()
    {
        DateTime last = default;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_cobot.State is { } s && s.UpdatedAt != last && s.TcpPose is { Length: >= 3 } p)
                {
                    last = s.UpdatedAt;
                    if (_t0 == default) _t0 = s.UpdatedAt;
                    lock (_samples) _samples.Add(new Sample((s.UpdatedAt - _t0).TotalSeconds, p[0], p[1], p[2]));
                }
                await Task.Delay(PollMs, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>측정을 멈추고 모은 샘플을 돌려준다.</summary>
    public async Task<IReadOnlyList<Sample>> StopAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { /* 측정 실패는 로그 진단용이라 무시 */ }
        lock (_samples) return _samples.ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) await StopAsync();
        _cts.Dispose();
    }

    private static double Dist(Sample a, Sample b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// 샘플과 계획 명령 길이로 보고서를 만든다. 움직이기 시작한 때부터 마지막으로 움직인 때까지만 본다.
    /// 샘플이 모자라거나 거의 안 움직였으면 null.
    /// </summary>
    internal static Report? Analyze(IReadOnlyList<Sample> samples, IReadOnlyList<double> plannedMm)
    {
        if (samples.Count < 3 || plannedMm.Count == 0) return null;
        // 누적 거리
        var cum = new double[samples.Count];
        for (var i = 1; i < samples.Count; i++) cum[i] = cum[i - 1] + Dist(samples[i - 1], samples[i]);
        var total = cum[^1];
        if (total < 1.0) return null;
        var first = Array.FindIndex(cum, c => c > MoveEpsMm);
        var lastIdx = Array.FindLastIndex(cum, c => c < total - MoveEpsMm) + 1;
        first = Math.Max(0, first - 1);
        lastIdx = Math.Min(samples.Count - 1, Math.Max(lastIdx, first + 1));
        var moved = cum[lastIdx] - cum[first];
        var sec = samples[lastIdx].TSec - samples[first].TSec;

        // 시각 t 부근 PeakWindowSec 창 평균 속도의 최대
        double Peak(int a, int b)
        {
            var best = 0.0;
            var j = a;
            for (var i = a; i <= b; i++)
            {
                while (j < b && samples[j].TSec - samples[i].TSec < PeakWindowSec) j++;
                var dt = samples[j].TSec - samples[i].TSec;
                if (dt >= PeakWindowSec * 0.5) best = Math.Max(best, (cum[j] - cum[i]) / dt);
            }
            return best;
        }

        // [a,b] 구간을 ProfileBinSec 간격으로 잘라 구간 평균 속도
        List<double> Bins(int a, int b)
        {
            var list = new List<double>();
            var i = a;
            while (i < b)
            {
                var j = i;
                while (j < b && samples[j].TSec - samples[i].TSec < ProfileBinSec) j++;
                var dt = samples[j].TSec - samples[i].TSec;
                if (dt > 0) list.Add((cum[j] - cum[i]) / dt);
                if (j == i) break;
                i = j;
            }
            return list;
        }

        // 거리 d 에 도달한 시각의 샘플 인덱스
        int IndexAt(double d)
        {
            var i = Array.BinarySearch(cum, first, lastIdx - first + 1, d);
            if (i < 0) i = ~i;
            return Math.Clamp(i, first, lastIdx);
        }

        var planned = plannedMm.Sum();
        var scale = planned > 0 ? moved / planned : 1;
        var cmds = new List<CommandSpeed>(plannedMm.Count);
        var acc = 0.0;
        var startIdx = first;
        for (var m = 0; m < plannedMm.Count; m++)
        {
            acc += plannedMm[m];
            var endIdx = m == plannedMm.Count - 1 ? lastIdx : IndexAt(cum[first] + acc * scale);
            var mm = cum[endIdx] - cum[startIdx];
            var dt = samples[endIdx].TSec - samples[startIdx].TSec;
            cmds.Add(new CommandSpeed(m + 1, plannedMm[m], mm, dt, dt > 0 ? mm / dt : 0, endIdx > startIdx ? Peak(startIdx, endIdx) : 0,
                Bins(startIdx, endIdx)));
            startIdx = endIdx;
        }
        return new Report(moved, planned, sec, Peak(first, lastIdx), cmds);
    }

    /// <summary>보고서를 로그로 — 전체 요약 1줄 + 긴 명령(기본 30mm 이상) 줄마다.</summary>
    internal static void Log(ILogger logger, string where, Report r, double? targetMmPerSec, double longMm = 30)
    {
        logger.LogInformation(
            "[{Where}] 실측 TCP 속도 — {Mm:0} mm(계획 {Plan:0}) 를 {Sec:0.00} 초에(평균 {Avg:0.0} mm/s, 최고 {Peak:0.0} mm/s{Target}), 명령 {N}개",
            where, r.TotalMm, r.PlannedMm, r.Sec, r.Sec > 0 ? r.TotalMm / r.Sec : 0, r.PeakMmPerSec,
            targetMmPerSec is { } t ? $", 목표 {t:0.#}" : "", r.Commands.Count);
        foreach (var c in r.Commands.Where(c => c.PlannedMm >= longMm))
            logger.LogInformation("[{Where}]   명령 {I}: {Plan:0.0} mm(실측 {Mm:0.0}) {Sec:0.00} 초 — 평균 {Avg:0.0} mm/s, 최고 {Peak:0.0} mm/s, " +
                                  "{Bin}s 간격 속도 [{Profile}]",
                where, c.Index, c.PlannedMm, c.MovedMm, c.Sec, c.AvgMmPerSec, c.PeakMmPerSec, ProfileBinSec,
                string.Join(" ", (c.Profile ?? Array.Empty<double>()).Select(v => v.ToString("0"))));
        var shortCmds = r.Commands.Where(c => c.PlannedMm < longMm).ToList();
        if (shortCmds.Count > 0)
        {
            double mm = shortCmds.Sum(c => c.MovedMm), s = shortCmds.Sum(c => c.Sec);
            logger.LogInformation("[{Where}]   짧은 명령 {N}개(<{L:0}mm, 곡선 등): {Mm:0.0} mm {Sec:0.00} 초 — 평균 {Avg:0.0} mm/s",
                where, shortCmds.Count, longMm, mm, s, s > 0 ? mm / s : 0);
        }
    }
}
