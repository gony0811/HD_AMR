using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD_AMR.Contracts.Lidar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Communication;

/// <summary>
/// Jetson LiDAR 비전 서비스의 HTTP 클라이언트.
///
/// <b>영상은 가져오지 않는다.</b> 설계상 젯슨이 영상 분석과 모니터링 화면을 모두 담당하고,
/// 제어 프로그램은 검출된 능선의 위치 데이터만 받는다. 영상 스트림을 이쪽으로 끌어오면
/// 네트워크 대역과 SignalR 회선을 잡아먹으면서 제어에는 아무것도 보태지 못한다.
///
/// 경로 상수는 <see cref="LidarApiRoutes"/> 를 쓴다. 서버와 같은 상수를 참조하므로 경로
/// 오타는 컴파일 시점에 걸린다.
/// </summary>
public sealed class LidarVisionClient
{
    private readonly HttpClient _http;
    private readonly LidarVisionSettings _settings;
    private readonly ILogger<LidarVisionClient> _log;

    /// <summary>
    /// 서버(ASP.NET Core minimal API)의 직렬화 설정과 맞춘다. 열거형은 계약 쪽 타입에
    /// <c>JsonStringEnumConverter</c> 가 붙어 있어 양쪽이 자동으로 일치한다.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public LidarVisionClient(
        HttpClient http, IOptions<LidarVisionSettings> options, ILogger<LidarVisionClient> log)
    {
        _settings = options.Value;
        _log = log;

        _http = http;
        _http.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");

        // 요청마다 타임아웃이 달라야 하는데(측정은 길고 상태 조회는 짧다) HttpClient.Timeout 은
        // 인스턴스 단위라 바꿀 수 없다. 그래서 인스턴스 타임아웃은 가장 긴 쪽에 맞춰 두고,
        // 짧아야 하는 요청은 CancellationToken 으로 개별 제한한다.
        _http.Timeout = TimeSpan.FromMilliseconds(_settings.EffectiveMeasureHttpTimeoutMs + 1000);
    }

    public LidarVisionSettings Settings => _settings;

    /// <summary>
    /// 1회 측정. 응답의 좌표는 <b>센서 광학 좌표계</b>이며, 로봇 좌표로 옮기려면
    /// <see cref="LidarRidgeTransform"/> 를 거쳐야 한다.
    ///
    /// ⚠ 호출 측은 반드시 <see cref="MeasureResponse.Valid"/> 를 확인할 것. 검출 실패를
    ///   "직전 값 유지"로 숨기면 잘못된 위치로 제어가 나간다.
    /// </summary>
    /// <exception cref="LidarVisionException">통신에 실패했거나 응답을 해석할 수 없을 때.</exception>
    public Task<MeasureResponse> MeasureAsync(
        int? frames = null, bool includeSamples = false, CancellationToken ct = default)
    {
        var request = new MeasureRequest
        {
            Frames = frames ?? _settings.MeasureFrames,
            TimeoutMs = _settings.MeasureTimeoutMs,
            IncludeSamples = includeSamples,
        };

        return SendAsync(
            "측정",
            _settings.EffectiveMeasureHttpTimeoutMs,
            async token =>
            {
                var response = await _http
                    .PostAsJsonAsync(LidarApiRoutes.Measure, request, Json, token)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                return await response.Content
                    .ReadFromJsonAsync<MeasureResponse>(Json, token)
                    .ConfigureAwait(false);
            },
            ct);
    }

    /// <summary>서비스/센서 상태 조회.</summary>
    /// <exception cref="LidarVisionException">통신에 실패했거나 응답을 해석할 수 없을 때.</exception>
    public Task<LidarStatus> GetStatusAsync(CancellationToken ct = default) =>
        SendAsync(
            "상태 조회",
            _settings.RequestTimeoutMs,
            token => _http.GetFromJsonAsync<LidarStatus>(LidarApiRoutes.Status, Json, token),
            ct);

    /// <summary>
    /// 서비스 프로세스가 살아 있는지만 확인한다. <b>센서 연결 여부는 알려주지 않는다</b> —
    /// 그건 <see cref="GetStatusAsync"/> 의 <see cref="LidarStatus.Connected"/> 다.
    ///
    /// 화면 폴링용이라 예외를 던지지 않는다. 연결이 끊긴 동안 매 주기마다 예외가 올라오면
    /// 로그가 뒤덮이고 UI 코드가 try/catch 로 도배된다.
    /// </summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_settings.RequestTimeoutMs);

            var response = await _http.GetAsync(LidarApiRoutes.Health, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// 공통 요청 처리 — 개별 타임아웃, 재시도, 오류 변환.
    ///
    /// 재시도는 <b>통신 실패</b>에만 적용한다. 정지 측정이라 같은 것을 다시 재는 것이므로
    /// 안전하다. 반면 응답이 정상적으로 왔는데 <c>valid=false</c> 인 경우는 재시도하지 않고
    /// 그대로 돌려준다 — 그것은 오류가 아니라 답이고, 재시도로 덮으면 검출이 안 되는 상황을
    /// 조용히 반복 측정으로 감추게 된다.
    /// </summary>
    private async Task<T> SendAsync<T>(
        string what, int timeoutMs, Func<CancellationToken, Task<T?>> send, CancellationToken ct)
        where T : class
    {
        var attempts = Math.Max(0, _settings.RetryCount) + 1;
        Exception? last = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            try
            {
                var result = await send(timeout.Token).ConfigureAwait(false);
                if (result is null)
                    throw new LidarVisionException($"{what} 응답 본문이 비어 있다.");

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 호출 측 취소는 오류가 아니다. 재시도하지 않고 그대로 올린다.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                last = ex;

                // 타임아웃은 OperationCanceledException 으로 오는데, 위에서 호출 측 취소를
                // 걸러냈으므로 여기 도달한 것은 우리가 건 제한 시간이다.
                var reason = ex is OperationCanceledException ? $"{timeoutMs}ms 안에 응답 없음" : ex.Message;
                _log.LogWarning("{What} 실패 ({Attempt}/{Attempts}): {Reason}", what, attempt, attempts, reason);

                if (attempt < attempts && _settings.RetryDelayMs > 0)
                    await Task.Delay(_settings.RetryDelayMs, ct).ConfigureAwait(false);
            }
        }

        throw new LidarVisionException(
            $"{what} 실패 — {attempts}회 시도했으나 {_settings.BaseUrl} 에 도달하지 못했다. " +
            "젯슨 서비스 기동 상태와 네트워크를 확인할 것.", last);
    }
}

/// <summary>LiDAR 비전 서비스와의 통신 실패. 검출 실패(<c>valid=false</c>)와는 다른 사건이다.</summary>
public sealed class LidarVisionException : Exception
{
    public LidarVisionException(string message, Exception? inner = null) : base(message, inner) { }
}
