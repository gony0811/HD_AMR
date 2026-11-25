using System.Globalization;
using System.Text;
using HD.AMR.App.Data;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 레이저 센서 고속 기록 모드 — <see cref="EnabledKey"/> 가 true 면 <see cref="IntervalKey"/>(기본 100ms) 간격으로
/// 3채널 거리·평면 z/틸트·코봇 TCP/관절을 로그 폴더의 별도 CSV(<c>logs/laser-YYYY-MM-DD.csv</c>)에 남긴다.
/// 기본은 시퀀스가 실행 중일 때만 기록한다(<see cref="DuringSequenceOnlyKey"/>). 파라미터는 몇 초마다 다시 읽어
/// 앱을 재시작하지 않고 켜고 끌 수 있다.
/// </summary>
public sealed class LaserDataLogService : BackgroundService
{
    public const string EnabledKey = "Laser.DataLog.Enabled";
    public const string IntervalKey = "Laser.DataLog.IntervalMs";
    public const string DuringSequenceOnlyKey = "Laser.DataLog.DuringSequenceOnly";

    public const int DefaultIntervalMs = 100;
    public const int MinIntervalMs = 20, MaxIntervalMs = 1000;

    /// <summary>파라미터 다시 읽는 주기.</summary>
    private static readonly TimeSpan ConfigRefresh = TimeSpan.FromSeconds(2);
    /// <summary>버퍼를 파일로 밀어내는 주기 — 매 줄 flush 하지 않아 디스크 부담을 줄인다.</summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(1);

    public const string CsvHeader =
        "time,step,acsOrder,ch1,ch1Ok,ch2,ch2Ok,ch3,ch3Ok,planeZ,rx,ry,rawRx,rawRy," +
        "tcpX,tcpY,tcpZ,tcpRx,tcpRy,tcpRz,j1,j2,j3,j4,j5,j6";

    private readonly LaserDisplacementSensorService _laser;
    private readonly CobotService _cobot;
    private readonly SequenceMonitorService _monitor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LaserDataLogService> _logger;
    private readonly string _dir;

    private StreamWriter? _writer;
    private string _writerDate = "";

    public LaserDataLogService(LaserDisplacementSensorService laser, CobotService cobot,
        SequenceMonitorService monitor, IServiceScopeFactory scopeFactory, ILogger<LaserDataLogService> logger)
    {
        _laser = laser;
        _cobot = cobot;
        _monitor = monitor;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _dir = Path.Combine(SqliteConnectionResolver.AppDataDirectory, "logs");
    }

    /// <summary>지금 기록 중인지(설정 켬 + 조건 충족 + 센서 연결).</summary>
    public bool IsRecording { get; private set; }

    /// <summary>기록 파일 경로(오늘). 기록 전이면 null.</summary>
    public string? CurrentFile => _writer is null ? null : FilePathFor(_writerDate);

    public static IEnumerable<(string Name, string Value, string Desc)> SeedDefaults()
    {
        yield return (EnabledKey, "false",
            "레이저 센서 고속 기록 모드(true/false) — 켜면 레이저 3채널 거리·평면 z/틸트·코봇 TCP/관절을 " +
            $"'{IntervalKey}' 간격(기본 100ms)으로 로그 폴더의 별도 파일 laser-YYYY-MM-DD.csv 에 남긴다. 2초 안에 반영(재시작 불필요).");
        yield return (IntervalKey, DefaultIntervalMs.ToString(CultureInfo.InvariantCulture),
            $"레이저 센서 고속 기록 간격(ms). {MinIntervalMs}~{MaxIntervalMs}, 밖이거나 비우면 {DefaultIntervalMs}. " +
            "센서 패킷 주기(LaserDisplacementSensor.RpiMs, 기본 50ms)보다 짧게 해도 새 값은 그 주기로만 바뀐다.");
        yield return (DuringSequenceOnlyKey, "true",
            "레이저 센서 고속 기록을 시퀀스(검사) 실행 중에만 할지(true/false). false 면 켜져 있는 동안 계속 기록한다. 비우면 true.");
    }

    /// <summary>간격 파라미터 → 쓸 값(ms). 비었거나 범위 밖이면 기본값.</summary>
    public static int ResolveIntervalMs(int? value)
        => value is { } v && v >= MinIntervalMs && v <= MaxIntervalMs ? v : DefaultIntervalMs;

    /// <summary>이 순간 기록해야 하는지.</summary>
    public static bool ShouldRecord(bool enabled, bool duringSequenceOnly, bool sequenceRunning, bool laserConnected)
        => enabled && laserConnected && (!duringSequenceOnly || sequenceRunning);

    /// <summary>CSV 한 줄(<see cref="CsvHeader"/> 순서). 값이 없으면 빈 칸.</summary>
    public static string FormatLine(DateTime now, string? step, string? acsOrder,
        IReadOnlyList<LaserChannelReading> readings, PlanePose? plane, PlanePose? rawPlane,
        double[]? tcp, double[]? joints)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        sb.Append(now.ToString("HH:mm:ss.fff", ci)).Append(',')
          .Append(Csv(step)).Append(',').Append(Csv(acsOrder));
        for (var ch = 1; ch <= 3; ch++)
        {
            var r = readings.FirstOrDefault(x => x.Channel == ch);
            sb.Append(',');
            if (r is not null) sb.Append(r.Value.ToString("0.000", ci));
            sb.Append(',');
            if (r is not null) sb.Append(r.Enabled ? '1' : '0');
        }
        AppendPlane(sb, plane, withZ: true);
        AppendPlane(sb, rawPlane, withZ: false);
        AppendArray(sb, tcp, "0.00");
        AppendArray(sb, joints, "0.000");
        return sb.ToString();

