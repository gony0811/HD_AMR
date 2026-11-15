using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 측면 리치 맵 → ACS 전달용 JSON(측면 정차 검사 사양 §8.2). 순수 계산.
///
/// ACS 는 층+벽면마다 벽과의 옆 거리를 하나로 고정하고 정차(영역)별 리프트 높이로 높이를 맞춘다. 그래서
/// "상대 높이(용접선 z − 스트로크) 구간마다 어떤 옆 거리에서 닿는가" 를 [최소, 권장, 최대] mm 로 준다.
///
/// 판정 규칙:
///  · 옆 거리 d(= |셀 y|)가 그 구간·스트로크에서 <b>유효</b> = 벽 따라(x) 연속으로 닿는 구간이
///    <see cref="MinAlongWindowMm"/>(ACS 의 ±0.2 m 정차 여유) 이상인 것.
///  · 구간의 유효 거리 집합 = 그 구간을 지나는 모든 스트로크(≥ minStrokeMm)의 유효 집합 교집합.
///  · minStrokeMm = 그 이상 모든 스트로크에서 유효 거리가 있는 가장 낮은 스트로크. 가장 낮은 스트로크부터 되면 null.
///  · 권장 = 유효 거리 중 벽 따라 닿는 구간이 가장 넓은 거리(같으면 범위 가운데에 가까운 쪽).
/// </summary>
public static class SideReachExport
{
    /// <summary>유효 판정에 필요한 벽 따라 최소 연속 도달 폭 [mm] — ACS 정차 여유 ±0.2 m.</summary>
    public const double MinAlongWindowMm = 400.0;

    /// <summary>
    /// 측면 리치 맵 결과(좌/우, 여러 벽면 가능)를 JSON 문자열로 만든다.
    /// </summary>
    /// <param name="results">측면(SIDE_LEFT/SIDE_RIGHT) 리치 맵 결과. 정면 결과는 무시한다.</param>
    /// <param name="version">맵 버전 — 재계산할 때마다 올린다.</param>
    /// <param name="strokeRangeMm">ACS 가 쓸 수 있는 스트로크 범위 [최소, 최대] mm.</param>
    public static string Build(IEnumerable<ReachMapResult> results, int version, DateTimeOffset computedAt,
        (double Min, double Max) strokeRangeMm)
        => Build(results.Select(r => (r.Spec.Mode,
                (IReadOnlyList<ExportCell>)r.Cells.Select(c => new ExportCell(c.WallCode, c.StrokeMm, c.Xmm, c.Ymm, c.Zmm, c.Usable)).ToList())),
            version, computedAt, strokeRangeMm);

    /// <summary>저장된 리치 맵 CSV(<see cref="ReachMapCsv"/>)에서 만든다 — 측면 맵 파일만 쓴다.</summary>
    public static string Build(IEnumerable<ReachMapFile> files, int version, DateTimeOffset computedAt,
        (double Min, double Max) strokeRangeMm)
        => Build(files.Select(f => (f.Stamp.Mode,
                (IReadOnlyList<ExportCell>)f.Cells.Select(c => new ExportCell(c.WallCode, c.StrokeMm, c.Xmm, c.Ymm, c.Zmm, c.Usable)).ToList())),
            version, computedAt, strokeRangeMm);

