using System.Text.Json;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 노드↔Job/Task 인덱스 로컬 매핑 저장소(JSON 파일). EF 마이그레이션 없이 경량 보존.
/// 어댑터(향후 VDA5050 수신부)는 order nodeId를 <see cref="Get"/>로 조회해 TARS-M 이동을 실행한다.
/// 사양: HD_ACS docs/VDA5050_NODE_INDEX_TRANSMISSION.md
/// </summary>
public sealed class AmrJobMappingStore
{
    private readonly string _path;
    private readonly ILogger<AmrJobMappingStore> _logger;
    private readonly object _lock = new();
    private List<AmrNodeMapping> _items = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AmrJobMappingStore(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AmrJobMappingStore>();
        _path = Path.Combine(AppContext.BaseDirectory, "amr_job_mapping.json");
        Load();
    }

    public IReadOnlyList<AmrNodeMapping> All()
    {
        lock (_lock) return _items.OrderBy(m => m.MapId).ThenBy(m => m.NodeId).ToList();
    }

    public AmrNodeMapping? Get(string nodeId)
    {
        lock (_lock) return _items.FirstOrDefault(m => m.NodeId == nodeId);
    }

    /// <summary>nodeId 기준 upsert 후 저장. Source/UpdatedAt은 호출자가 설정한다.</summary>
    public void Upsert(AmrNodeMapping m)
    {
        lock (_lock)
        {
            var i = _items.FindIndex(x => x.NodeId == m.NodeId);
            if (i >= 0) _items[i] = m; else _items.Add(m);
            SaveNoLock();
        }
    }

    public void Remove(string nodeId)
    {
        lock (_lock)
        {
            _items.RemoveAll(x => x.NodeId == nodeId);
            SaveNoLock();
        }
    }

    /// <summary>
    /// ACS 티칭 테이블(좌표+등록 인덱스)을 병합한다. 좌표·이름은 ACS 값으로 갱신하고,
    /// jobIndex는 로컬(LOCAL) 편집분을 보존한다(로컬이 비었고 ACS에 값이 있을 때만 채움).
    /// [정책: NODE_INDEX_TRANSMISSION §5 — 기본 로컬 보존]
    /// </summary>
    public (int Added, int Updated) MergeFromAcs(IEnumerable<AmrNodeMapping> acsRows)
    {
        lock (_lock)
        {
            int added = 0, updated = 0;
            foreach (var a in acsRows)
            {
                var cur = _items.FirstOrDefault(x => x.NodeId == a.NodeId);
                if (cur is null)
                {
                    _items.Add(a);
                    added++;
                }
                else
                {
                    cur.MapId = a.MapId; cur.Name = a.Name;
                    cur.MapX = a.MapX; cur.MapY = a.MapY; cur.ThetaRad = a.ThetaRad;
                    // jobIndex: 로컬(LOCAL) 편집분 보존, 비어 있을 때만 ACS 값 채움.
                    if (cur.Source != "LOCAL" || cur.JobIndex is null)
                    {
                        cur.JobIndex = a.JobIndex;
                        cur.TaskIndex = a.TaskIndex;
                        cur.GotoMode = a.GotoMode;
                        if (cur.Source != "LOCAL") cur.Source = "ACS";
                    }
                    cur.UpdatedAt = DateTime.Now;
                    updated++;
                }
            }
            SaveNoLock();
            return (added, updated);
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _items = JsonSerializer.Deserialize<List<AmrNodeMapping>>(File.ReadAllText(_path), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AMR 매핑 로드 실패({Path}) — 빈 상태로 시작: {Err}", _path, ex.Message);
            _items = new();
        }
    }

    private void SaveNoLock()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogError("AMR 매핑 저장 실패({Path}): {Err}", _path, ex.Message);
        }
    }
}
