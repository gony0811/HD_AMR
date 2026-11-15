namespace HD.AMR.App.Service;

/// <summary>
/// 깊이 셀 하나의 최소제곱 평면 맞춤(d = a·x + b·y + c) 누적기 — 평면 잔차 RMS 를 낸다.
///
/// 셀 σ(깊이 단순 표준편차)는 <b>기울기</b>까지 포함해 "평평하지만 기운 면"과 "비드·단차가 걸친 면"을
/// 구분하지 못한다(셀 폭 45 mm 에서 5° 기울기 ≈ 1.1 mm, 2 mm 비드가 20% 덮음 ≈ 0.8 mm). 평면을 빼고 남은
/// 잔차는 센서 노이즈 + 단차만 남으므로 "레이저 3점이 같은 평면에 떨어지는가"의 판정 근거가 된다
/// (기울기는 ④ Phase C 틸트 보정이 처리한다).
///
/// 수치 안정 — 좌표는 셀 중심 기준(<paramref name="originX"/>, <paramref name="originY"/>), 깊이는 첫 샘플
/// 기준으로 빼서 누적한다(400 mm² × 수천 픽셀의 상쇄 오차 방지).
/// </summary>
internal struct DepthPlaneFit
{
    private readonly double _ox, _oy;
    private double _d0;
    private long _n;
    private double _sx, _sy, _sxx, _syy, _sxy, _sd, _sxd, _syd, _sdd;

    public DepthPlaneFit(double originX, double originY)
    {
        _ox = originX;
        _oy = originY;
        _d0 = 0;
        _n = 0;
        _sx = _sy = _sxx = _syy = _sxy = _sd = _sxd = _syd = _sdd = 0;
    }

    public readonly long Count => _n;

    public void Add(double x, double y, double depth)
    {
        if (_n == 0) _d0 = depth;
        double u = x - _ox, v = y - _oy, d = depth - _d0;
        _n++;
        _sx += u; _sy += v;
        _sxx += u * u; _syy += v * v; _sxy += u * v;
        _sd += d; _sxd += u * d; _syd += v * d; _sdd += d * d;
    }

    /// <summary>평면 잔차 RMS(깊이 단위). 점이 3개 미만이거나 한 줄로 늘어서 평면이 정해지지 않으면
    /// 평균만 뺀 표준편차로 대신한다(= 셀 σ).</summary>
    public readonly double ResidualRms()
    {
        if (_n == 0) return 0;
        double n = _n;

        // 정규방정식 M·[a b c]ᵀ = r — Cramer 로 푼다.
        double m00 = _sxx, m01 = _sxy, m02 = _sx;
        double m11 = _syy, m12 = _sy, m22 = n;
        double det = m00 * (m11 * m22 - m12 * m12)
                   - m01 * (m01 * m22 - m12 * m02)
                   + m02 * (m01 * m12 - m11 * m02);

        double ss;
        // 스케일 무관 특이 판정 — 대각 곱 대비 행렬식이 너무 작으면 평면 미결정.
        if (_n < 3 || Math.Abs(det) <= 1e-9 * Math.Max(1e-12, m00 * m11 * m22))
        {
            ss = _sdd - _sd * _sd / n;
        }
        else
        {
            double r0 = _sxd, r1 = _syd, r2 = _sd;
            double a = (r0 * (m11 * m22 - m12 * m12) - m01 * (r1 * m22 - m12 * r2) + m02 * (r1 * m12 - m11 * r2)) / det;
            double b = (m00 * (r1 * m22 - m12 * r2) - r0 * (m01 * m22 - m12 * m02) + m02 * (m01 * r2 - r1 * m02)) / det;
            double c = (m00 * (m11 * r2 - r1 * m12) - m01 * (m01 * r2 - r1 * m02) + r0 * (m01 * m12 - m11 * m02)) / det;
            ss = _sdd - a * _sxd - b * _syd - c * _sd;
        }
        return Math.Sqrt(Math.Max(0, ss) / n);
    }
}
