using System.Globalization;

namespace HD.AMR.App.Service;

/// <summary>
/// ACS 이동 명령(VDA5050 order 노드 좌표)에 더하는 현장 정차 오프셋 — 맵(월드) 좌표계 기준.
/// X = 선수–선미 방향, Y = 좌현–우현 방향(맵 x·y 축과 같은 방향, 부호도 맵 그대로).
/// 주행 목표·도착 판정·코봇 도달 사전 점검·정차 보정이 모두 오프셋을 더한 좌표를 쓴다. 방향(θ)은 바꾸지 않는다.
/// 2026-11-16 맵과 실제 벽의 차이로 정차 위치를 매번 손으로 옮기던 것을 파라미터로 대신한다.
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

    internal static IEnumerable<(string Name, string Value, string Desc)> SeedDefaults() =>
    [
        (XKey, "0",
            "ACS 이동 명령 정차 위치 보정 X(mm) — 맵 x(선수–선미) 방향으로 목표 좌표에 더한다. + 는 맵 +x 쪽. " +
            $"주행·도착 판정·코봇 도달 사전 점검에 모두 적용. 0 = 끔. ±{MaxAbsMm.ToString("0", CultureInfo.InvariantCulture)} 밖이면 0 으로 본다."),
        (YKey, "0",
            "ACS 이동 명령 정차 위치 보정 Y(mm) — 맵 y(좌현–우현) 방향으로 목표 좌표에 더한다. + 는 맵 +y 쪽. " +
            $"주행·도착 판정·코봇 도달 사전 점검에 모두 적용. 0 = 끔. ±{MaxAbsMm.ToString("0", CultureInfo.InvariantCulture)} 밖이면 0 으로 본다."),
    ];
}