    /// <summary>방식별 셀 목록에서 만든다. 같은 방식이 여러 번 오면 뒤의 것이 이긴다.</summary>
    public static string Build(IEnumerable<(StationMode Mode, IReadOnlyList<ExportCell> Cells)> maps, int version,
        DateTimeOffset computedAt, (double Min, double Max) strokeRangeMm)
    {
        var byWall = new SortedDictionary<string, Dictionary<StationMode, JsonArray>>(StringComparer.Ordinal);
        foreach (var (mode, cells) in maps.Where(m => m.Mode.IsSide()))
        {
            var zStep = Step(cells.Select(c => c.Zmm - c.StrokeMm).ToArray());
            foreach (var wall in cells.Select(c => c.WallCode).Distinct())
            {
                var bands = Bands(cells.Where(c => c.WallCode == wall).ToList(), mode, zStep);
                if (!byWall.TryGetValue(wall, out var modes)) byWall[wall] = modes = new();
                modes[mode] = bands;
            }
        }

        var def = new JsonObject();
        foreach (var (wall, modes) in byWall)
        {
            var w = new JsonObject();
            modes.TryGetValue(StationMode.SideLeft, out var left);
            modes.TryGetValue(StationMode.SideRight, out var right);
            if (left is not null) w["SIDE_LEFT"] = left.DeepClone();
            if (right is not null)
                w["SIDE_RIGHT"] = left is not null && JsonNode.DeepEquals(left, right)
                    ? JsonValue.Create("SAME_AS_SIDE_LEFT")
                    : right.DeepClone();
            def[wall] = w;
        }

        var root = new JsonObject
        {
            ["version"] = version,
            ["computedAt"] = computedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["strokeRangeMm"] = new JsonArray(strokeRangeMm.Min, strokeRangeMm.Max),
            ["relZDefinition"] = "seam z (level floor datum) - telescopic stroke (fully lowered = 0), mm",
            ["lateralDefinition"] = "distance from wall to robot reference point (AMR body origin), mm",
            ["default"] = def,
            ["levelOverrides"] = new JsonObject(),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>한 벽면·한 방식의 상대 높이 구간 목록.</summary>
    internal static JsonArray Bands(IReadOnlyList<ExportCell> cells, StationMode mode, double zStep)
    {
        var sign = mode == StationMode.SideLeft ? 1.0 : -1.0;
        // 벽 쪽 셀만(옆 거리 > 0). 상대 높이 구간 = round((z − h) / zStep).
        var wallSide = cells.Where(c => c.Ymm * sign > 0).ToList();
        var bandWidth = zStep > 0 ? zStep : 1.0;
        var list = new JsonArray();
        foreach (var band in wallSide.GroupBy(c => (int)Math.Round((c.Zmm - c.StrokeMm) / bandWidth)).OrderBy(g => g.Key))
        {
            var center = band.Key * bandWidth;
            var strokes = band.Select(c => c.StrokeMm).Distinct().OrderBy(s => s).ToList();
            // 스트로크별 유효 거리 → 거리별 벽 따라 연속 도달 폭
            var valid = strokes.ToDictionary(s => s, s => ValidDistances(band.Where(c => c.StrokeMm == s)));

            double? minStroke = null;
            for (var i = 0; i < strokes.Count; i++)
                if (strokes.Skip(i).All(s => valid[s].Count > 0)) { minStroke = strokes[i]; break; }

            var item = new JsonObject
            {
                ["relZMm"] = new JsonArray(center - bandWidth / 2, center + bandWidth / 2),
            };
            if (minStroke is null)
            {
                item["lateralMm"] = null;
                item["minStrokeMm"] = null;
                item["note"] = "unreachable at any stroke";
                list.Add(item);
                continue;
            }

            var usedStrokes = strokes.Where(s => s >= minStroke.Value).ToList();
            var common = usedStrokes.Select(s => valid[s].Keys.ToHashSet())
                .Aggregate((a, b) => { a.IntersectWith(b); return a; });
            if (common.Count == 0)
            {
                item["lateralMm"] = null;
                item["minStrokeMm"] = minStroke == strokes[0] ? null : minStroke;
                item["note"] = "no lateral distance common to all strokes";
                list.Add(item);
                continue;
            }

            var sorted = common.OrderBy(d => d).ToList();
            var mid = (sorted[0] + sorted[^1]) / 2;
            var recommended = sorted
                .OrderByDescending(d => usedStrokes.Min(s => valid[s][d]))
                .ThenBy(d => Math.Abs(d - mid))
                .First();
            item["lateralMm"] = new JsonArray(sorted[0], recommended, sorted[^1]);
            item["minStrokeMm"] = minStroke == strokes[0] ? null : minStroke;
            list.Add(item);
        }
        return list;
    }

    /// <summary>한 스트로크·한 높이 구간 셀들에서 옆 거리 → 벽 따라 연속 도달 폭(≥ <see cref="MinAlongWindowMm"/> 인 것만).</summary>
    internal static Dictionary<double, double> ValidDistances(IEnumerable<ExportCell> cells)
    {
        var result = new Dictionary<double, double>();
        foreach (var byLateral in cells.GroupBy(c => Math.Abs(c.Ymm)))
        {
            var xs = byLateral.OrderBy(c => c.Xmm).ToList();
            var xStep = Step(xs.Select(c => c.Xmm).Distinct().ToArray());
            double best = 0, runStart = double.NaN, prev = double.NaN;
            foreach (var c in xs)
            {
                if (!c.Usable) { runStart = double.NaN; continue; }
                if (double.IsNaN(runStart) || double.IsNaN(prev) || c.Xmm - prev > xStep * 1.5) runStart = c.Xmm;
                prev = c.Xmm;
                // 연속 구간 폭 = 끝 − 시작 + 격자 한 칸(셀은 칸의 중심이다)
                best = Math.Max(best, c.Xmm - runStart + xStep);
            }
            if (best >= MinAlongWindowMm) result[byLateral.Key] = best;
        }
        return result;
    }

    private static double Step(double[] values)
    {
        var v = values.Distinct().OrderBy(x => x).ToArray();
        if (v.Length < 2) return 0;
        var step = double.PositiveInfinity;
        for (var i = 1; i < v.Length; i++) step = Math.Min(step, v[i] - v[i - 1]);
        return step;
    }
}

/// <summary>내보내기용 셀 — 리치 맵 결과·CSV 공용.</summary>
public sealed record ExportCell(string WallCode, double StrokeMm, double Xmm, double Ymm, double Zmm, bool Usable);
