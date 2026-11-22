using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Data.Entities;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 코로게이션 단면 형상(작업물 프레임 X–Z 평면, 한 피치 Top→Top). 기본값은 도면
/// <c>MEMBRANE-A_ONE (10-45) (260716).dxf</c> 에서 뽑았다 — 그 도면은 선/호가 없이 POINT 94개뿐이라,
/// 점들에 맞춘 해석 형상으로 재구성한다(반 피치, Top 기준 dx):
/// <list type="number">
///   <item>Top 원호: 반경 <see cref="TopRadiusMm"/>, 중심 (0, TopHeight−R). dx = 0 ~ <see cref="ArcEndDxMm"/>.
///         도면 Top 점들(10° 간격)이 R 28.2605 원 위에 1e-4mm 이내로 놓인다.</item>
///   <item>옆면(플랭크): 도면 점 <see cref="FlankMid"/>·<see cref="FlankEnd"/> 를 지나는 C1 3차 Hermite.
///         양끝 기울기는 원호 접선·필렛 접선, 중간점은 양쪽 현 기울기의 조화평균(단조·오버슈트 없음).</item>
///   <item>바닥 필렛: 반경 <see cref="FilletRadiusMm"/>, 바닥(Z=0)에 접한다. <see cref="FlankEnd"/> 가 필렛 위 점이다 —
///         도면의 (30.9289, 2.9289) 는 R10·(1−cos45°)=2.9289 라 R10 필렛의 45° 점으로 본다.</item>
///   <item>평탄부 Z = 0 — 반 피치까지.</item>
/// </list>
/// 반대쪽 반 피치는 X = Pitch/2 에 대해 대칭이다. 좌표는 <see cref="InternalWorkpieceFrame"/> 규약:
/// X=0 이 점1(Top), X+ 가 점2(다음 Top) 방향, Z=0 이 코로게이션 바닥, Z=TopHeight 가 Top.
/// </summary>
public sealed record CorrugationProfileShape
{
    public double PitchMm { get; init; } = 360.0;
    public double TopHeightMm { get; init; } = InternalWorkpieceFrame.DefaultCorrugationTopHeightMm;
    public double TopRadiusMm { get; init; } = 28.2605;
    /// <summary>Top 원호가 끝나고 플랭크가 시작되는 dx(Top 기준) — 도면 점 (25.7617, 17.1586).</summary>
    public double ArcEndDxMm { get; init; } = 25.7617;
    /// <summary>플랭크 중간 통과점 (dx, z) — 도면 점.</summary>
    public (double Dx, double Z) FlankMid { get; init; } = (27.5430, 11.8688);
    /// <summary>플랭크 끝 = 필렛 시작점 (dx, z) — 도면 점.</summary>
    public (double Dx, double Z) FlankEnd { get; init; } = (30.9289, 2.9289);
    public double FilletRadiusMm { get; init; } = 10.0;

    /// <summary>도면 MEMBRANE-A (pitch 360, 높이 33.8) 기본 형상.</summary>
    public static CorrugationProfileShape MembraneA { get; } = new();
}

/// <summary>코로게이션 프로파일 샘플링 옵션.</summary>
/// <param name="SpacingMm">측정 포인트 간격 [mm] — 단면을 <b>따라 잰 길이(호장)</b> 기준. 전체 길이를 이 값 이하의 등간격으로 나눈다
/// (양 끝 Top 이 정확히 포함되도록 실제 간격은 이 값보다 약간 작을 수 있다).</param>
/// <param name="StandoffMm">팁 이격 [mm] — 작업물 Z+(평면 수직, 툴축) 방향으로 띄운다. 0 = TCP 를 표면 위에 둔다(기존 LINE-1-COR 와 동일).
/// 툴축이 평면 수직 고정이라 경사 θ 인 옆면에서 표면까지의 수직 거리는 이격·cosθ 로 줄어든다(73° 옆면 ≈ 0.3배).</param>
/// <param name="FlankClearanceMm">옆면 여유 [mm] — 평탄부에서 실제 필렛 시작보다 이만큼 일찍 올라가기 시작한다(내려올 때는 이만큼
/// 늦게 평탄부에 닿는다). 수평 여유는 바닥에서 이 값이고 높이에 비례해 줄어 <b>Top 에서 0</b> 이 된다 — 경로는 Top 을 그대로 지나고
/// 평탄부와 만나는 구석만 비켜 간다(팁이 코로게이션 발치까지 들어가지 않게). 0 = 도면 단면 그대로. 필렛 끝 + 여유가 반 피치를 넘으면 실패.</param>
public sealed record CorrugationSamplingOptions(double SpacingMm = 2.0, double StandoffMm = 0.0, double FlankClearanceMm = 0.0);

