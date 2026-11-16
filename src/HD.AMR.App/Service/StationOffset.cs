using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.AMR.App.Communication.Vda5050;

namespace HD.AMR.App.Service;

/// <summary>
/// ACS 좌표(VDA5050 order 노드 좌표와 용접선 좌표)에 더하는 현장 오프셋 — 맵(월드) 좌표계 기준.
/// X = 선수–선미 방향, Y = 좌현–우현 방향(맵 x·y 축과 같은 방향, 부호도 맵 그대로).
/// 노드(정차 위치)와 용접선 좌표(<c>position.seamStartW/seamEndW</c>)를 <b>같이</b> 옮긴다 — AMR 도 TCP 도
/// 오프셋만큼 옮겨 간다. 맵상 위치와 실제 용접선이 어긋나 TCP 가 늘 같은 거리만큼 비켜 서서 수동 비드 입력이
/// 뜨던 것(2026-11-16 X −400mm)을 바로잡는 용도. AMR–용접선 상대 위치는 그대로라 코봇 리치는 바뀌지 않는다.
/// 주행·도착 판정·코봇 도달 사전 점검·정차 보정·검사 모두 오프셋을 더한 좌표를 쓴다. 방향(θ)과 높이(z)는 바꾸지 않는다.
/// </summary>
public static class StationOffset
{
    /// <summary>정차 오프셋 X(mm) 파라미터 키 — 맵 x(선수–선미) 방향.</summary>
    public const string XKey = "arrival.offsetXMm";

    /// <summary>정차 오프셋 Y(mm) 파라미터 키 — 맵 y(좌현–우현) 방향.</summary>
    public const string YKey = "arrival.offsetYMm";

    /// <summary>축별 허용 절대값(mm). 넘으면 설정 실수로 보고 그 축은 0 으로 쓴다.</summary>
    public const double MaxAbsMm = 1000.0;

    /// <summary>파라미터 값 정리 — 없음·NaN·범위 밖이면 0.</summary>
    public static double Sanitize(double? mm)
        => mm is { } v && double.IsFinite(v) && Math.Abs(v) <= MaxAbsMm ? v : 0.0;

    /// <summary>노드 좌표(m)에 오프셋(mm)을 더한 좌표(m).</summary>
    public static (double X, double Y) Apply(double x, double y, double offsetXMm, double offsetYMm)
        => (x + offsetXMm / 1000.0, y + offsetYMm / 1000.0);

    /// <summary>액션의 <c>position</c> 파라미터 안 <c>seamStartW</c>·<c>seamEndW</c> 의 x·y(m)에 오프셋(mm)을 더한다(제자리 수정).
    /// position 이 없거나 해석할 수 없으면 손대지 않고 false — 파서가 원래 오류를 그대로 보고한다.</summary>
    public static bool ShiftSeamPosition(VdaAction action, double offsetXMm, double offsetYMm)
    {
        var p = action.ActionParameters.FirstOrDefault(a => a.Key == "position");
        if (p is null || ToNode(p.Value) is not JsonObject position) return false;

        var shifted = false;
        foreach (var key in new[] { "seamStartW", "seamEndW" })
        {
            if (position[key] is not JsonArray { Count: >= 2 } vec) continue;
            if (!TryNumber(vec[0], out var x) || !TryNumber(vec[1], out var y)) continue;
            vec[0] = x + offsetXMm / 1000.0;
            vec[1] = y + offsetYMm / 1000.0;
            shifted = true;
        }
        if (shifted)
            p.Value = JsonSerializer.SerializeToElement(position);
        return shifted;
    }

    private static JsonNode? ToNode(object? value)
    {
        try
        {
            switch (value)
            {
                case null: return null;
                case JsonElement { ValueKind: JsonValueKind.String } s:
                    return s.GetString() is { } json ? JsonNode.Parse(json) : null;   // 문자열에 담긴 JSON(§8.3 폴백)
                case JsonElement el: return JsonNode.Parse(el.GetRawText());
                case string str: return JsonNode.Parse(str);
                default: return JsonSerializer.SerializeToNode(value);
            }
        }
        catch (JsonException) { return null; }
    }

    private static bool TryNumber(JsonNode? node, out double v)
    {
        v = 0;
        return node is JsonValue jv && jv.TryGetValue(out v) && double.IsFinite(v);
    }

    internal static IEnumerable<(string Name, string Value, string Desc)> SeedDefaults() =>
    [
        (XKey, "0",
            "ACS 좌표 보정 X(mm) — 맵 x(선수–선미) 방향. 정차 위치와 용접선 좌표(TCP 가 가는 곳)에 같이 더한다. + 는 맵 +x 쪽. " +
            $"0 = 끔. ±{MaxAbsMm.ToString("0", CultureInfo.InvariantCulture)} 밖이면 0 으로 본다."),
        (YKey, "0",
            "ACS 좌표 보정 Y(mm) — 맵 y(좌현–우현) 방향. 정차 위치와 용접선 좌표(TCP 가 가는 곳)에 같이 더한다. + 는 맵 +y 쪽. " +
            $"0 = 끔. ±{MaxAbsMm.ToString("0", CultureInfo.InvariantCulture)} 밖이면 0 으로 본다."),
    ];
}
