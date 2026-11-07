using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 검출된 비드 중심선을 정답 선 라벨(<see cref="WeldLineLabel"/>)과 비교하는 오차 지표. 순수 함수.
/// <list type="bullet">
/// <item><b>각도 오차</b>(°): 두 직선(TLS 피팅) 방향 차이. 0~90.</item>
/// <item><b>중심 오차</b>(px): 진행축 FOV 중심 위치에서 두 직선의 cross-axis 거리 — 시퀀스가 실제로 쓰는 d 와 같은 위치.</item>
/// <item><b>평균 거리</b>(px): 검출 중심선 점들에서 정답 폴리라인까지 수직거리 평균(국소 이탈 감지).</item>
/// <item><b>커버리지</b>: 정답 선의 진행축 구간 중 검출 점이 덮는 비율.</item>
/// </list>
/// </summary>
public static class WeldLineMetrics
{
    /// <summary>합격 기준 기본값: 각도 2° 이하, 중심 오차 10px 이하.</summary>
    public const double DefaultAngleTolDeg = 2.0;
    public const double DefaultCenterTolPx = 10.0;

    /// <summary>점 집합의 TLS(주성분) 직선: 중심 (Cx,Cy) 와 단위 방향 (Dx,Dy). 점이 2개 미만이면 null.</summary>
    public static (double Cx, double Cy, double Dx, double Dy)? FitLine(IReadOnlyList<(double X, double Y)> pts)
    {
        if (pts.Count < 2) return null;
        double cx = 0, cy = 0;
        foreach (var (x, y) in pts) { cx += x; cy += y; }
        cx /= pts.Count; cy /= pts.Count;
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var (x, y) in pts)
        {
            double dx = x - cx, dy = y - cy;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        if (sxx + syy <= 1e-12) return null;
        // 공분산 행렬 최대 고유벡터 방향.
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        return (cx, cy, Math.Cos(theta), Math.Sin(theta));
    }

    /// <summary>두 방향 사이의 각도(°, 0~90, 방향 부호 무시).</summary>
    public static double AngleBetweenDeg((double Dx, double Dy) a, (double Dx, double Dy) b)
    {
        double dot = Math.Abs(a.Dx * b.Dx + a.Dy * b.Dy);
        return Math.Acos(Math.Clamp(dot, 0, 1)) * 180.0 / Math.PI;
    }

    /// <summary>직선의 진행좌표 <paramref name="progress"/> 에서 cross-axis 좌표. 진행축과 평행하지 않으면 NaN.</summary>
    public static double CrossAt((double Cx, double Cy, double Dx, double Dy) l, double progress, bool horizontal)
    {
        double dp = horizontal ? l.Dx : l.Dy;
        double dc = horizontal ? l.Dy : l.Dx;
        double cp = horizontal ? l.Cx : l.Cy;
        double cc = horizontal ? l.Cy : l.Cx;
        if (Math.Abs(dp) < 1e-6) return double.NaN;
        return cc + (progress - cp) * dc / dp;
    }