/// <summary>샘플 한 점(작업물 프레임). <see cref="SurfaceSlopeDeg"/> = 그 점의 표면 경사(참고용, 툴 자세엔 쓰지 않음).</summary>
public readonly record struct CorrugationSample(double S, double X, double Z, double SurfaceSlopeDeg, SurfaceType Surface);

/// <summary>
/// 코로게이션 1 피치(Top→Top) 가스 검사 경로 생성(순수 함수). 단면을 따라 호장 등간격으로 점을 찍는다.
/// 손목은 틸트하지 않는다 — 가스 검사 팁은 작업물 평면에 수직(Rx=Ry=Rz=0, 점1 Top 교시 자세 그대로)으로 세운 채
/// X–Z 위치만 코로게이션 곡선을 따라간다.
/// 출력 경유점은 /inspection-points 절대 6-DOF 형식(Y=0, Rx=0, θ=0, Rz=0) — <see cref="InspectionProfile.PoseAbsolute"/>=true 로 저장한다.
/// </summary>
public static class CorrugationProfileGenerator
{
    // 호장 적분용 X 분할 수(1 피치). 360mm 기준 2µm — 경사 73° 구간에서도 호장 오차 &lt; 1e-6mm.
    private const int DenseSteps = 180_000;

    /// <summary>형상 유효성 검사. 문제 없으면 null.</summary>
    public static string? Validate(CorrugationProfileShape g)
    {
        if (g.PitchMm <= 0 || g.TopHeightMm <= 0 || g.TopRadiusMm <= 0 || g.FilletRadiusMm <= 0)
            return "피치·높이·Top 반경·필렛 반경은 양수여야 합니다.";
        if (!(0 < g.ArcEndDxMm && g.ArcEndDxMm < g.FlankMid.Dx && g.FlankMid.Dx < g.FlankEnd.Dx))
            return "dx 순서가 Top 원호 끝 < 플랭크 중간 < 플랭크 끝 이어야 합니다.";
        if (g.ArcEndDxMm >= g.TopRadiusMm)
            return "Top 원호 끝 dx 가 Top 반경 이상입니다.";
        var zArc = ArcZ(g, g.ArcEndDxMm);
        if (!(zArc > g.FlankMid.Z && g.FlankMid.Z > g.FlankEnd.Z && g.FlankEnd.Z > 0))
            return "플랭크 높이가 Top 원호 끝 > 중간 > 끝 > 0 순서가 아닙니다.";
        if (g.FlankEnd.Z >= g.FilletRadiusMm)
            return $"플랭크 끝 높이({g.FlankEnd.Z})가 필렛 반경({g.FilletRadiusMm}) 이상 — 필렛 위 점이 될 수 없습니다.";
        if (FilletCenterDx(g) > g.PitchMm / 2)
            return "필렛 끝이 반 피치를 넘습니다 — 피치가 너무 짧습니다.";
        return null;
    }

    /// <summary>단면 높이 Z(x)와 기울기 dZ/dX (x ∈ [0, Pitch]).</summary>
    public static (double Z, double Slope) Evaluate(CorrugationProfileShape g, double x) => Evaluate(g, x, 0);

    /// <summary>
    /// 옆면 여유 <paramref name="clearanceMm"/> 를 준 경로 단면의 높이·기울기. 표면 위 점 (s, Z(s)) 를 수평으로
    /// 여유·(1 − Z(s)/Top높이) 만큼 Top 에서 멀어지는 쪽으로 민 곡선이다 — 바닥에서 여유만큼, Top 에서 0.
    /// 표면보다 낮아지는 곳은 없다.
    /// </summary>
    public static (double Z, double Slope) Evaluate(CorrugationProfileShape g, double x, double clearanceMm)
    {
        var half = g.PitchMm / 2;
        if (x <= half)
            return HalfWithClearance(g, Math.Max(0, x), clearanceMm);
        var (z, m) = HalfWithClearance(g, Math.Max(0, g.PitchMm - x), clearanceMm);
        return (z, -m);
    }

