using System.Net.Http.Headers;
using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// 검사 프로필을 ACS 로 올려 보내는 설정. appsettings.json 의 <c>AcsProfileSync</c> 섹션.
/// ACS 는 받은 경유점으로 정차 위치를 고를 때 순회가 되는지 미리 계산한다 — 지령은 종전대로 VDA 5050 뿐이다.
/// </summary>
public class AcsProfileSyncSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>ACS REST 주소(예 http://192.168.100.98:5199). 비우면 VDA 브로커 주소(<c>Vda5050:BrokerHost</c>)와 <see cref="Port"/> 로 만든다.</summary>
    public string? BaseUrl { get; set; }

    /// <summary><see cref="BaseUrl"/> 을 비웠을 때 쓰는 ACS REST 포트.</summary>
    public int Port { get; set; } = 5199;

    /// <summary>다시 보내는 간격(초). 프로필을 저장·삭제하면 기다리지 않고 바로 보낸다.</summary>
    public int IntervalSec { get; set; } = 300;

    public int TimeoutSec { get; set; } = 10;
}

/// <summary>ACS 로 보내는 프로필 본문(순수 함수) — <c>PUT /api/inspection-profiles/sync</c>.</summary>
public static class AcsProfilePayload
{
    public sealed record Waypoint(double X, double Y, double Z, double Theta, double RzDeg, double RxDeg, int Surface);

    public sealed record Profile(int Id, string Name, string SeamType, bool PoseAbsolute, DateTimeOffset UpdatedAt,
        IReadOnlyList<Waypoint> Waypoints);

    /// <summary>
    /// 지정표 한 줄 — 이 유형(ACS 계약 이름: LINE · CROSS4_L · CROSS4_R · CROSS3_R0 …)·면·진행 방향(H 가로 / V 세로)의 용접선은 이 프로필로 순회한다.
    /// </summary>
    public sealed record Assignment(string SeamType, string WallCode, string Direction, int ProfileId);

    /// <summary>
    /// Full = 목록이 전체다(ACS 는 목록에 없는 프로필을 삭제된 것으로 표시한다). 읽지 못한 프로필이 있으면 false.
    /// Assignments = 유형·면·방향별 프로필 지정표 전체(ACS 는 통째로 바꾼다).
    /// </summary>
    public sealed record Body(string Source, bool Full, IReadOnlyList<Profile> Profiles, IReadOnlyList<Assignment> Assignments,
        RobotModel? Robot = null);

    /// <summary>
    /// 이 AMR 이 쓰는 코봇 모델 값 — ACS 리치 시뮬레이터가 같은 값을 쓰는지 대조하라고 함께 보낸다(ACS 는 다르면 경고만 한다).
    /// LinksMm = 링크 치수 [d1,a2,a3,d4,d5,d6], WristMarginDeg = 경로 손목 여유 |J5|, ToolLengthMm = 플랜지 면 → 검사 초점.
    /// </summary>
    public sealed record RobotModel(double[] LinksMm, double WristMarginDeg, double ToolLengthMm);

    /// <summary>지금 값 — 링크 치수는 파라미터(<see cref="Motion.Fr5Kinematics.LinksKey"/>)가 적용된 값이다.</summary>
    public static RobotModel CurrentRobotModel() => new(
        Motion.Fr5Kinematics.Fr5Wml.LinksMm, Motion.PostureLimits.DefaultWristMarginDeg, Motion.Fr5Kinematics.InspectionTool[2]);

