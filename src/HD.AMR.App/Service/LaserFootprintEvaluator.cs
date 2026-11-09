using System.Globalization;
using System.Runtime.InteropServices;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service;

/// <summary>
/// ④ 레이저 3점 풋프린트 판정 옵션 — 범용 key/value(<see cref="ParameterService"/>)에 보관, Parameters 페이지에서 조정.
///
/// 배경(2026-11-09): 헤드 3점은 한 변 약 38 mm 정삼각형(높이 약 33 mm)이라 한 점이 0.58 mm 만 떠도 틸트 1° 가 된다.
/// 깊이 셀(약 46×27 mm, 잔차 상한 1.5 mm)은 이 풋프린트보다 작고 기준도 느슨해, 셀은 평평해도 스팟 하나가
/// 이웃 비드·코로게이션 경사에 걸려 ④ Phase C 가 '비평탄/발산'으로 실패했다.
/// 그래서 셀 중심이 아니라 <b>세 스팟이 실제로 떨어질 자리</b>를 깊이 프레임에서 평가해 위치를 고른다.
/// </summary>
/// <param name="Enabled">false = 종전(셀 중심) 동작.</param>
/// <param name="MarginMm">스팟 위치 오차 여유 반경(mm) — 이 원 안 어디에 떨어져도 되는지 본다(개루프 이동·장착 오차).</param>
/// <param name="MaxPredictedTiltDeg">예측 최악 틸트 오차 상한(°). 넘으면 그 자리는 탈락.</param>
/// <param name="BlockMm">스팟 원 안 국부 높이를 평균할 블록 한 변(mm) — 깊이 노이즈 평활.</param>
/// <param name="HeightToleranceMm">국부 높이 허용 편차(mm) — 기준 평면 대비 이 안의 블록 편차는 깊이 노이즈·정수 mm
/// 양자화로 보고 0 으로 친다(0.4mm ≈ 레이저 틸트 0.7°). 비드(2~3mm)·코로게이션 경사는 이보다 훨씬 커서 걸러진다.</param>
/// <param name="SearchRangeMm">셀 중심 기준 탐색 반폭(mm) — 셀 경계 밖으로도 풋프린트를 옮겨 본다.</param>
/// <param name="SearchStepMm">탐색 격자 간격(mm).</param>
/// <param name="MaxSiteRetries">Phase C 실패 시 다른 자리로 다시 시도하는 최대 횟수(0 = 재시도 없음).</param>
/// <param name="ExcludeRadiusMm">실패한 자리 주변 제외 반경(mm) — 재시도에서 같은 곳을 다시 고르지 않게.</param>
public sealed record LaserFootprintOptions(
    bool Enabled = true,
    double MarginMm = 8.0,
    double MaxPredictedTiltDeg = 1.0,
    double BlockMm = 4.0,
    double HeightToleranceMm = 0.4,
    double SearchRangeMm = 18.0,
    double SearchStepMm = 6.0,
    int MaxSiteRetries = 2,
    double ExcludeRadiusMm = 25.0)
{
    public const string EnabledKey = "Sequence.FlatSurface.Footprint.Enabled";
    public const string MarginKey = "Sequence.FlatSurface.Footprint.MarginMm";
    public const string MaxTiltKey = "Sequence.FlatSurface.Footprint.MaxPredictedTiltDeg";
    public const string BlockKey = "Sequence.FlatSurface.Footprint.BlockMm";
    public const string HeightTolKey = "Sequence.FlatSurface.Footprint.HeightToleranceMm";
    public const string SearchRangeKey = "Sequence.FlatSurface.Footprint.SearchRangeMm";
    public const string SearchStepKey = "Sequence.FlatSurface.Footprint.SearchStepMm";
    public const string MaxSiteRetriesKey = "Sequence.FlatSurface.Footprint.MaxSiteRetries";
    public const string ExcludeRadiusKey = "Sequence.FlatSurface.Footprint.ExcludeRadiusMm";

    /// <summary>레이저 판정 원(스팟 외접원 + 여유)에서 기준 평면을 맞출 때 쓰는 추가 반경(mm).</summary>
    public const double ReferenceExtraMm = 6.0;

    public static async Task<LaserFootprintOptions> LoadAsync(ParameterService param)
    {
        var d = new LaserFootprintOptions();
        return new LaserFootprintOptions(
            await param.GetBoolAsync(EnabledKey) ?? d.Enabled,
            Positive(await param.GetDoubleAsync(MarginKey), d.MarginMm, allowZero: true),
            Positive(await param.GetDoubleAsync(MaxTiltKey), d.MaxPredictedTiltDeg),
            Positive(await param.GetDoubleAsync(BlockKey), d.BlockMm),
            Positive(await param.GetDoubleAsync(HeightTolKey), d.HeightToleranceMm, allowZero: true),
            Positive(await param.GetDoubleAsync(SearchRangeKey), d.SearchRangeMm, allowZero: true),
            Positive(await param.GetDoubleAsync(SearchStepKey), d.SearchStepMm),
            await param.GetIntAsync(MaxSiteRetriesKey) is >= 0 and <= 5 and var r ? r : d.MaxSiteRetries,
            Positive(await param.GetDoubleAsync(ExcludeRadiusKey), d.ExcludeRadiusMm, allowZero: true));

        static double Positive(double? v, double fallback, bool allowZero = false)
            => v is { } x && double.IsFinite(x) && (x > 0 || (allowZero && x == 0)) ? x : fallback;
    }

    /// <summary>Parameters 페이지 노출용 시드(이름, 값, 설명).</summary>
    public static IEnumerable<(string Name, string Value, string Desc)> SeedDefaults()
    {
        var d = new LaserFootprintOptions();
        string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        yield return (EnabledKey, d.Enabled ? "true" : "false",
            "④ 레이저 3점 풋프린트 판정 사용 — 셀 중심 대신 세 스팟이 떨어질 자리를 깊이로 평가해 고른다. false = 종전(셀 중심).");
        yield return (MarginKey, F(d.MarginMm),
            "④ 스팟 위치 오차 여유 반경(mm) — 각 스팟이 이 원 안 어디에 떨어져도 평탄해야 한다.");
        yield return (MaxTiltKey, F(d.MaxPredictedTiltDeg),
            "④ 깊이로 예측한 레이저 틸트 오차 최악값 상한(°). 넘는 자리는 탈락(2mm 비드가 스팟에 걸리면 3° 이상).");
        yield return (BlockKey, F(d.BlockMm),
            "④ 스팟 원 안 국부 높이 평균 블록 한 변(mm) — 클수록 노이즈에 강하고 작은 요철에 둔감.");
        yield return (HeightTolKey, F(d.HeightToleranceMm),
            "④ 국부 높이 허용 편차(mm) — 기준 평면 대비 이 안은 깊이 노이즈로 본다. 로그의 '기준면 노이즈 σ'가 크면 올린다.");
        yield return (SearchRangeKey, F(d.SearchRangeMm),
            "④ 후보 셀 중심 기준 풋프린트 탐색 반폭(mm). 0 = 셀 중심만 평가.");
        yield return (SearchStepKey, F(d.SearchStepMm), "④ 풋프린트 탐색 격자 간격(mm).");
        yield return (MaxSiteRetriesKey, d.MaxSiteRetries.ToString(CultureInfo.InvariantCulture),
            "④ Phase C(레이저 틸트) 실패 시 시작 위치로 돌아가 다른 자리로 재시도하는 최대 횟수(0~5).");
        yield return (ExcludeRadiusKey, F(d.ExcludeRadiusMm),
            "④ 재시도 때 실패한 자리 주변을 제외할 반경(mm).");
    }
}