    private static (double Z, double Slope) HalfWithClearance(CorrugationProfileShape g, double dx, double c)
    {
        if (!(c > 0)) return Half(g, dx);
        var foot = FilletCenterDx(g);
        if (dx >= foot + c) return (0.0, 0.0);
        // dx = s + c·(1 − Z(s)/H) 를 s 에 대해 푼다 — Z 가 s 에 대해 줄어들기만 하므로 우변은 단조 증가(이분법).
        double lo = 0, hi = foot;
        for (var i = 0; i < 50; i++)
        {
            var mid = (lo + hi) / 2;
            if (mid + c * (1 - Half(g, mid).Z / g.TopHeightMm) < dx) lo = mid; else hi = mid;
        }
        var (z, slope) = Half(g, (lo + hi) / 2);
        return (z, slope / (1 - c * slope / g.TopHeightMm));
    }

    /// <summary>1 피치 단면 전체 길이(호장) [mm].</summary>
    public static double Length(CorrugationProfileShape g, double clearanceMm = 0) => BuildArcLengthTable(g, clearanceMm).S[^1];

    /// <summary>호장 등간격 샘플. 실패 시 false + error.</summary>
    public static bool TrySample(CorrugationProfileShape g, CorrugationSamplingOptions o,
        out List<CorrugationSample> samples, out string? error)
    {
        samples = new List<CorrugationSample>();
        error = Validate(g);
        if (error is not null) return false;
        if (!(o.SpacingMm > 0) || !double.IsFinite(o.SpacingMm))
        {
            error = $"측정 간격({o.SpacingMm})은 양수여야 합니다.";
            return false;
        }
        if (!(o.StandoffMm >= 0) || !double.IsFinite(o.StandoffMm))
        {
            error = $"팁 이격({o.StandoffMm})은 0 이상이어야 합니다.";
            return false;
        }
        if (!(o.FlankClearanceMm >= 0) || !double.IsFinite(o.FlankClearanceMm))
        {
            error = $"옆면 여유({o.FlankClearanceMm})는 0 이상이어야 합니다.";
            return false;
        }
        if (FilletCenterDx(g) + o.FlankClearanceMm > g.PitchMm / 2)
        {
            error = $"옆면 여유({o.FlankClearanceMm})가 너무 큽니다 — 평탄부가 남지 않습니다.";
            return false;
        }

        var c = o.FlankClearanceMm;
        var (xs, s) = BuildArcLengthTable(g, c);
        var length = s[^1];
        var n = Math.Max(1, (int)Math.Ceiling(length / o.SpacingMm - 1e-9));
        var step = length / n;
        var filletEnd = FilletCenterDx(g) + c;

        var j = 1;
        for (var k = 0; k <= n; k++)
        {
            var target = k == n ? length : k * step;
            while (j < s.Length - 1 && s[j] < target) j++;
            var span = s[j] - s[j - 1];
            var x = span > 0 ? xs[j - 1] + (xs[j] - xs[j - 1]) * (target - s[j - 1]) / span : xs[j];
            if (k == 0) x = 0;
            if (k == n) x = g.PitchMm;

            var (z, slope) = Evaluate(g, x, c);
            var slopeDeg = Math.Atan(slope) * 180.0 / Math.PI;

            var dx = Math.Min(x, g.PitchMm - x);
            var surface = dx < filletEnd - 1e-9 ? SurfaceType.Corrugation : SurfaceType.Flat;
            samples.Add(new CorrugationSample(target, x, z + o.StandoffMm, Clean(slopeDeg), surface));
        }
        return true;
    }

    /// <summary>
    /// 경유점 생성 — /inspection-points 절대 6-DOF 프로필 형식(X, Y=0, Z, Rx=0, Ry=0, Rz=0) — 손목 평면 수직 고정.
    /// </summary>
    public static bool TryGenerate(CorrugationProfileShape g, CorrugationSamplingOptions o,
        out List<InspectionWaypoint> waypoints, out string? error)
    {
        waypoints = new List<InspectionWaypoint>();
        if (!TrySample(g, o, out var samples, out error)) return false;
        foreach (var p in samples)
            waypoints.Add(new InspectionWaypoint(
                X: Math.Round(p.X, 3), Z: Math.Round(p.Z, 3), Theta: 0, ThetaManual: true,
                Surface: (byte)p.Surface, SurfaceManual: true, Y: 0, RzDeg: 0, RxDeg: 0));
        return true;
    }