    /// <summary>ACS 응답에서 코봇 모델 값 차이(robotModelMismatch)를 꺼낸다 — 없거나 구버전 ACS 면 빈 목록.</summary>
    public static IReadOnlyList<string> RobotModelMismatch(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("robotModelMismatch", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : Array.Empty<string>();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    /// <summary>ACS 계약의 용접선 유형 이름 ↔ AMR 유형. 코너는 티칭 프로필로 순회하지 않아 지정표에 없다.
    /// 구 이름 "CROSS4"(좌/우 미구분)도 함께 싣는다 — ACS 가 CROSS4_L/CROSS4_R 로 바꾸기 전까지 그 이름으로 찾기 때문이고,
    /// AMR 이 구 "CROSS4" 지령을 실행할 때와 같은 프로필(좌측)이 실린다.</summary>
    private static readonly (string Name, SeamTypeKind Kind)[] SeamTypes =
    {
        ("LINE", SeamTypeKind.Line),
        ("CROSS4_L", SeamTypeKind.Cross4L), ("CROSS4_R", SeamTypeKind.Cross4R), ("CROSS4", SeamTypeKind.Cross),
        ("CROSS3_R0", SeamTypeKind.Cross3R0), ("CROSS3_R90", SeamTypeKind.Cross3R90),
        ("CROSS3_R180", SeamTypeKind.Cross3R180), ("CROSS3_R270", SeamTypeKind.Cross3R270),
    };

    /// <summary>
    /// 지정표 — 실행 때와 같은 함수(<see cref="InspectionRecipeResolver.ResolveRecipeId"/> ·
    /// <see cref="InspectionRecipeResolver.SelectProfileId"/>)로 유형 × 면 × 방향마다 쓸 프로필을 구한다.
    /// 프로필이 지정되지 않은 조합은 싣지 않는다. 레시피 선택 규칙이 바뀌어도 ACS 는 이 표만 본다.
    /// </summary>
    public static List<Assignment> BuildAssignments(IEnumerable<InspectionRecipe> recipes)
    {
        var byId = recipes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var list = new List<Assignment>();
        foreach (var (name, kind) in SeamTypes)
            foreach (var wall in WallCodes.All)
            {
                if (InspectionRecipeResolver.ResolveRecipeId(kind, wall.Code) is not { } recipeId
                    || !byId.TryGetValue(recipeId, out var recipe)) continue;
                foreach (var (dirName, dir) in new[] { ("H", InspectionMoveDirection.Horizontal), ("V", InspectionMoveDirection.Vertical) })
                    if (InspectionRecipeResolver.SelectProfileId(recipe, wall.Code, dir).ProfileId is { } profileId)
                        list.Add(new Assignment(name, wall.Code, dirName, profileId));
            }
        return list;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 프로필 목록 → 본문. 경유점 JSON 을 읽지 못한 프로필은 빼고 <paramref name="skipped"/> 에 이름을 담는다 —
    /// 그때는 Full = false 로 보내 ACS 가 그 프로필을 지워진 것으로 보지 않게 한다.
    /// </summary>
    public static Body Build(string source, IEnumerable<InspectionProfile> profiles, out List<string> skipped,
        IEnumerable<InspectionRecipe>? recipes = null)
    {
        skipped = new List<string>();
        var list = new List<Profile>();
        foreach (var p in profiles.OrderBy(p => p.Id))
        {
            List<InspectionWaypoint>? wps;
            try { wps = JsonSerializer.Deserialize<List<InspectionWaypoint>>(p.WaypointsJson ?? "[]"); }
            catch (JsonException) { wps = null; }
            if (wps is null || string.IsNullOrWhiteSpace(p.Name)) { skipped.Add(string.IsNullOrWhiteSpace(p.Name) ? $"#{p.Id}" : p.Name); continue; }
            list.Add(new Profile(p.Id, p.Name.Trim(), string.IsNullOrWhiteSpace(p.SeamType) ? "LINE" : p.SeamType.Trim(), p.PoseAbsolute,
                new DateTimeOffset(DateTime.SpecifyKind(p.UpdatedAt, DateTimeKind.Utc)),
                wps.Select(w => new Waypoint(w.X, w.Y, w.Z, w.Theta, w.RzDeg, w.RxDeg, w.Surface)).ToList()));
        }
        return new Body(source, skipped.Count == 0, list, BuildAssignments(recipes ?? Array.Empty<InspectionRecipe>()));
    }

    /// <summary>ACS 주소 — 설정한 주소가 있으면 그것, 없으면 VDA 브로커 호스트 + 포트. 둘 다 없으면 null(보내지 않는다).</summary>
    public static Uri? ResolveBaseUri(string? baseUrl, string? brokerHost, int port)
    {
        var raw = string.IsNullOrWhiteSpace(baseUrl)
            ? (string.IsNullOrWhiteSpace(brokerHost) ? null : $"http://{brokerHost.Trim()}:{port}")
            : baseUrl.Trim();
        if (raw is null) return null;
        if (!raw.Contains("://")) raw = "http://" + raw;
        return Uri.TryCreate(raw.TrimEnd('/') + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }
}

/// <summary>
/// 검사 프로필 전체 목록과 유형·면별 지정표를 ACS 로 올려 보낸다 — 앱 시작 후 한 번, 프로필·레시피를 저장·삭제할 때마다, 그리고 일정 간격으로.
/// ACS 가 꺼져 있거나 구버전(주소 없음)이면 경고만 남기고 다음 간격에 다시 시도한다. 검사 동작에는 영향을 주지 않는다.
/// </summary>
public sealed class AcsProfileSyncService : BackgroundService
{
    private readonly AcsProfileSyncSettings _s;
    private readonly Vda5050AdapterSettings _vda;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AcsProfileSyncService> _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly HttpClient _http = new();
    private bool? _lastOk;
    private string? _lastMismatch;

    public AcsProfileSyncService(IOptions<AcsProfileSyncSettings> settings, IOptions<Vda5050AdapterSettings> vda,
        IServiceScopeFactory scopes, ILogger<AcsProfileSyncService> logger)
    {
        _s = settings.Value;
        _vda = vda.Value;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>마지막 전송 결과 — 화면·점검용. null = 아직 보낸 적 없음.</summary>
    public string? LastStatus { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 주소를 따로 주지 않았으면 ACS 연동(VDA)이 켜져 있을 때만 보낸다
        var baseUri = _s.Enabled && (!string.IsNullOrWhiteSpace(_s.BaseUrl) || _vda.Enabled)
            ? AcsProfilePayload.ResolveBaseUri(_s.BaseUrl, _vda.BrokerHost, _s.Port)
            : null;
        if (baseUri is null)
        {
            _logger.LogInformation("검사 프로필 ACS 전송 꺼짐(설정 꺼짐 또는 ACS 주소 없음).");
            return;
        }
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(2, _s.TimeoutSec));
        DrawingService.ProfilesChanged += OnProfilesChanged;
        InspectionRecipeService.RecipesChanged += OnProfilesChanged;   // 레시피의 프로필 지정이 바뀌어도 바로 보낸다
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);   // DB 준비·시드가 끝난 뒤
            while (!stoppingToken.IsCancellationRequested)
            {
                await SyncOnceAsync(stoppingToken);
                await _signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(10, _s.IntervalSec)), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            DrawingService.ProfilesChanged -= OnProfilesChanged;
            InspectionRecipeService.RecipesChanged -= OnProfilesChanged;
        }
    }

    private void OnProfilesChanged()
    {
        try { if (_signal.CurrentCount == 0) _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        try
        {
            List<InspectionProfile> profiles;
            List<InspectionRecipe> recipes;
            using (var scope = _scopes.CreateScope())
            {
                profiles = await scope.ServiceProvider.GetRequiredService<DrawingService>().ListProfilesAsync(ct);
                recipes = await scope.ServiceProvider.GetRequiredService<InspectionRecipeService>().ListAsync(ct);
            }
            var body = AcsProfilePayload.Build(_vda.SerialNumber, profiles, out var skipped, recipes)
                with { Robot = AcsProfilePayload.CurrentRobotModel() };
            if (skipped.Count > 0)
                _logger.LogWarning("검사 프로필 ACS 전송 — 경유점을 읽지 못해 뺀 프로필: {Names}", string.Join(", ", skipped));

            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, AcsProfilePayload.Json);
            using var content = new ByteArrayContent(bytes);   // 길이를 아는 본문(Content-Length)
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var res = await _http.PutAsync("api/inspection-profiles/sync", content, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                Report(false, $"ACS 응답 {(int)res.StatusCode} — {Trim(text)}");
                return;
            }
            // 코봇 모델 값(d6·손목 여유·초점 거리)이 ACS 와 다르면 시뮬레이션과 실제 동작이 어긋난다 — 달라질 때마다 한 번 알린다
            var mismatch = string.Join(" / ", AcsProfilePayload.RobotModelMismatch(text));
            if (mismatch != _lastMismatch)
            {
                _lastMismatch = mismatch;
                if (mismatch.Length > 0) _logger.LogWarning("코봇 모델 값이 ACS 와 다릅니다 — {Mismatch}", mismatch);
                else _logger.LogInformation("코봇 모델 값이 ACS 와 같습니다.");
            }
            var changed = text.Contains("\"added\":0") && text.Contains("\"changed\":0") && text.Contains("\"removed\":0")
                          && !text.Contains("\"assignmentsChanged\":true") ? "" : $" — {Trim(text)}";
            Report(true, $"프로필 {body.Profiles.Count}개·지정 {body.Assignments.Count}줄 전송{changed}", logAlways: changed.Length > 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Report(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>성공↔실패가 바뀔 때와 ACS 쪽 내용이 바뀌었을 때만 로그를 남긴다(5분마다 같은 줄이 쌓이지 않게).</summary>
    private void Report(bool ok, string message, bool logAlways = false)
    {
        LastStatus = $"{DateTime.Now:HH:mm:ss} {(ok ? "성공" : "실패")} — {message}";
        if (_lastOk == ok && !logAlways) return;
        _lastOk = ok;
        if (ok) _logger.LogInformation("검사 프로필 ACS 전송 성공({Url}) — {Message}", _http.BaseAddress, message);
        else _logger.LogWarning("검사 프로필 ACS 전송 실패({Url}) — {Message}. 다음 간격에 다시 시도합니다.", _http.BaseAddress, message);
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public override void Dispose()
    {
        _http.Dispose();
        _signal.Dispose();
        base.Dispose();
    }
}
