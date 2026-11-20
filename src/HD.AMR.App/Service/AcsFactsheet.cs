using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// ACS 로 보내는 factsheet(VDA 5050 <c>factsheet</c> 토픽, retained)의 HD 확장 <c>hdAmr</c> — 순수 함수.
/// ACS 리치 시뮬레이션·배차가 이 AMR 의 실제 값(면별 ② 출발 관절각 · 리프트 운영 · 장착 보정 T_A_B · 코봇 값)을 쓰게 한다.
/// 사양: HD_ACS docs/VDA5050_INTERFACE_SPEC.md §7.3.
/// </summary>
public static class AcsFactsheet
{
    /// <summary>확장 구조 버전 — 필드 의미가 바뀌면 올린다.</summary>
    public const int SchemaVersion = 1;

    /// <summary>보고 묶음 — 묶음마다 지문을 따로 내고, 지문이 바뀐 묶음 이름을 <c>changed</c> 에 싣는다.</summary>
    public static readonly string[] Sections = { "teaching", "lift", "mount", "cobot" };

    /// <summary>factsheet 에 함께 싣는 티칭 기준 자세(관절각) — 운영 자세 모드의 기준·경유점.</summary>
    public static readonly string[] PoseKeys = { "home", "home15", "home16", "home2", "home3", "home4", "ready" };

    /// <summary>
    /// factsheet 를 만드는 데 필요한 값. TeachingLiftMode = 운영 티칭 세트(SHORT/LONG). TaughtJoints = 티칭된 슬롯 키 → 관절각 6개(미티칭 슬롯은 넣지 않는다, 운영 리프트 모드 세트).
    /// MountPose = 저장된 T_A_B [x,y,z,rx,ry,rz](모두 0 이면 미보정 → null).
    /// </summary>
    public sealed record Inputs(
        string TeachingLiftMode, IReadOnlyDictionary<string, double[]> TaughtJoints,
        bool LiftEnabled, double DisabledStrokeMm, int MinHeightMm, int MaxHeightMm, double MmPerUnit, int HeightOffsetMm,
        double[]? MountPose, double[] LinksMm, double MaxEntryJointTravelDeg);

