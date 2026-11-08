using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 검출된 비드 중심선을 정답 선 라벨(<see cref="WeldLineLabel"/>)과 비교하는 오차 지표. 순수 함수.
/// <list type="bullet">
/// <item><b>각도 오차</b>(°): 두 직선(TLS 피팅) 방향 차이. 0~90.</item>
/// <item><b>중심 오차</b>(px): 진행축 FOV 중심 위치에서 두 직선의 cross-axis 거리 — 시퀀스가 실제로 쓰는 d 와 같은 위치.
/// 정답 선(가지)이 FOV 중심까지 오지 않으면 그 선 구간 안에서 중심에 가장 가까운 위치에서 잰다(구간 밖 외삽 금지).</item>
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
        // 정답 선이 여러 가지(교차·모서리·T자)면 전체를 직선 하나로 평균하면 어느 가지와도 맞지 않는다 →
        // 검출 선에 가장 가까운 가지 하나와만 비교(BranchMatched=true). 가지가 하나면 기존과 완전히 같다.
        var branches = Branches(gt);
        if (branches.Count > 1 && centerline.Count >= 2)
        {
            var detDense = Densify(centerline.Select(p => new[] { p.X, p.Y }).ToList());
            var nearest = branches.MinBy(b => detDense.Average(p => DistToLabel(p.X, p.Y, b)))!;
            return CompareCore(nearest, centerline, horizontal, angleTolDeg, centerTolPx) with { BranchMatched = true };
        }
        return CompareCore(gt, centerline, horizontal, angleTolDeg, centerTolPx);
    }

    /// <summary>정답 선의 가장 긴 가지(진행축 결정용)의 TLS 직선. 가지가 하나면 라벨 전체. 점이 없으면 null.</summary>
    public static (double Cx, double Cy, double Dx, double Dy)? MainBranchFit(WeldLineLabel gt)
    {
        var main = Branches(gt)
            .Select(b => b.Segments().Where(s => s.Count >= 2).SelectMany(Densify).ToList())
            .MaxBy(d => d.Count);   // Densify = 1px 간격 → 점 수 ≈ 길이
        return main is null ? null : FitLine(main);
    }

    /// <summary>조각을 나누는 꺾임 각도(°). 이웃한 두 변의 방향이 이보다 크게 바뀌는 꼭짓점에서 자른다.</summary>
    public const double BranchSplitDeg = 15.0;

    /// <summary>다른 가지로 보는 방향 차(°). 조각들의 방향이 이보다 크게 갈라질 때만 여러 가지(교차·모서리·T자)로 본다 —
    /// 비드 윤곽을 따라 구불구불 그린 일자 비드(꺾임 15~30°)는 한 가지로 남는다.</summary>
    public const double BranchAngleDeg = 30.0;

    /// <summary>
    /// 정답 선을 방향이 다른 가지(각각 하나의 선 라벨)로 나눈다. 꺾이는 꼭짓점(<see cref="BranchSplitDeg"/> 초과)에서
    /// 조각으로 자르고, 교차부를 잇는 짧은 연결 조각(두께×4 와 40px 중 큰 값 미만)은 버린 뒤, 방향 차가
    /// <see cref="BranchAngleDeg"/> 이하인 조각끼리(끊어 그린 구간 · 교차부를 관통하는 선 · 구불구불한 일자 비드) 한 가지로 묶는다.
    /// 가지가 하나면 원래 라벨 하나를 돌려준다.
    /// </summary>
    public static List<WeldLineLabel> Branches(WeldLineLabel gt, double splitDeg = BranchSplitDeg)
    {
        double minLen = Math.Max(gt.Thickness * 4, 40);
        var runs = new List<List<double[]>>();
        foreach (var seg in gt.Segments().Where(s => s.Count >= 2))
        {
            var run = new List<double[]> { seg[0], seg[1] };
            for (int i = 2; i < seg.Count; i++)
            {
                var a = run[^2]; var b = run[^1]; var c = seg[i];
                if (AngleBetweenDeg(Dir(a, b), Dir(b, c)) > splitDeg) { runs.Add(run); run = new List<double[]> { b, c }; }
                else run.Add(c);
            }
            runs.Add(run);
        }
        runs = runs.Where(r => Length(r) >= minLen).ToList();
        if (runs.Count <= 1) return new List<WeldLineLabel> { gt };

        // 방향이 비슷한 조각끼리 묶기(방향 차 BranchAngleDeg 이하).
        var groups = new List<List<List<double[]>>>();
        foreach (var r in runs)
        {
            var rf = FitLine(Densify(r))!.Value;
            var g = groups.FirstOrDefault(gr =>
            {
                var gf = FitLine(gr.SelectMany(Densify).ToList())!.Value;
                return AngleBetweenDeg((gf.Dx, gf.Dy), (rf.Dx, rf.Dy)) <= BranchAngleDeg;
            });
            if (g is null) groups.Add(new List<List<double[]>> { r }); else g.Add(r);
        }
        if (groups.Count <= 1) return new List<WeldLineLabel> { gt };
        return groups.Select(g =>
        {
            var pts = new List<double[]>(); var breaks = new List<int>();
            foreach (var r in g) { if (pts.Count > 0) breaks.Add(pts.Count); pts.AddRange(r); }
            return new WeldLineLabel { Width = gt.Width, Height = gt.Height, Thickness = gt.Thickness, Points = pts, Breaks = breaks.Count > 0 ? breaks : null };
        }).ToList();

        static (double, double) Dir(double[] p, double[] q)
        {
            double dx = q[0] - p[0], dy = q[1] - p[1], n = Math.Sqrt(dx * dx + dy * dy);
            return n < 1e-9 ? (1, 0) : (dx / n, dy / n);
        }
        static double Length(List<double[]> r)
        {
            double s = 0;
            for (int i = 0; i + 1 < r.Count; i++) s += Math.Sqrt(Math.Pow(r[i + 1][0] - r[i][0], 2) + Math.Pow(r[i + 1][1] - r[i][1], 2));
            return s;
        }
    }

    private static LineErrors CompareCore(WeldLineLabel gt, IReadOnlyList<PixelPoint> centerline, bool horizontal,
        double angleTolDeg, double centerTolPx)
    {
        var gtDense = gt.Segments().Where(s => s.Count >= 2).SelectMany(Densify).ToList();
        var gtFit = FitLine(gtDense);
        var det = centerline.Select(p => (p.X, p.Y)).ToList();
        var detFit = FitLine(det);
        if (gtFit is null || detFit is null) return LineErrors.Missed;

        var g = gtFit.Value; var d = detFit.Value;
        double angle = AngleBetweenDeg((g.Dx, g.Dy), (d.Dx, d.Dy));
        // 정답 진행축 구간 [lo,hi] — 중심 오차 측정 위치 제한과 커버리지에 쓴다.
        double lo = gtDense.Min(p => horizontal ? p.X : p.Y);
        double hi = gtDense.Max(p => horizontal ? p.X : p.Y);
        // FOV 중심이 정답 구간 밖이면(짧은 가지 등) 구간 안의 가장 가까운 위치에서 잰다 — 멀리 외삽하면
        // 작은 각도·라벨 꺾임 차이가 수십 px 로 부풀어 맞게 찾은 선도 불합격이 된다.
        double center = Math.Clamp((horizontal ? gt.Width : gt.Height) / 2.0, lo, hi);
        double offset = Math.Abs(CrossAt(d, center, horizontal) - CrossAt(g, center, horizontal));
        double mean = det.Average(p => DistToLabel(p.X, p.Y, gt));

        // 커버리지: 정답 진행축 구간 [lo,hi] 을 1px 칸으로 나눠, 정답 선이 지나는 칸 중
        // 정답에서 ±(두께) 안의 검출 점이 있는 칸 비율. 끊긴 구간(정답 선이 없는 칸)은 분모에서 뺀다.
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
/// <param name="BranchMatched">정답 선이 여러 가지라 검출 선에 가장 가까운 가지 하나와 비교했는지(느슨한 채점).</param>
public sealed record LineErrors(bool Detected, double AngleDeg, double CenterOffsetPx, double MeanDistPx, double Coverage, bool Pass,
    bool BranchMatched = false)
{
    public static readonly LineErrors Missed = new(false, double.NaN, double.NaN, double.NaN, 0, false);
}
