using System.Globalization;
using System.Text.RegularExpressions;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// <see cref="ReachMapResult.ToCsv"/> 가 남긴 리치 맵을 다시 읽는다. 사전 점검은 최신 파일 중
/// 지금 장비(장착·공구 길이·이격·최소 뻗음·손목 여유)와 같은 헤더의 셀만 신뢰한다.
/// </summary>
public static class ReachMapCsv
{
    /// <summary>셀 좌표가 이 거리(mm) 안이면 같은 점으로 본다. 격자 간격보다 좁다 — 빈 칸은 실시간 계산으로 넘긴다.</summary>
    public const double CellToleranceMm = 25;

    /// <summary>스트로크는 지령 높이 그대로라 1mm 안만 같은 셀로 본다.</summary>
    public const double StrokeToleranceMm = 1;

    private static readonly Regex HeaderRegex = new(
        @"approachStandoff=(?<a>-?[\d.]+)mm\s+alignStandoff=(?<l>-?[\d.]+)mm\s+focusStandoff=(?<f>-?[\d.]+)mm\s+" +
        @"tool=(?<t>-?[\d.]+)mm\s+minFlange=(?<m>-?[\d.]+)mm\s+wrist=(?<w>-?[\d.]+)deg\s+mount=\[(?<mount>[^\]]*)\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>CSV 한 장의 파싱 결과. 헤더를 못 읽으면 null.</summary>
    public static ReachMapFile? Parse(string csv)
    {
        var lines = csv.Split('\n');
        ReachMapStamp? stamp = null;
        var cells = new List<StoredReachCell>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                stamp ??= TryParseStamp(line);
                continue;
            }
            if (line.StartsWith("wall_code", StringComparison.Ordinal)) continue;
            if (TryParseCell(line) is { } cell) cells.Add(cell);
        }
        return stamp is null ? null : new ReachMapFile(stamp, cells);
    }

    /// <summary>
    /// <paramref name="directory"/> 의 reachmap*.csv 를 최신부터 보며, 스탬프가 맞고 점이 들어 있는 첫 셀을 돌려준다.
    /// 폴더가 없거나 맞는 셀이 없으면 null — 호출측이 그 점을 새로 계산한다.
    /// </summary>
    public static StoredReachCell? FindLatest(string directory, ReachMapStamp stamp, string wall,
        double strokeMm, double xmm, double ymm, double zmm)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "reachmap*.csv")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            ReachMapFile? file;
            try { file = Parse(File.ReadAllText(path)); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (file is null || !StampMatches(file.Stamp, stamp)) continue;
            if (Find(file, wall, strokeMm, xmm, ymm, zmm) is { } hit) return hit;
        }
        return null;
    }

    public static StoredReachCell? Find(ReachMapFile file, string wall, double strokeMm, double xmm, double ymm, double zmm)
    {
        StoredReachCell? best = null;
        var bestD = double.PositiveInfinity;
        foreach (var c in file.Cells)
        {
            if (!string.Equals(c.WallCode, wall, StringComparison.OrdinalIgnoreCase)) continue;
            if (Math.Abs(c.StrokeMm - strokeMm) > StrokeToleranceMm) continue;
            var dx = c.Xmm - xmm;
            var dy = c.Ymm - ymm;
            var dz = c.Zmm - zmm;
            if (Math.Abs(dx) > CellToleranceMm || Math.Abs(dy) > CellToleranceMm || Math.Abs(dz) > CellToleranceMm)
                continue;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>헤더의 장비 조건이 지금 판정에 쓸 조건과 같은가. 다르면 그 파일은 쓰지 않는다.</summary>
    public static bool StampMatches(ReachMapStamp file, ReachMapStamp current)
    {
        if (file.Mount.Length != 6 || current.Mount.Length != 6) return false;
        if (Math.Abs(file.ApproachMm - current.ApproachMm) > 0.6) return false;
        if (Math.Abs(file.AlignMm - current.AlignMm) > 0.6) return false;
        if (Math.Abs(file.FocusMm - current.FocusMm) > 0.6) return false;
        if (Math.Abs(file.ToolMm - current.ToolMm) > 0.6) return false;
        if (Math.Abs(file.MinFlangeMm - current.MinFlangeMm) > 0.6) return false;
        if (Math.Abs(file.WristDeg - current.WristDeg) > 0.2) return false;
        for (var i = 0; i < 6; i++)
            if (Math.Abs(file.Mount[i] - current.Mount[i]) > 0.6) return false;
        return true;
    }

    private static ReachMapStamp? TryParseStamp(string line)
    {
        var m = HeaderRegex.Match(line);
        if (!m.Success) return null;
        if (!TryD(m, "a", out var approach) || !TryD(m, "l", out var align) || !TryD(m, "f", out var focus)
            || !TryD(m, "t", out var tool) || !TryD(m, "m", out var flange) || !TryD(m, "w", out var wrist))
            return null;
        var parts = m.Groups["mount"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) return null;
        var mount = new double[6];
        for (var i = 0; i < 6; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out mount[i]))
                return null;
        return new ReachMapStamp(approach, align, focus, tool, flange, wrist, mount);
    }

    private static bool TryD(Match m, string name, out double value)
        => double.TryParse(m.Groups[name].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static StoredReachCell? TryParseCell(string line)
    {
        var quote = line.IndexOf('"');
        var head = quote >= 0 ? line[..quote].TrimEnd(',') : line;
        var reason = "";
        if (quote >= 0)
        {
            var end = line.LastIndexOf('"');
            if (end > quote) reason = line[(quote + 1)..end];
        }
        var f = head.Split(',');
        if (f.Length < 7) return null;
        if (!Enum.TryParse<ReachStatus>(f[5], out var status)) return null;
        if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var stroke)
            || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return null;
        var usable = f[6] == "1";
        return new StoredReachCell(f[0], stroke, x, y, z, status, usable, reason);
    }
}

/// <summary>리치 맵 CSV 헤더 — 이 값들이 같아야 저장된 셀을 지금 장비의 판정으로 쓴다.</summary>
public sealed record ReachMapStamp(
    double ApproachMm, double AlignMm, double FocusMm,
    double ToolMm, double MinFlangeMm, double WristDeg,
    double[] Mount);

public sealed record ReachMapFile(ReachMapStamp Stamp, IReadOnlyList<StoredReachCell> Cells);

/// <summary>CSV 에서 꺼낸 셀. 관절각은 사전 점검이 쓰지 않는다.</summary>
public sealed record StoredReachCell(
    string WallCode, double StrokeMm, double Xmm, double Ymm, double Zmm,
    ReachStatus Status, bool Usable, string Reason);
