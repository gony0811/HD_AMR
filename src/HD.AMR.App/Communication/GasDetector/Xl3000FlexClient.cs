namespace HD.AMR.App.Communication.GasDetector;

/// <summary>가스 디텍터가 오류 응답(<c>Exx</c>)이나 해석할 수 없는 응답을 보냈을 때.</summary>
public sealed class GasDetectorException(string message) : Exception(message);

/// <summary>
/// XL3000flex ASCII 클라이언트. 프로토콜이 엄격한 문답식(응답을 받기 전 다음 명령 금지)이라
/// 모든 교환을 <see cref="SemaphoreSlim"/> 으로 직렬화한다 — 폴링과 경유점 측정이 동시에 불러도 섞이지 않는다.
/// 전송 계층(<see cref="IGasDetectorTransport"/>)은 시리얼/TCP 중 무엇이든 된다.
/// </summary>
public sealed class Xl3000FlexClient(IGasDetectorTransport transport, int responseTimeoutMs) : IDisposable
{
    private readonly SemaphoreSlim _io = new(1, 1);

    public bool IsOpen => transport.IsOpen;

    /// <summary>마지막 송신 명령(진단 표시용).</summary>
    public string? LastTx { get; private set; }

    /// <summary>마지막 수신 응답(진단 표시용). 무응답이면 null.</summary>
    public string? LastRx { get; private set; }

    public Task OpenAsync(CancellationToken ct) => transport.OpenAsync(ct);
    public void Close() => transport.Close();

    /// <summary>명령 1건 교환. 무응답이면 <see cref="TimeoutException"/>, <c>Exx</c> 면 <see cref="GasDetectorException"/>.</summary>
    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LastTx = command;
            var rx = await transport.ExchangeAsync(Xl3000AsciiProtocol.Frame(command), responseTimeoutMs, ct)
                .ConfigureAwait(false);
            LastRx = rx;
            if (rx is null)
                throw new TimeoutException($"응답 없음 ({command}, {responseTimeoutMs} ms)");
            if (Xl3000AsciiProtocol.TryGetError(rx, out var code))
                throw new GasDetectorException($"{command} → E{code:D2} {Xl3000AsciiProtocol.ErrorMeaning(code)}");
            return rx;
        }
        finally { _io.Release(); }
    }

    public async Task<double> ReadNumberAsync(string command, CancellationToken ct = default)
    {
        var rx = await QueryAsync(command, ct).ConfigureAwait(false);
        return Xl3000AsciiProtocol.TryParseNumber(rx, out var v)
            ? v
            : throw new GasDetectorException($"{command} → 숫자 아님: '{rx}'");
    }

    /// <summary>쓰기/동작 명령 — <c>OK</c> 를 기대한다.</summary>
    public async Task CommandAsync(string command, CancellationToken ct = default)
    {
        var rx = await QueryAsync(command, ct).ConfigureAwait(false);
        if (!Xl3000AsciiProtocol.IsOk(rx))
            throw new GasDetectorException($"{command} → 예상 밖 응답: '{rx}'");
    }

    public Task<double> ReadLeakRateAsync(CancellationToken ct = default)
        => ReadNumberAsync(Xl3000AsciiProtocol.ReadLeakRate, ct);

    public Task<double> ReadLeakRateMbarLsAsync(CancellationToken ct = default)
        => ReadNumberAsync(Xl3000AsciiProtocol.ReadLeakRateMbar, ct);

    public void Dispose()
    {
        transport.Dispose();
        _io.Dispose();
    }
}

/// <summary>실시간 상태 스냅샷.</summary>
public sealed record GasDetectorReading(
    double? LeakRate,
    double? LeakRateMbarLs,
    string? Unit,
    Xl3000Mode Mode,
    string? ModeRaw,
    bool[]? Triggers,
    string? ErrorStatus,
    DateTime ReadAtUtc)
{
    public bool HasDeviceError => ErrorStatus is { } e && !Xl3000AsciiProtocol.IsNoError(e);
}

/// <summary>측정 1건(scan)·포인트·Task 판정. Error 는 유효 측정값을 얻지 못한 경우(통신·단위 불일치).</summary>
public enum GasJudgement
{
    Ok,
    Ng,
    Error,
}

/// <summary>
/// 측정 1건(SAIGE <c>scans[]</c> 1개)의 결과 — 대기 후 샘플 N개를 읽어 <b>최대값</b>을 측정값으로 삼는다.
/// 값·임계값 단위는 <see cref="Unit"/>(장비 인터페이스 단위 = 임계값 단위).
/// </summary>
public sealed record GasMeasurementResult(
    int Seq,
    string Label,
    int Retry,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    int SampleCount,
    double? Value,
    double? Avg,
    string Unit,
    double Threshold,
    GasJudgement Judgement,
    string? Error)
{
    /// <summary>측정 시각(SAIGE <c>measuredAt</c>) — 마지막 샘플을 읽은 시각.</summary>
    public DateTime MeasuredUtc => FinishedUtc;

    /// <summary>판정 규칙(SAIGE 규격 §2) — 측정값이 임계값 이하면 OK, 초과면 NG.</summary>
    public static GasJudgement Judge(double value, double threshold)
        => value > threshold ? GasJudgement.Ng : GasJudgement.Ok;

    /// <summary>샘플 묶음 판정 — 유효 샘플이 없으면 Error, 아니면 최대값으로 <see cref="Judge(double,double)"/>.</summary>
    public static GasJudgement Judge(IReadOnlyCollection<double> samples, double threshold)
        => samples.Count == 0 ? GasJudgement.Error : Judge(samples.Max(), threshold);
}