    /// <summary>점에서 폴리라인까지 최소 거리.</summary>
    public static double DistToPolyline(double x, double y, IReadOnlyList<double[]> poly)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i + 1 < poly.Count; i++)
        {
            double ax = poly[i][0], ay = poly[i][1], bx = poly[i + 1][0], by = poly[i + 1][1];
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            double t = len2 <= 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1);
            double ex = ax + t * dx - x, ey = ay + t * dy - y;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    /// <summary>점에서 선 라벨까지 최소 거리. 끊긴 구간(<see cref="WeldLineLabel.Breaks"/>)은 선으로 보지 않는다.</summary>
    public static double DistToLabel(double x, double y, WeldLineLabel gt)
    {
        double best = double.PositiveInfinity;
        foreach (var (a, b) in gt.Edges())
            best = Math.Min(best, DistToPolyline(x, y, new[] { a, b }));
        return best;
    }

    /// <summary>
    /// 정답 대비 검출 결과 오차. 검출 실패·중심선 2점 미만이면 <see cref="LineErrors.Detected"/>=false.
    /// </summary>
    public static LineErrors Compare(WeldLineLabel gt, IReadOnlyList<PixelPoint> centerline, bool horizontal,
        double angleTolDeg = DefaultAngleTolDeg, double centerTolPx = DefaultCenterTolPx)
    {
        var gtDense = gt.Segments().Where(s => s.Count >= 2).SelectMany(Densify).ToList();
        var gtFit = FitLine(gtDense);
        var det = centerline.Select(p => (p.X, p.Y)).ToList();
        var detFit = FitLine(det);
        if (gtFit is null || detFit is null) return LineErrors.Missed;

        var g = gtFit.Value; var d = detFit.Value;
        double angle = AngleBetweenDeg((g.Dx, g.Dy), (d.Dx, d.Dy));
        double center = (horizontal ? gt.Width : gt.Height) / 2.0;
        double offset = Math.Abs(CrossAt(d, center, horizontal) - CrossAt(g, center, horizontal));
        double mean = det.Average(p => DistToLabel(p.X, p.Y, gt));

        // 커버리지: 정답 진행축 구간 [lo,hi] 을 1px 칸으로 나눠, 정답 선이 지나는 칸 중
        // 정답에서 ±(두께) 안의 검출 점이 있는 칸 비율. 끊긴 구간(정답 선이 없는 칸)은 분모에서 뺀다.
        double lo = gtDense.Min(p => horizontal ? p.X : p.Y);
        double hi = gtDense.Max(p => horizontal ? p.X : p.Y);
        int bins = Math.Max(1, (int)Math.Ceiling(hi - lo));
        var hasGt = new bool[bins];
        foreach (var (x, y) in gtDense) hasGt[Math.Clamp((int)((horizontal ? x : y) - lo), 0, bins - 1)] = true;
        var hit = new bool[bins];
        double tol = Math.Max(gt.Thickness, 3);
        foreach (var (x, y) in det)
        {
            double pr = horizontal ? x : y;
            if (pr < lo || pr > hi) continue;
            if (DistToLabel(x, y, gt) > tol) continue;
            hit[Math.Min(bins - 1, (int)(pr - lo))] = true;
        }
        int gtBins = hasGt.Count(h => h);
        double coverage = gtBins == 0 ? 0 : Enumerable.Range(0, bins).Count(i => hit[i] && hasGt[i]) / (double)gtBins;

        bool pass = !double.IsNaN(offset) && angle <= angleTolDeg && offset <= centerTolPx;
        return new LineErrors(true, angle, double.IsNaN(offset) ? double.PositiveInfinity : offset, mean, coverage, pass);
    }

    // 폴리라인 꼭짓점만으로 피팅하면 점 2개짜리 선은 OK 지만, 꺾인 선은 꼭짓점 분포에 끌린다 → 1px 간격으로 촘촘히.
    private static List<(double X, double Y)> Densify(IReadOnlyList<double[]> poly)
    {
        var r = new List<(double, double)>();
        for (int i = 0; i + 1 < poly.Count; i++)
        {
            double ax = poly[i][0], ay = poly[i][1], bx = poly[i + 1][0], by = poly[i + 1][1];
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay))));
            for (int k = 0; k < n; k++) r.Add((ax + (bx - ax) * k / n, ay + (by - ay) * k / n));
        }
        r.Add((poly[^1][0], poly[^1][1]));
        return r;
    }

    /// <summary>값 목록의 분위수(0~1, 선형보간). 비어 있으면 NaN.</summary>
    public static double Quantile(IEnumerable<double> values, double q)
    {
        var v = values.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        double pos = (v.Length - 1) * Math.Clamp(q, 0, 1);
        int i = (int)Math.Floor(pos);
        return i + 1 < v.Length ? v[i] + (v[i + 1] - v[i]) * (pos - i) : v[i];
    }
}

/// <summary>한 장의 오차. Detected=false 면 나머지 값은 의미 없음.</summary>
public sealed record LineErrors(bool Detected, double AngleDeg, double CenterOffsetPx, double MeanDistPx, double Coverage, bool Pass)
{
    public static readonly LineErrors Missed = new(false, double.NaN, double.NaN, double.NaN, 0, false);
}