/// <summary>한 자리(풋프린트 중심 u,v)의 판정 결과.</summary>
/// <param name="Valid">깊이 유효 픽셀이 충분해 판정했는가.</param>
/// <param name="WorstTiltDeg">스팟마다 원 안 최고/최저 블록(허용 편차를 뺀 값)을 골라 만든 8조합 중 최대 틸트 오차(°).</param>
/// <param name="SpotDevMm">스팟별 기준 평면 대비 최대 |블록 편차|(mm, 허용 편차 빼기 전) — 어느 스팟이 걸리는지 진단용.</param>
/// <param name="NoiseMm">기준 영역 픽셀 잔차의 로버스트 σ(1.4826·MAD, mm) — 깊이 노이즈 수준, 허용 편차 튜닝용.</param>
public sealed record FootprintEval(
    bool Valid, double U, double V, double WorstTiltDeg, IReadOnlyList<double> SpotDevMm, double DepthMm, string? Note,
    double NoiseMm = 0)
{
    public bool Passes(LaserFootprintOptions o) => Valid && WorstTiltDeg <= o.MaxPredictedTiltDeg;

    /// <summary>가장 크게 벗어난 스팟(0-based), 없으면 −1.</summary>
    public int WorstSpot => SpotDevMm.Count == 0 ? -1
        : SpotDevMm.Select((d, i) => (d, i)).MaxBy(t => t.d).i;

    public string ShortText() => Valid
        ? $"예측 틸트 오차 ≤{WorstTiltDeg:0.00}° (스팟 편차 {string.Join("/", SpotDevMm.Select(d => d.ToString("0.00")))}mm, " +
          $"기준면 노이즈 σ {NoiseMm:0.00}mm)"
        : $"판정 불가({Note})";
}

