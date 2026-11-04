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

/// <summary>경유점 판정.</summary>
public enum GasJudgement
{
    Ok,
    Ng,
    Error,
}

/// <summary>경유점 1곳의 측정 결과.</summary>
public sealed record GasMeasurementResult(
    int Seq,
    string Label,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    int SampleCount,
    double? MaxMbarLs,
    double? AvgMbarLs,
    double LimitMbarLs,
    GasJudgement Judgement,
    string? Error)
{
    /// <summary>판정 규칙 — 유효 샘플이 하나도 없으면 Error, 최대값이 기준 이상이면 NG.</summary>
    public static GasJudgement Judge(IReadOnlyCollection<double> samplesMbarLs, double limitMbarLs)
        => samplesMbarLs.Count == 0 ? GasJudgement.Error
            : samplesMbarLs.Max() >= limitMbarLs ? GasJudgement.Ng
            : GasJudgement.Ok;
}
