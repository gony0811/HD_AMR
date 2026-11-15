using System.Globalization;

namespace HD.AMR.App.Communication.Vda5050;

/// <summary>
/// 층 맵 ID 규칙 — AMR 은 <b>층 번호만</b> 알고, 선창 ID 는 갖고 있지 않는다(VDA 사양서 §4.3).
/// AMR 이 ACS 에 보고하는 맵 ID 는 층만 적은 `L{층}` 이다. 선창 ID 는 ACS 가 정하고 관리한다.
/// ACS 지령의 맵 ID(`{선창}-L{층}` 꼴)는 층 번호만 읽어 비교하고, 선창 이름 부분은 보지 않는다.
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

    /// <summary>층 → AMR 이 보고하는 맵 ID(`L{n}`). 선창 ID 는 붙이지 않는다.</summary>
    public static string Compose(int level) => $"L{level}";

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