/// <summary>
/// 레이저 3점 풋프린트 판정(순수 함수) — 깊이 프레임에서 세 스팟 위치의 국부 높이를 넓은 기준 평면과 비교해
/// 레이저가 잴 틸트가 실제 면 기울기와 얼마나 어긋날지 예측한다.
///
/// 기준 평면은 풋프린트 전체(외접원 + 여유 + <see cref="LaserFootprintOptions.ReferenceExtraMm"/>) 픽셀로 맞추고
/// 큰 잔차를 한 번 걸러 다시 맞춘다(비드가 기준을 끌지 않게). 세 스팟 원만으로 맞추면 평면이 세 원 평균을
/// 정확히 지나 편차가 0 이 되므로 넓은 영역이 필요하다.
/// </summary>
public static class LaserFootprintEvaluator
{
    /// <summary>
    /// 헤드 3점(툴 XY, mm)을 중심 기준 상대 위치로 바꾼 뒤 이미지 mm 축으로 옮긴다.
    /// 이미지 +X 이동은 툴 <paramref name="imageX"/> 방향 이동에 대응하므로(<see cref="FlatSurfaceCenteringService.ApplyAxis"/>),
    /// 툴 오프셋의 그 축 성분(부호 포함)이 곧 이미지 X 오프셋이다.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> SpotOffsetsInImageMm(
        IReadOnlyList<(double X, double Y)> headsToolMm, ToolAxisDir imageX, ToolAxisDir imageY)
    {
        double cx = headsToolMm.Average(h => h.X), cy = headsToolMm.Average(h => h.Y);
        return headsToolMm.Select(h =>
        {
            var tool = new[] { h.X - cx, h.Y - cy, 0.0 };
            return (Component(tool, imageX), Component(tool, imageY));
        }).ToList();

        static double Component(double[] v, ToolAxisDir d)
            => ((int)d % 2 == 0 ? 1.0 : -1.0) * v[(int)d / 2];
    }

    /// <summary>
    /// 풋프린트 중심을 정규화 좌표 (<paramref name="u"/>, <paramref name="v"/>)에 둘 때의 판정.
    /// <paramref name="mmPerPx"/> 는 깊이 z(mm)에서 픽셀 한 칸의 (X, Y) mm 크기.
    /// </summary>
    public static FootprintEval Evaluate(
        CameraFrame depth, double u, double v, IReadOnlyList<(double X, double Y)> spotsImgMm,
        LaserFootprintOptions o, Func<double, (double X, double Y)> mmPerPx)
    {
        var px = MemoryMarshal.Cast<byte, ushort>(depth.Pixels);
        int w = depth.Width, h = depth.Height;
        double cu = u * w, cv = v * h;

        // 1) 중심 근방 깊이로 스케일 결정.
        double z0 = MedianDepth(px, w, h, cu, cv, 6);
        if (z0 <= 0) return Invalid(u, v, "중심 깊이 없음");
        var (sx, sy) = mmPerPx(z0);
        if (!(sx > 0) || !(sy > 0)) return Invalid(u, v, "스케일 없음");

        double spotR = spotsImgMm.Max(s => Math.Sqrt(s.X * s.X + s.Y * s.Y));
        double refR = spotR + o.MarginMm + LaserFootprintOptions.ReferenceExtraMm;

        // 2) 기준 평면 — 풋프린트 원 전체(mm 좌표, 중심 기준), 2.5·MAD 밖 제거 후 재맞춤.
        var pts = new List<(double X, double Y, double D)>();
        int total = 0;
        {
            int rx = (int)Math.Ceiling(refR / sx), ry = (int)Math.Ceiling(refR / sy);
            int xc = (int)Math.Round(cu), yc = (int)Math.Round(cv);
            for (int y = yc - ry; y <= yc + ry; y++)
            for (int x = xc - rx; x <= xc + rx; x++)
            {
                double dx = (x - cu) * sx, dy = (y - cv) * sy;
                if (dx * dx + dy * dy > refR * refR) continue;
                total++;   // 프레임 밖도 무효로 센다 — 풋프린트가 화면을 벗어나면 판정하지 않는다.
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int d = px[y * w + x];
                if (d > 0) pts.Add((dx, dy, d));
            }
        }
        if (total == 0) return Invalid(u, v, "프레임 밖");
        if (pts.Count < total * 0.5) return Invalid(u, v, $"유효 픽셀 부족({(double)pts.Count / total:P0})");

        var plane = FitPlane(pts);
        if (plane is null) return Invalid(u, v, "평면 미결정");
        var res = pts.Select(p => p.D - Eval(plane.Value, p.X, p.Y)).ToArray();
        var absSorted = res.Select(Math.Abs).OrderBy(r => r).ToArray();
        double mad = absSorted[absSorted.Length / 2];
        double noise = 1.4826 * mad;
        double cut = Math.Max(1.0, 2.5 * 1.4826 * mad);
        var inliers = pts.Where((_, i) => Math.Abs(res[i]) <= cut).ToList();
        if (inliers.Count >= pts.Count / 2 && FitPlane(inliers) is { } refined) plane = refined;

        // 3) 스팟별 — 여유 원 안 블록 평균 편차의 최고/최저.
        int bx = Math.Max(1, (int)Math.Round(o.BlockMm / sx)), by = Math.Max(1, (int)Math.Round(o.BlockMm / sy));
        var hi = new double[spotsImgMm.Count];
        var lo = new double[spotsImgMm.Count];
        var dev = new double[spotsImgMm.Count];
        for (var k = 0; k < spotsImgMm.Count; k++)
        {
            double pu = cu + spotsImgMm[k].X / sx, pv = cv + spotsImgMm[k].Y / sy;
            double best = double.NegativeInfinity, worst = double.PositiveInfinity;
            int nb = (int)Math.Ceiling(o.MarginMm / o.BlockMm);
            for (int j = -nb; j <= nb; j++)
            for (int i = -nb; i <= nb; i++)
            {
                double ox = i * o.BlockMm, oy = j * o.BlockMm;
                if (ox * ox + oy * oy > o.MarginMm * o.MarginMm + 1e-9) continue;
                double bcu = pu + ox / sx, bcv = pv + oy / sy;
                int x0 = (int)Math.Round(bcu - bx / 2.0), y0 = (int)Math.Round(bcv - by / 2.0);
                double sum = 0; int n = 0, cnt = 0;
                for (int y = y0; y < y0 + by; y++)
                for (int x = x0; x < x0 + bx; x++)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    cnt++;
                    int d = px[y * w + x];
                    if (d == 0) continue;
                    sum += d - Eval(plane.Value, (x - cu) * sx, (y - cv) * sy);
                    n++;
                }
                if (cnt == 0 || n < cnt * 0.5) continue;
                double m = sum / n;
                best = Math.Max(best, m);
                worst = Math.Min(worst, m);
            }
            if (double.IsNegativeInfinity(best))
                return Invalid(u, v, $"스팟{k + 1} 자리 깊이 없음");
            // 허용 편차 안은 노이즈로 보고 0 쪽으로 깎는다 — 남는 것은 노이즈를 넘는 실제 요철분.
            hi[k] = Math.Max(0, best - o.HeightToleranceMm);
            lo[k] = Math.Min(0, worst + o.HeightToleranceMm);
            dev[k] = Math.Max(Math.Abs(best), Math.Abs(worst));
        }

        // 4) 최악 조합 틸트 — 스팟마다 hi/lo 중 하나를 골라 세 점 평면의 기울기(스팟이 여유 원 안 어디에 떨어질지 모르므로).
        double worstTilt = 0;
        int comb = 1 << spotsImgMm.Count;
        for (int mask = 0; mask < comb; mask++)
        {
            var tri = new (double X, double Y, double D)[spotsImgMm.Count];
            for (var k = 0; k < tri.Length; k++)
                tri[k] = (spotsImgMm[k].X, spotsImgMm[k].Y, (mask >> k & 1) == 1 ? hi[k] : lo[k]);
            if (FitPlane(tri) is { } p)
                worstTilt = Math.Max(worstTilt, Math.Atan(Math.Sqrt(p.A * p.A + p.B * p.B)) * 180 / Math.PI);
        }

        return new FootprintEval(true, u, v, worstTilt, dev, z0, null, noise);
    }

    /// <summary>
    /// 후보 셀 중심 (<paramref name="u0"/>, <paramref name="v0"/>) 주변 격자(±<see cref="LaserFootprintOptions.SearchRangeMm"/>)를
    /// 평가해 예측 틸트 오차가 가장 작은 자리를 고른다(같으면 셀 중심에 가까운 쪽). 자리 중심은 ROI 안이어야 하고,
    /// <paramref name="excluded"/> (정규화 좌표) 반경 <see cref="LaserFootprintOptions.ExcludeRadiusMm"/> 안은 건너뛴다.
    /// 평가 가능한 자리가 없으면 null.
    /// </summary>
    public static FootprintEval? Search(
        CameraFrame depth, double u0, double v0, double roiX, double roiY, double roiW, double roiH,
        IReadOnlyList<(double X, double Y)> spotsImgMm, LaserFootprintOptions o,
        Func<double, (double X, double Y)> mmPerPx, IReadOnlyList<(double U, double V)>? excluded = null)
    {
        var px = MemoryMarshal.Cast<byte, ushort>(depth.Pixels);
        double z = MedianDepth(px, depth.Width, depth.Height, u0 * depth.Width, v0 * depth.Height, 6);
        if (z <= 0) z = 400;
        var (sx, sy) = mmPerPx(z);
        if (!(sx > 0) || !(sy > 0)) return null;
        double mmPerU = sx * depth.Width, mmPerV = sy * depth.Height;   // 정규화 좌표 1 당 mm

        int n = o.SearchRangeMm > 0 && o.SearchStepMm > 0 ? (int)Math.Floor(o.SearchRangeMm / o.SearchStepMm) : 0;
        FootprintEval? best = null;
        double bestDist = double.MaxValue;
        for (int j = -n; j <= n; j++)
        for (int i = -n; i <= n; i++)
        {
            double ox = i * o.SearchStepMm, oy = j * o.SearchStepMm;
            double u = u0 + ox / mmPerU, v = v0 + oy / mmPerV;
            if (u < roiX || u > roiX + roiW || v < roiY || v > roiY + roiH) continue;
            if (excluded is not null && o.ExcludeRadiusMm > 0 && excluded.Any(e =>
                {
                    double ex = (u - e.U) * mmPerU, ey = (v - e.V) * mmPerV;
                    return ex * ex + ey * ey < o.ExcludeRadiusMm * o.ExcludeRadiusMm;
                }))
                continue;

            var e = Evaluate(depth, u, v, spotsImgMm, o, mmPerPx);
            if (!e.Valid) continue;
            double dist = ox * ox + oy * oy;
            if (best is null || e.WorstTiltDeg < best.WorstTiltDeg - 0.05
                || (Math.Abs(e.WorstTiltDeg - best.WorstTiltDeg) <= 0.05 && dist < bestDist))
            {
                best = e;
                bestDist = dist;
            }
        }
        return best;
    }

    private static FootprintEval Invalid(double u, double v, string note)
        => new(false, u, v, double.PositiveInfinity, Array.Empty<double>(), 0, note);

    private static double MedianDepth(ReadOnlySpan<ushort> px, int w, int h, double cu, double cv, int r)
    {
        var vals = new List<int>();
        int xc = (int)Math.Round(cu), yc = (int)Math.Round(cv);
        for (int y = Math.Max(0, yc - r); y <= Math.Min(h - 1, yc + r); y++)
        for (int x = Math.Max(0, xc - r); x <= Math.Min(w - 1, xc + r); x++)
            if (px[y * w + x] is > 0 and var d) vals.Add(d);
        if (vals.Count == 0) return 0;
        vals.Sort();
        return vals[vals.Count / 2];
    }

    private readonly record struct Plane(double A, double B, double C);

    private static double Eval(Plane p, double x, double y) => p.A * x + p.B * y + p.C;

    /// <summary>최소제곱 평면 d = A·x + B·y + C (정규방정식, Cramer). 미결정이면 null.</summary>
    private static Plane? FitPlane(IReadOnlyCollection<(double X, double Y, double D)> pts)
    {
        if (pts.Count < 3) return null;
        double n = pts.Count, mx = 0, my = 0, md = 0;
        foreach (var p in pts) { mx += p.X; my += p.Y; md += p.D; }
        mx /= n; my /= n; md /= n;
        double sxx = 0, syy = 0, sxy = 0, sxd = 0, syd = 0;
        foreach (var p in pts)
        {
            double x = p.X - mx, y = p.Y - my, d = p.D - md;
            sxx += x * x; syy += y * y; sxy += x * y; sxd += x * d; syd += y * d;
        }
        double det = sxx * syy - sxy * sxy;
        if (Math.Abs(det) <= 1e-9 * Math.Max(1e-12, sxx * syy)) return null;
        double a = (sxd * syy - syd * sxy) / det;
        double b = (syd * sxx - sxd * sxy) / det;
        return new Plane(a, b, md - a * mx - b * my);
    }
}
