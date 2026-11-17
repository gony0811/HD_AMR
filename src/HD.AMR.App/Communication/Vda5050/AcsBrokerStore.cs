using System.Text.Json;

namespace HD.AMR.App.Communication.Vda5050;

/// <summary>
/// 화면에서 바꾼 ACS MQTT 브로커 주소(호스트·포트) 보관 — <c>%LOCALAPPDATA%\HD.AMR\acs-broker.json</c>.
/// appsettings.json 의 <c>Vda5050:BrokerHost/BrokerPort</c> 는 배포 때 덮어쓰일 수 있어 따로 둔다.
/// 파일이 있으면 그 값이 설정보다 우선한다(<see cref="ApplyTo"/> — 앱 시작 때 한 번).
/// </summary>
public static class AcsBrokerStore
{
    public const int DefaultPort = 1883;

    /// <summary>보관 파일 경로. 테스트에서 바꿀 수 있다.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD.AMR", "acs-broker.json");

    /// <summary>
    /// 입력 검증 — 통과하면 null, 아니면 사용자에게 보일 사유.
    /// 호스트는 IPv4·IPv6 주소 또는 호스트 이름(공백·주소 형식 문자 없음), 포트는 1~65535.
    /// </summary>
    public static string? Validate(string? host, int port)
    {
        var h = host?.Trim() ?? "";
        if (h.Length == 0) return "브로커 IP 주소를 입력하세요.";
        if (h.Contains("://") || h.Contains('/') || h.Any(char.IsWhiteSpace))
            return "IP 주소 또는 호스트 이름만 입력하세요(예 192.168.100.98).";
        if (Uri.CheckHostName(h) == UriHostNameType.Unknown)
            return $"'{h}' 은(는) 올바른 IP 주소·호스트 이름이 아닙니다.";
        // 숫자와 점으로만 된 값은 IPv4 로 읽혀야 한다(예 192.168.1 · 300.1.1.1 은 거부 — 호스트 이름으로 통과시키지 않는다).
        if (h.All(c => char.IsDigit(c) || c == '.') &&
            !(h.Split('.') is { Length: 4 } parts && parts.All(p => p.Length is >= 1 and <= 3 && int.Parse(p) <= 255)))
            return $"'{h}' 은(는) 올바른 IPv4 주소가 아닙니다(예 192.168.100.98).";
        if (port is < 1 or > 65535) return "포트는 1~65535 사이여야 합니다.";
        return null;
    }

    /// <summary>보관된 주소 — 파일이 없거나 읽을 수 없거나 값이 잘못됐으면 null(설정값을 그대로 쓴다).</summary>
    public static (string Host, int Port)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            var host = root.TryGetProperty("host", out var h) ? h.GetString() : null;
            var port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : DefaultPort;
            return Validate(host, port) is null ? (host!.Trim(), port) : null;
        }
        catch { return null; }
    }

    /// <summary>주소 보관. 실패하면 예외를 던진다(호출 측이 사용자에게 알린다).</summary>
    public static void Save(string host, int port)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new { host = host.Trim(), port }));
    }

    /// <summary>보관된 주소가 있으면 설정에 덮어쓴다 — DI 의 PostConfigure 로 걸어 모든 소비자가 같은 값을 본다.</summary>
    public static void ApplyTo(Vda5050AdapterSettings settings)
    {
        if (Load() is not { } saved) return;
        settings.BrokerHost = saved.Host;
        settings.BrokerPort = saved.Port;
    }
}
