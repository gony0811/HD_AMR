using System.Globalization;

namespace HD.AMR.App.Communication.Vda5050;

/// <summary>
/// 층 맵 ID(`{선창}-L{층}`) 규칙 — AMR 은 <b>층 번호만</b> 알고 선창 이름은 ACS 가 지령에 실어 보낸 값을 그대로 되돌려 보고한다.
/// ACS 에서 선창 ID 를 바꿔도 AMR 설정을 고칠 필요가 없게 하려는 것이다(VDA 사양서 §4.3).
/// 층 검사는 그대로다 — 지령의 층 번호가 AMR 이 아는 층과 다르면 거부한다. 선창 이름 부분은 비교하지 않는다.
/// </summary>
public static class FloorMap
{
    /// <summary>`{선창}-L{n}` · `L{n}` · `{n}` → 층 번호(1 이상). 읽을 수 없으면 null.</summary>
    public static int? LevelOf(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId)) return null;
        var s = mapId.Trim();
        var i = s.LastIndexOf("-L", StringComparison.OrdinalIgnoreCase);
        var tail = i >= 0 ? s[(i + 2)..] : s.StartsWith('L') || s.StartsWith('l') ? s[1..] : s;
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : null;
    }

    /// <summary>`{선창}-L{n}` 의 선창 이름. 층만 있는 꼴(`L4`·`4`)이거나 읽을 수 없으면 null.</summary>
    public static string? TankOf(string? mapId)
    {
        if (LevelOf(mapId) is null) return null;
        var s = mapId!.Trim();
        var i = s.LastIndexOf("-L", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? s[..i] : null;
    }

    /// <summary>선창 이름 + 층 → 맵 ID. 선창 이름을 아직 모르면 층만(`L{n}`) — ACS 가 층 번호로 맞춘다.</summary>
    public static string Compose(string? tank, int level) =>
        string.IsNullOrWhiteSpace(tank) ? $"L{level}" : $"{tank.Trim()}-L{level}";

    /// <summary>
    /// 맨 아래층(층 번호 1)의 **화면 표기** 번호 — 0(기본) = L0·L1·L2·L3, 1 = L1·L2·L3·L4. 설정 <c>Vda5050:LevelLabelBase</c>.
    /// 화면 표기만 바꾼다 — 층 번호(1부터)·맵 ID(`{선창}-L{n}`)·ACS 와 주고받는 값은 그대로다.
    /// </summary>
    public static int LabelBase { get; set; } = 0;

    /// <summary>층 번호(1부터) → 화면 표기.</summary>
    public static string Label(int level) => "L" + (level - 1 + LabelBase).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 맵 ID → 화면에 보일 글 — `L3 (맵 CT1-L4)`. 표기와 맵 ID 의 숫자가 다를 수 있어 둘 다 보인다(로그·ACS 와 대조).
    /// 표기가 층 번호와 같거나(LabelBase = 1) 층을 읽을 수 없으면 맵 ID 그대로.
    /// </summary>
    public static string Display(string? mapId) =>
        LevelOf(mapId) is int n && LabelBase != 1 ? $"{Label(n)} (맵 {mapId})" : mapId ?? "-";

    /// <summary>두 맵 ID 가 같은 층인가 — 선창 이름은 보지 않는다. 한쪽이라도 층을 읽을 수 없으면 false.</summary>
    public static bool SameLevel(string? a, string? b) =>
        LevelOf(a) is int la && LevelOf(b) is int lb && la == lb;

    /// <summary>
    /// 층 목록 — 설정 <c>Levels</c> 가 있으면 그것, 없으면 옛 설정 <c>AvailableMapIds</c> 에서 층 번호만 뽑는다. 둘 다 없으면 1~4.
    /// 중복 제거·오름차순.
    /// </summary>
    public static IReadOnlyList<int> Levels(IEnumerable<int>? levels, IEnumerable<string>? legacyMapIds)
    {
        var list = (levels ?? []).Where(n => n >= 1).ToList();
        if (list.Count == 0) list = (legacyMapIds ?? []).Select(LevelOf).Where(n => n is not null).Select(n => n!.Value).ToList();
        if (list.Count == 0) list = [1, 2, 3, 4];
        return list.Distinct().OrderBy(n => n).ToArray();
    }
}

/// <summary>
/// ACS 에서 마지막으로 받은 선창 이름을 파일에 적어 두었다가 재시작 뒤에도 쓴다(없으면 설정의 옛 MapId 에서 뽑은 값).
/// 파일: %LOCALAPPDATA%\HD.AMR\acs-tank.txt — 한 줄.
/// </summary>
public sealed class TankNameStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD.AMR", "acs-tank.txt");

    public string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var s = File.ReadAllText(_path).Trim();
            return s.Length == 0 ? null : s;
        }
        catch (Exception) { return null; }   // 못 읽으면 설정값으로 — 선창 이름은 다음 지령에서 다시 받는다
    }

    /// <summary>저장 실패는 예외로 올리지 않는다(false) — 동작에는 지장이 없고 재시작 뒤 옛 이름으로 보고할 뿐이다.</summary>
    public bool Save(string tank)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, tank);
            return true;
        }
        catch (Exception) { return false; }
    }
}
