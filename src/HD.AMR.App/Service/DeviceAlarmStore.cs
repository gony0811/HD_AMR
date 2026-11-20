using System.Text.Json;
using System.Text.Json.Serialization;
using HD.AMR.App.Data;

namespace HD.AMR.App.Service;

/// <summary>
/// 장치 HEAVY 알람 영속 — <c>%LOCALAPPDATA%\HD.AMR\device-alarms.json</c>.
/// 알람 상태기는 메모리에만 있어 앱 재시작 때 RESET 없이 사라졌다. 이벤트 기록(OperationLogs DB)은 재시작을
/// 넘어 남으므로, 기록에는 "발생"만 있고 ACS state.errors 에는 없는 불일치가 생겼다. 알람 목록이 바뀔 때마다
/// 여기 저장하고 기동 때 복원한다.
/// </summary>
public sealed class DeviceAlarmStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public DeviceAlarmStore() : this(Path.Combine(SqliteConnectionResolver.AppDataDirectory, "device-alarms.json")) { }

    public DeviceAlarmStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>저장된 알람 — 파일이 없거나 깨졌으면 빈 목록.</summary>
    public IReadOnlyList<DeviceAlarmEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<DeviceAlarmEntry>();
            return JsonSerializer.Deserialize<List<DeviceAlarmEntry>>(File.ReadAllText(FilePath), Json)
                   ?? (IReadOnlyList<DeviceAlarmEntry>)Array.Empty<DeviceAlarmEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return Array.Empty<DeviceAlarmEntry>();
        }
    }

    /// <summary>현재 알람 목록 저장(임시 파일 후 교체 — 쓰는 도중 꺼져도 이전 내용이 남는다). 실패하면 예외.</summary>
    public void Save(IReadOnlyList<DeviceAlarmEntry> entries)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
