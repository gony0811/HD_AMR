using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Communication;

/// <summary>
/// TARS-M v3 REST 호출 결과. HTTP 200 이어도 body 의 code 로 성공/실패를 판정한다(공통 envelope).
/// 성공 <c>{code:0, data:{}}</c> / 실패 <c>{code:N, message:""}</c>.
/// <see cref="Data"/> 는 응답 스키마 미확정(벤더 2차 회신 대기)이라 원문 JSON 그대로 노출한다.
/// </summary>
public sealed record AmrRestResult(bool Ok, int Code, string? Message, JsonElement? Data, string? Raw)
{
    public static AmrRestResult Fail(string message) => new(false, -1, message, null, null);
}

/// <summary>
/// TARS-M v3 REST 클라이언트 — VDA5050 어댑터의 이동 실현 경로.
/// 확정 3종만 사용: POST /robot/go(큐 추가·비동기), POST /robot/state(정지), GET /robot/status.
/// ⚠ /robot/go 는 목적지 <b>큐 추가(append)</b>다 — 목적지 교체는 반드시 <see cref="StopAsync"/> 선행
/// (사양서 부록 D-3: 미준수 시 이전 목적지 경유 후 이동하는 "조용한 오검사").
/// ⚠ 운영 중 호출 금지 API(/robot/recover, DELETE /map/*)는 이 클라이언트에 두지 않는다.
/// </summary>
public sealed class AmrRestClient : IDisposable
{
    private readonly AmrRestSettings _s;
    private readonly ILogger<AmrRestClient> _logger;
    private readonly HttpClient _http;

    public AmrRestClient(IOptions<AmrRestSettings> options, ILogger<AmrRestClient> logger)
    {
        _s = options.Value;
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_s.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMilliseconds(_s.TimeoutMs),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>좌표 이동 명령(비동기 — 응답은 "명령 수리"). 검사 정차는 항상 stopFlag=true
    /// (false 면 각도 미보정 → 벽 정면 전제 붕괴, 부록 D-2).</summary>
    public Task<AmrRestResult> GoAsync(double x, double y, double rz, bool stopFlag = true, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, _s.GoPath, new { x, y, rz, stopFlag }, ct);

    /// <summary>주행 정지 — 진행 중 이동 중단. emergencyStop 이행체이자 Order 교체(정지→go)의 선행 단계.</summary>
    public Task<AmrRestResult> StopAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, _s.StatePath, new { state = "stop" }, ct);

    /// <summary>로봇 상태 조회 — schedule(진행)·error(실패) 필드 폴링. 값 해석은 미확정(D-12)이라
    /// 호출측이 <see cref="AmrRestResult.Data"/> 를 유연 파싱한다.</summary>
    public Task<AmrRestResult> GetStatusAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, _s.StatusPath, body: null, ct);

    /// <summary>현재 SLAM 포즈, 맵 좌표로 변환된 라이다 점과 맵 일치율을 조회한다.</summary>
    public Task<AmrRestResult> GetPoseAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, _s.PosePath, body: null, ct, logResponseBody: false);

    /// <summary>이름으로 맵 내용(노드·코스·base64 PNG mapping)을 조회한다.
    /// 활성 맵 이름 조회 API가 확정되지 않아 호출측에서 이름을 제공해야 한다.</summary>
    public Task<AmrRestResult> GetMapContentAsync(string mapName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return Task.FromResult(AmrRestResult.Fail("맵 이름을 입력하세요."));

        var path = $"{_s.MapContentPath.TrimEnd('/')}/{Uri.EscapeDataString(mapName.Trim())}";
        return SendAsync(HttpMethod.Get, path, body: null, ct);
    }

    private async Task<AmrRestResult> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct,
        bool logResponseBody = true)
    {
        var rel = path.TrimStart('/');
        try
        {
            using var req = new HttpRequestMessage(method, rel);
            if (body is not null)
            {
                var json = JsonSerializer.Serialize(body);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                _logger.LogDebug("AMR REST {Method} {Path} 요청: {Body}", method, rel, json);
            }

            using var res = await _http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (logResponseBody)
                _logger.LogDebug("AMR REST {Method} {Path} 응답 HTTP {Status}: {Raw}", method, rel, (int)res.StatusCode, raw);
            else
                _logger.LogDebug("AMR REST {Method} {Path} 응답 HTTP {Status} ({Length} chars)",
                    method, rel, (int)res.StatusCode, raw.Length);

            if (!res.IsSuccessStatusCode)
            {
                var detail = string.IsNullOrWhiteSpace(raw)
                    ? null
                    : raw.Length <= 300 ? raw.Trim() : raw[..300].Trim() + "…";
                var errorMessage = $"HTTP {(int)res.StatusCode} ({res.ReasonPhrase})" +
                                   (detail is null ? "" : $": {detail}");
                return new AmrRestResult(false, (int)res.StatusCode, errorMessage, null, raw);
            }

            // 공통 envelope 파싱 — HTTP 상태코드가 아니라 body 의 code 로 판정한다.
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement.Clone();
            var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
            string? message = root.TryGetProperty("message", out var m) ? m.ToString() : null;
            JsonElement? data = root.TryGetProperty("data", out var d) ? d : root;

            if (code != 0)
                _logger.LogWarning("AMR REST {Path} 실패 code={Code}: {Message}", rel, code, message);
            return new AmrRestResult(code == 0, code, message, data, raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AMR REST {Method} {Path} 오류: {Err}", method, rel, ex.Message);
            return AmrRestResult.Fail(ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
