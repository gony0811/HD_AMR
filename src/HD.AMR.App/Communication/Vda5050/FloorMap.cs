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