    // ── 형상 ────────────────────────────────────────────────────────────

    private static double ArcCenterZ(CorrugationProfileShape g) => g.TopHeightMm - g.TopRadiusMm;

    private static double ArcZ(CorrugationProfileShape g, double dx)
        => ArcCenterZ(g) + Math.Sqrt(g.TopRadiusMm * g.TopRadiusMm - dx * dx);

    /// <summary>필렛이 플랭크 끝점에서 이루는 경사각 α (rad) — z = R(1−cosα).</summary>
    private static double FilletAngle(CorrugationProfileShape g) => Math.Acos(1 - g.FlankEnd.Z / g.FilletRadiusMm);

    /// <summary>필렛 중심 dx(= 평탄부 시작).</summary>
    private static double FilletCenterDx(CorrugationProfileShape g)
        => g.FlankEnd.Dx + g.FilletRadiusMm * Math.Sin(FilletAngle(g));

    private static (double Z, double Slope) Half(CorrugationProfileShape g, double dx)
    {
        if (dx <= g.ArcEndDxMm)
        {
            var z = ArcZ(g, dx);
            return (z, -dx / (z - ArcCenterZ(g)));
        }

        var x1 = g.ArcEndDxMm;
        var z1 = ArcZ(g, x1);
        var m1 = -x1 / (z1 - ArcCenterZ(g));
        var (x2, z2) = g.FlankMid;
        var (x3, z3) = g.FlankEnd;
        var m3 = -Math.Tan(FilletAngle(g));
        var d12 = (z2 - z1) / (x2 - x1);
        var d23 = (z3 - z2) / (x3 - x2);
        // 같은 부호 현 기울기의 조화평균(Fritsch–Carlson) — 단조 보간, 오버슈트 없음.
        var m2 = d12 * d23 > 0 ? 2 * d12 * d23 / (d12 + d23) : 0.0;

        if (dx <= x2) return Hermite(dx, x1, z1, m1, x2, z2, m2);
        if (dx <= x3) return Hermite(dx, x2, z2, m2, x3, z3, m3);

        var r = g.FilletRadiusMm;
        var cx = FilletCenterDx(g);
        if (dx < cx)
        {
            var u = cx - dx;
            var root = Math.Sqrt(r * r - u * u);
            return (r - root, -u / root);
        }
        return (0.0, 0.0);
    }

    private static (double Z, double Slope) Hermite(double x, double x0, double z0, double m0, double x1, double z1, double m1)
    {
        var h = x1 - x0;
        var t = (x - x0) / h;
        double t2 = t * t, t3 = t2 * t;
        var z = (2 * t3 - 3 * t2 + 1) * z0 + (t3 - 2 * t2 + t) * h * m0 + (-2 * t3 + 3 * t2) * z1 + (t3 - t2) * h * m1;
        var dz = (6 * t2 - 6 * t) / h * z0 + (3 * t2 - 4 * t + 1) * m0 + (-6 * t2 + 6 * t) / h * z1 + (3 * t2 - 2 * t) * m1;
        return (z, dz);
    }

    private static (double[] X, double[] S) BuildArcLengthTable(CorrugationProfileShape g, double clearanceMm = 0)
    {
        var xs = new double[DenseSteps + 1];
        var s = new double[DenseSteps + 1];
        var prevZ = Evaluate(g, 0, clearanceMm).Z;
        for (var i = 1; i <= DenseSteps; i++)
        {
            xs[i] = g.PitchMm * i / DenseSteps;
            var z = Evaluate(g, xs[i], clearanceMm).Z;
            var dx = xs[i] - xs[i - 1];
            s[i] = s[i - 1] + Math.Sqrt(dx * dx + (z - prevZ) * (z - prevZ));
            prevZ = z;
        }
        return (xs, s);
    }

    private static double Clean(double v) => Math.Abs(v) < 1e-9 ? 0.0 : v;
}
