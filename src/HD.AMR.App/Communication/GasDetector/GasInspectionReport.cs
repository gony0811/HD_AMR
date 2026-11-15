using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD.AMR.App.Communication.GasDetector;

/// <summary>판정 임계값이 어디서 왔는지.</summary>
public enum GasThresholdSource
{
    /// <summary>appsettings / 화면 설정(DB) 값.</summary>
    Local,
    /// <summary>SAIGE → ACS → AMR (<c>setGasThreshold</c> instantAction).</summary>
    Acs,
}

/// <summary>
/// 판정 임계값 1건. Task 를 시작할 때 한 번 잡아 끝까지 쓴다 — 검사 도중 SAIGE 가 값을 바꾸면
/// 측정 중인 Task 는 이전 값으로 끝내고 다음 Task 부터 새 값을 쓴다(SAIGE 규격 §3).
/// </summary>
public sealed record GasThreshold(
    double Value,
    string Unit,
    GasThresholdSource Source,
    DateTime AppliedUtc,
    string? TankId = null,
    string? RequestedBy = null,
    long? RequestedAtMs = null)
{
    public string Describe() => $"{Value:0.###} {Unit}" +
        (Source == GasThresholdSource.Acs ? $" (SAIGE{(RequestedBy is { } by ? $" {by}" : "")}{(TankId is { } t ? $", {t}" : "")})" : " (로컬 설정)");
}

/// <summary>경유점 1곳(SAIGE <c>points[]</c> 1개) — 최초 측정 + NG 재측정까지의 scan 들.</summary>
public sealed record GasPointResult(int PointSeq, int? U, int? V, IReadOnlyList<GasMeasurementResult> Scans)
{
    /// <summary>
    /// 재측정까지 반영한 포인트 판정 — 마지막 유효 측정의 판정(재측정에서 OK 가 나오면 OK).
    /// 유효 측정이 하나도 없으면 Error.
    /// </summary>
    public GasJudgement Result => Scans.LastOrDefault(s => s.Judgement != GasJudgement.Error)?.Judgement
                                  ?? GasJudgement.Error;

    /// <summary>NG 재측정 규칙 — 최초 측정 후, 판정이 OK 가 아니면 <paramref name="maxRetries"/> 회까지 같은 자리에서 다시 잰다.</summary>
    public static bool NeedsRetry(GasJudgement last, int retriesDone, int maxRetries)
        => last != GasJudgement.Ok && retriesDone < maxRetries;
}

/// <summary>검사 Task 1개(용접선 1구간, SAIGE 결과 전송 1건)의 가스 측정 결과.</summary>
public sealed record GasTaskResult(
    DateTime StartedUtc,
    DateTime FinishedUtc,
    GasThreshold Threshold,
    IReadOnlyList<GasPointResult> Points)
{
    /// <summary>실행 컨텍스트 Bag 키 — ⑱ 검사 수행이 남기고 오케스트레이터가 ACS 결과로 싣는다.</summary>
    public const string BagKey = "gas.taskResult";

    /// <summary>Task 판정 — 포인트 중 하나라도 NG 면 NG, NG 없이 측정 실패 포인트가 있으면 Error, 아니면 OK.</summary>
    public GasJudgement Result => Points.Any(p => p.Result == GasJudgement.Ng) ? GasJudgement.Ng
        : Points.Count == 0 || Points.Any(p => p.Result == GasJudgement.Error) ? GasJudgement.Error
        : GasJudgement.Ok;
}

/// <summary>
/// SAIGE 연동 규격 v0.1 §2 "가스 측정 결과 전송" 본문(<c>POST /admin/v3/external/gas/results</c>).
/// AMR 은 이 JSON 을 VDA5050 <c>startWeldInspection</c> actionState 의 <c>resultDescription</c> 에 실어 ACS 로 보내고,
/// ACS 는 그대로(또는 필드를 보완해) SAIGE 로 올린다 — ACS 쪽 전달 방식은 이노로보틱스와 협의 대상.
/// </summary>
public sealed class SaigeGasResultReport
{
    [JsonPropertyName("tankId")] public string TankId { get; set; } = "";
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("round")] public int? Round { get; set; }
    [JsonPropertyName("robotId")] public string? RobotId { get; set; }
    [JsonPropertyName("startedAt")] public long StartedAt { get; set; }
    [JsonPropertyName("finishedAt")] public long FinishedAt { get; set; }
    [JsonPropertyName("threshold")] public SaigeThreshold Threshold { get; set; } = new();
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("points")] public List<SaigePoint> Points { get; set; } = new();

    public sealed class SaigeThreshold
    {
        [JsonPropertyName("value")] public double Value { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    }

    public sealed class SaigePoint
    {
        [JsonPropertyName("pointSeq")] public int PointSeq { get; set; }
        [JsonPropertyName("result")] public string Result { get; set; } = "";
        [JsonPropertyName("scans")] public List<SaigeScan> Scans { get; set; } = new();
    }

    public sealed class SaigeScan
    {
        [JsonPropertyName("retry")] public int Retry { get; set; }
        [JsonPropertyName("u")] public int? U { get; set; }
        [JsonPropertyName("v")] public int? V { get; set; }
        [JsonPropertyName("value")] public double Value { get; set; }
        [JsonPropertyName("result")] public string Result { get; set; } = "";
        [JsonPropertyName("measuredAt")] public long MeasuredAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ResultText(GasJudgement j) => j switch
    {
        GasJudgement.Ok => "OK",
        GasJudgement.Ng => "NG",
        _ => "ERROR",
    };

    private static long EpochMs(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>
    /// Task 결과 → SAIGE 본문. 유효 측정값이 없는 scan(통신 실패)은 <c>value</c> 가 필수라 싣지 않는다 —
    /// 그런 포인트만 남으면 포인트·Task 판정이 <c>ERROR</c> 가 된다(SAIGE 규격에 없는 값, 협의 대상).
    /// </summary>
    public static SaigeGasResultReport Build(GasTaskResult task, string tankId, Guid? taskId, int? round, string? robotId)
        => new()
        {
            TankId = tankId,
            TaskId = taskId is { } id && id != Guid.Empty ? id.ToString() : "",
            Round = round,
            RobotId = robotId,
            StartedAt = EpochMs(task.StartedUtc),
            FinishedAt = EpochMs(task.FinishedUtc),
            Threshold = new SaigeThreshold { Value = task.Threshold.Value, Unit = task.Threshold.Unit },
            Result = ResultText(task.Result),
            Points = task.Points.OrderBy(p => p.PointSeq).Select(p => new SaigePoint
            {
                PointSeq = p.PointSeq,
                Result = ResultText(p.Result),
                Scans = p.Scans.Where(s => s.Value is not null).Select(s => new SaigeScan
                {
                    Retry = s.Retry,
                    U = p.U,
                    V = p.V,
                    Value = s.Value!.Value,
                    Result = ResultText(s.Judgement),
                    MeasuredAt = EpochMs(s.MeasuredUtc),
                }).ToList(),
            }).ToList(),
        };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