    /// <summary>
    /// 면별 운영 ② 출발 관절각 — <see cref="SequenceEntry.PrepareAsync"/> 가 실제로 ② 직전에 세우는 자세와 같다:
    /// 상부 면(T/PU/SU)은 그 면 검사 준비 슬롯(미티칭이면 홈 위치_2), 하부 면(B/PL/SL)은 하부 홈(바닥 B 는 홈 위치_4 우선,
    /// 없으면 홈 위치_3)이 티칭돼 있으면 그 면 검사 준비 슬롯(없으면 그 홈), 그 외는 작업 준비 위치(ready). 티칭이 없는 면은 넣지 않는다.
    /// </summary>
    public static Dictionary<string, double[]> StartJointsByWall(IReadOnlyDictionary<string, double[]> taught, IEnumerable<string> wallCodes)
    {
        double[]? J(string key) => taught.TryGetValue(key, out var j) && j is { Length: 6 } ? j : null;
        var result = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in wallCodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var start = SequenceEntry.IsUpperRoute(code)
                ? J(TeachingService.WallSlotKey(code)) ?? J(SequenceEntry.Home2Key)
                : SequenceEntry.IsLowerRoute(code)
                  && ((code == "B" ? J(SequenceEntry.Home4Key) : null) ?? J(SequenceEntry.Home3Key)) is { } lowHome
                    ? J(TeachingService.WallSlotKey(code)) ?? lowHome
                    : J("ready");
            if (start is not null) result[code] = start;
        }
        return result;
    }

    /// <summary>
    /// ACS 리프트 운영 방식으로 옮긴 값 — 리프트 사용 = Auto(높이는 ACS 지령), 미사용이고 서 있는 높이 0 = Off(리프트 없음),
    /// 미사용이고 높이 &gt; 0 = Fixed(그 높이에 고정).
    /// </summary>
    public static (string Mode, double? FixedStrokeMm) LiftMode(bool enabled, double disabledStrokeMm) =>
        enabled ? ("Auto", null)
        : disabledStrokeMm > 0 && double.IsFinite(disabledStrokeMm) ? ("Fixed", disabledStrokeMm)
        : ("Off", null);

    private static JsonArray Arr(IEnumerable<double> v) => new(v.Select(x => (JsonNode?)JsonValue.Create(Math.Round(x, 4))).ToArray());

    /// <summary>묶음별 내용(지문 계산 대상 — 시각·버전은 넣지 않는다).</summary>
    public static Dictionary<string, JsonObject> BuildSections(Inputs i)
    {
        var starts = new JsonObject();
        foreach (var (code, j) in StartJointsByWall(i.TaughtJoints, WallCodes.All.Select(w => w.Code)).OrderBy(k => k.Key, StringComparer.Ordinal))
            starts[code] = Arr(j);
        var poses = new JsonObject();
        foreach (var key in PoseKeys)
            if (i.TaughtJoints.TryGetValue(key, out var j) && j is { Length: 6 }) poses[key] = Arr(j);
        var (mode, fixedMm) = LiftMode(i.LiftEnabled, i.DisabledStrokeMm);
        var mount = i.MountPose is { Length: 6 } m && m.Any(v => Math.Abs(v) > 1e-9) ? Arr(m) : null;
        return new Dictionary<string, JsonObject>
        {
            ["teaching"] = new()
            {
                ["liftMode"] = TeachingLiftMode.Normalize(i.TeachingLiftMode),
                ["startJointsByWall"] = starts,
                ["poses"] = poses,
            },
            ["lift"] = new()
            {
                ["enabled"] = i.LiftEnabled,
                ["mode"] = mode,
                ["fixedStrokeMm"] = fixedMm,
                ["disabledStrokeMm"] = i.DisabledStrokeMm,
                ["minHeightMm"] = i.MinHeightMm,
                ["maxHeightMm"] = i.MaxHeightMm,
                ["mmPerUnit"] = i.MmPerUnit,
                ["heightOffsetMm"] = i.HeightOffsetMm,
            },
            ["mount"] = new() { ["tAB"] = mount },
            ["cobot"] = new()
            {
                ["linksMm"] = Arr(i.LinksMm),
                ["maxEntryJointTravelDeg"] = i.MaxEntryJointTravelDeg,
            },
        };
    }

    /// <summary>묶음 지문 — 정렬 없는 JSON 문자열의 SHA-256 앞 16자(묶음 안 필드 순서는 코드가 고정한다).</summary>
    public static string Fingerprint(JsonNode node) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())))[..16].ToLowerInvariant();

    public static Dictionary<string, string> Fingerprints(Dictionary<string, JsonObject> sections) =>
        sections.ToDictionary(kv => kv.Key, kv => Fingerprint(kv.Value));

    /// <summary>
    /// 설정 버전 진행 — 직전 지문(없으면 처음)과 비교해 바뀐 묶음이 있으면 버전 +1, 바뀐 묶음 목록을 돌려준다.
    /// 처음(직전 지문 없음)은 버전 1, 바뀐 묶음 없음(초기값이지 "변경"이 아니다).
    /// </summary>
    public static (int Version, List<string> Changed) Advance(int prevVersion, IReadOnlyDictionary<string, string>? prev,
        IReadOnlyDictionary<string, string> now)
    {
        if (prev is null || prev.Count == 0 || prevVersion < 1) return (1, new List<string>());
        var changed = Sections.Where(s => !string.Equals(prev.GetValueOrDefault(s), now.GetValueOrDefault(s), StringComparison.Ordinal)).ToList();
        return changed.Count == 0 ? (prevVersion, changed) : (prevVersion + 1, changed);
    }

    /// <summary>
    /// factsheet 본문(헤더 제외 — 어댑터가 headerId·timestamp·version·manufacturer·serialNumber 를 채운다).
    /// 표준 필드는 최소(typeSpecification)만 싣고 HD 값은 <c>hdAmr</c> 확장에 담는다.
    /// </summary>
    public static JsonObject Build(Inputs i, int configVersion, DateTimeOffset changedAt, IReadOnlyList<string> changed)
    {
        var sections = BuildSections(i);
        var fp = new JsonObject();
        foreach (var (k, v) in Fingerprints(sections)) fp[k] = v;
        var hd = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["configVersion"] = configVersion,
            ["changedAt"] = changedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["changed"] = new JsonArray(changed.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["fingerprints"] = fp,
        };
        foreach (var (k, v) in sections) hd[k] = v;
        return new JsonObject
        {
            ["typeSpecification"] = new JsonObject
            {
                ["seriesName"] = "HD_AMR",
                ["seriesDescription"] = "HD현대중공업 LNG 화물창 검사 AMR (현대 AMR + FR5-WML 코봇 + 텔레스코픽 리프트)",
                ["agvKinematic"] = "OMNI",
                ["agvClass"] = "CARRIER",
                ["localizationTypes"] = new JsonArray("NATURAL"),
                ["navigationTypes"] = new JsonArray("AUTONOMOUS"),
            },
            ["hdAmr"] = hd,
        };
    }
}