        void AppendPlane(StringBuilder b, PlanePose? p, bool withZ)
        {
            var ok = p is { Valid: true };
            if (withZ) b.Append(',').Append(ok ? p!.Z.ToString("0.000", ci) : "");
            b.Append(',').Append(ok ? p!.Rx.ToString("0.0000", ci) : "");
            b.Append(',').Append(ok ? p!.Ry.ToString("0.0000", ci) : "");
        }

        void AppendArray(StringBuilder b, double[]? a, string fmt)
        {
            for (var i = 0; i < 6; i++)
            {
                b.Append(',');
                if (a is { Length: >= 6 } && double.IsFinite(a[i])) b.Append(a[i].ToString(fmt, ci));
            }
        }

        static string Csv(string? s)
            => string.IsNullOrEmpty(s) ? "" : s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private string FilePathFor(string date) => Path.Combine(_dir, $"laser-{date}.csv");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = false;
        var duringSeqOnly = true;
        var intervalMs = DefaultIntervalMs;
        var nextConfig = DateTime.MinValue;
        var lastFlush = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextConfig)
                {
                    (enabled, duringSeqOnly, intervalMs) = await ReadConfigAsync(enabled, duringSeqOnly, intervalMs);
                    nextConfig = DateTime.UtcNow + ConfigRefresh;
                }

                var record = ShouldRecord(enabled, duringSeqOnly, _monitor.RunActive, _laser.IsConnected);
                if (record != IsRecording)
                {
                    IsRecording = record;
                    if (record)
                        _logger.LogInformation("레이저 고속 기록 시작 — {Interval}ms 간격, {File}", intervalMs, FilePathFor(DateTime.Now.ToString("yyyy-MM-dd")));
                    else
                    {
                        _logger.LogInformation("레이저 고속 기록 멈춤 ({Why})",
                            !enabled ? "설정 끔" : !_laser.IsConnected ? "레이저 미연결" : "시퀀스 실행 중 아님");
                        _writer?.Flush();
                        if (!enabled) CloseWriter();
                    }
                }

                if (record)
                {
                    WriteSample();
                    if (DateTime.UtcNow - lastFlush >= FlushEvery)
                    {
                        _writer?.Flush();
                        lastFlush = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "레이저 고속 기록 실패 — 파일을 닫고 다시 시도합니다.");
                CloseWriter();
                IsRecording = false;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            // 기록 중이면 설정 간격, 아니면 설정 반영 대기만.
            var delay = IsRecording ? intervalMs : 500;
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        CloseWriter();
    }

    private async Task<(bool Enabled, bool DuringSeqOnly, int IntervalMs)> ReadConfigAsync(bool enabled, bool duringSeqOnly, int intervalMs)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            return (await param.GetBoolAsync(EnabledKey) ?? false,
                    await param.GetBoolAsync(DuringSequenceOnlyKey) ?? true,
                    ResolveIntervalMs(await param.GetIntAsync(IntervalKey)));
        }
        catch (Exception ex)
        {
            // DB 미준비 등 — 직전 값 유지.
            _logger.LogDebug(ex, "레이저 고속 기록 설정 읽기 실패 — 직전 값 유지");
            return (enabled, duringSeqOnly, intervalMs);
        }
    }

    /// <summary>
    /// 한 줄 기록 — 레이저 최신 스냅샷과 코봇 상태 패킷(TCP 는 활성 툴 기준 BASE 좌표)을 함께 남긴다.
    /// </summary>
    private void WriteSample()
    {
        var now = DateTime.Now;
        var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_writer is null || date != _writerDate) OpenWriter(date);

        var readings = _laser.GetReadings();
        PlanePose? raw = null, corrected = null;
        if (readings.Count >= 3)
        {
            raw = _laser.ComputeRawPlanePose(readings);
            corrected = _laser.ApplyTiltReference(raw);
        }
        var st = _cobot.IsStateConnected ? _cobot.State : null;
        var line = FormatLine(now, _monitor.RunActive ? _monitor.StepKey : null, null,
            readings, corrected, raw, st?.TcpPose, st?.JointPos);
        _writer!.WriteLine(line);
    }

    private void OpenWriter(string date)
    {
        CloseWriter();
        Directory.CreateDirectory(_dir);
        var path = FilePathFor(date);
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        // 다른 프로그램(메모장·scp)이 기록 중에도 읽을 수 있게 공유 읽기 허용.
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
        if (isNew) _writer.WriteLine(CsvHeader);
        _writerDate = date;
    }

    private void CloseWriter()
    {
        try { _writer?.Flush(); _writer?.Dispose(); }
        catch { /* 닫기 실패는 무시 */ }
        _writer = null;
        _writerDate = "";
    }

    public override void Dispose()
    {
        CloseWriter();
        base.Dispose();
    }
}