/// <summary>
/// factsheet 를 만든다 — 티칭(운영 리프트 모드 세트)·장착 보정(T_A_B)은 DB/파라미터에서, 리프트는 Telescopic 설정에서,
/// 코봇 값은 적용 중인 정적 값에서 읽는다. 설정 버전·지문은 파라미터(<see cref="VersionKey"/>·<see cref="FingerprintsKey"/>)에
/// 보관해 앱을 다시 켜도 이어 센다.
/// </summary>
public sealed class AcsFactsheetService
{
    public const string VersionKey = "Acs.Factsheet.ConfigVersion";
    public const string FingerprintsKey = "Acs.Factsheet.Fingerprints";
    /// <summary>지금 버전에서 바뀐 묶음·시각 — 같은 버전을 다시 발행할 때(재접속·요청)도 같은 값을 싣는다.</summary>
    public const string ChangedKey = "Acs.Factsheet.Changed";

    private sealed record ChangeRecord(List<string> Changed, DateTimeOffset At);

    private readonly IServiceScopeFactory _scopes;
    private readonly TelescopicSerialSettings _lift;
    private readonly ILogger<AcsFactsheetService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AcsFactsheetService(IServiceScopeFactory scopes, IOptions<TelescopicSerialSettings> lift, ILogger<AcsFactsheetService> logger)
    {
        _scopes = scopes;
        _lift = lift.Value;
        _logger = logger;
    }

    /// <summary>지금 값으로 factsheet 본문을 만든다. 바뀐 묶음이 있으면 버전을 올려 저장한다.</summary>
    /// <returns>(본문, 설정 버전, 바뀐 묶음 — 이번 호출에서 바뀐 것만)</returns>
    public async Task<(JsonObject Body, int Version, IReadOnlyList<string> Changed)> BuildAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var teaching = sp.GetRequiredService<TeachingService>();
            var param = sp.GetRequiredService<ParameterService>();
            var calib = sp.GetRequiredService<CalibrationService>();

            var mode = await teaching.GetActiveLiftModeAsync();
            var taught = (await teaching.ListAsync(mode, ct))
                .Where(p => p.IsTaught && p.J1.HasValue && p.J2.HasValue && p.J3.HasValue && p.J4.HasValue && p.J5.HasValue && p.J6.HasValue)
                .ToDictionary(p => p.Key, p => new[] { p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value });
            var inputs = new AcsFactsheet.Inputs(
                mode, taught, _lift.Enabled, _lift.DisabledStrokeMm, _lift.MinHeightMm, _lift.MaxHeightMm, _lift.MmPerUnit, _lift.HeightOffsetMm,
                await calib.GetMountAsync(), Motion.Fr5Kinematics.Fr5Wml.LinksMm, CobotInspectionMoveStep.MaxEntryJointTravelDeg);

            var now = AcsFactsheet.Fingerprints(AcsFactsheet.BuildSections(inputs));
            var prevVersion = int.TryParse(await param.GetAsync(VersionKey), out var v) ? v : 0;
            Dictionary<string, string>? prev = null;
            try { prev = JsonSerializer.Deserialize<Dictionary<string, string>>(await param.GetAsync(FingerprintsKey) ?? ""); }
            catch (JsonException) { }
            var (version, changed) = AcsFactsheet.Advance(prevVersion, prev, now);
            ChangeRecord record;
            if (version != prevVersion || prev is null)
            {
                record = new ChangeRecord(changed, DateTimeOffset.UtcNow);
                await param.SetAsync(VersionKey, version.ToString(), "ACS factsheet 설정 버전 — 티칭·리프트·장착 보정·코봇 값이 바뀌면 +1");
                await param.SetAsync(FingerprintsKey, JsonSerializer.Serialize(now), "ACS factsheet 묶음별 지문(마지막 보고)");
                await param.SetAsync(ChangedKey, JsonSerializer.Serialize(record), "ACS factsheet 지금 버전에서 바뀐 묶음·시각");
                if (changed.Count > 0)
                    _logger.LogInformation("ACS factsheet 설정 변경 — v{Version}, 바뀐 묶음 {Changed}", version, string.Join(",", changed));
            }
            else
            {
                try { record = JsonSerializer.Deserialize<ChangeRecord>(await param.GetAsync(ChangedKey) ?? "") ?? new(new(), DateTimeOffset.UtcNow); }
                catch (JsonException) { record = new(new(), DateTimeOffset.UtcNow); }
            }
            return (AcsFactsheet.Build(inputs, version, record.At, record.Changed), version, changed);
        }
        finally { _gate.Release(); }
    }
}
