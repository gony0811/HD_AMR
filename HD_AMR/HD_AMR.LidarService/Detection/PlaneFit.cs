namespace HD_AMR.LidarService.Detection;

/// <summary>평면 <c>n·p = D</c>. <see cref="Normal"/> 은 항상 단위벡터.</summary>
internal readonly record struct Plane(double Nx, double Ny, double Nz, double D)
{
    public double DistanceTo(double x, double y, double z) => Math.Abs(Nx * x + Ny * y + Nz * z - D);

    /// <summary>두 평면 법선 사이 각(도). 0°나 180°에 가까우면 교선이 병적으로 부정확해진다.</summary>
    public double AngleDeg(in Plane other)
    {
        var dot = Math.Clamp(Nx * other.Nx + Ny * other.Ny + Nz * other.Nz, -1.0, 1.0);
        return Math.Acos(Math.Abs(dot)) * 180.0 / Math.PI;
    }
}

/// <summary>평면 피팅 결과.</summary>
internal sealed record PlaneFitResult(Plane Plane, int[] Inliers, double RmsMm);

/// <summary>
/// 점군에 대한 평면 피팅.
///
/// 능선 검출이 평면 피팅 위에 서는 이유는, 코러게이션의 마루가 <b>두 경사면이 만나는 교선</b>
/// 이기 때문이다. 마루 픽셀을 직접 찾는 방식(행마다 가장 가까운 점 고르기)보다 이쪽이
/// 결정적으로 유리한데, <b>마루 자체가 무효 픽셀이어도 동작하기 때문</b>이다. 금속 코러게이션은
/// 마루에서 정반사로 포화(ADC_OVERFLOW)되기 쉬운데, 그때도 양옆 경사면의 수천 점이 남아
/// 교선을 결정할 수 있다.
/// </summary>
internal static class PlaneFit
{
    /// <summary>
    /// RANSAC 으로 지배적 평면을 찾은 뒤 인라이어로 최소제곱 재피팅한다.
    /// 후보를 찾지 못하면 null.
    /// </summary>
    /// <param name="scoreSampleMax">
    /// 가설 채점에 쓸 최대 표본 수. 0 이면 후보 전체를 쓴다. 자세한 근거는 본문 주석 참고.
    /// </param>
    public static PlaneFitResult? Ransac(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z,
        int[] candidates, double inlierThresholdMm, int iterations, int minInliers, Random rng,
        int scoreSampleMax = 0)
    {
        if (candidates.Length < Math.Max(3, minInliers)) return null;

        // 가설 채점은 표본으로 한다.
        //
        // RANSAC 반복의 목적은 "어느 가설이 가장 많은 점을 설명하는가"를 <b>비교</b>하는 것이지
        // 인라이어 수를 정확히 세는 게 아니다. 순위만 가리면 되므로 표본으로 충분하다 —
        // 4000점 표본이면 인라이어 비율 추정의 표준편차가 0.8%p 수준이라, 실제로 지배적인
        // 평면이 표본 때문에 밀리는 일은 사실상 없다.
        //
        // 이게 왜 중요한가: 전체 채점은 500회 x 76,800점 = 3,840만 회 거리 계산이고, 실측에서
        // 검출 한 번에 1.3초가 걸렸다. 측정 경로에서는 프레임 수집(0.67초)보다 오래 걸리는
        // 구간이 되고, 모니터링 화면에서는 조준을 할 수 없을 만큼 느려진다.
        //
        // 정밀도는 손상되지 않는다. 최종 인라이어 수집과 최소제곱 재피팅은 <b>후보 전체</b>로
        // 하기 때문이다. 표본은 가설을 고르는 데만 쓰인다.
        var scoreSet = candidates;
        if (scoreSampleMax > 0 && candidates.Length > scoreSampleMax)
        {
            scoreSet = new int[scoreSampleMax];
            for (int i = 0; i < scoreSampleMax; i++)
                scoreSet[i] = candidates[rng.Next(candidates.Length)];
        }

        var bestCount = 0;
        Plane bestPlane = default;

        for (int iter = 0; iter < iterations; iter++)
        {
            // 3점을 뽑아 평면 후보를 만든다. 같은 점이 중복되면 법선이 0이 되어 자연히 걸러진다.
            var i0 = candidates[rng.Next(candidates.Length)];
            var i1 = candidates[rng.Next(candidates.Length)];
            var i2 = candidates[rng.Next(candidates.Length)];

            if (!TryPlaneFrom3(x, y, z, i0, i1, i2, out var plane)) continue;

            var count = 0;
            foreach (var i in scoreSet)
            {
                if (plane.DistanceTo(x[i], y[i], z[i]) <= inlierThresholdMm) count++;
            }

            if (count > bestCount)
            {
                bestCount = count;
                bestPlane = plane;
            }
        }

        // 표본으로 셌으므로 여기서 minInliers 와 직접 비교할 수 없다. 하한 판정은 아래에서
        // 후보 전체로 다시 센 결과(finalInliers)로 한다.
        if (bestCount == 0) return null;

        // 최종 인라이어 수집 후 최소제곱으로 재피팅한다. RANSAC 이 고른 3점만으로 정한
        // 평면은 노이즈에 민감하므로, 인라이어 전체로 다시 맞춰야 정밀도가 나온다.
        var inliers = new List<int>(candidates.Length / 2);
        foreach (var i in candidates)
        {
            if (bestPlane.DistanceTo(x[i], y[i], z[i]) <= inlierThresholdMm) inliers.Add(i);
        }

        var refined = LeastSquares(x, y, z, inliers);
        if (refined is null) return null;

        // 재피팅으로 평면이 조금 움직였으니 인라이어를 다시 판정한다.
        var finalInliers = new List<int>(inliers.Count);
        double sumSq = 0;
        foreach (var i in candidates)
        {
            var d = refined.Value.DistanceTo(x[i], y[i], z[i]);
            if (d > inlierThresholdMm) continue;
            finalInliers.Add(i);
            sumSq += d * d;
        }

        if (finalInliers.Count < minInliers) return null;

        return new PlaneFitResult(
            refined.Value,
            finalInliers.ToArray(),
            Math.Sqrt(sumSq / finalInliers.Count));
    }

    /// <summary>
    /// 중심점과 공분산 행렬의 최소 고유벡터로 평면을 구한다(직교 회귀).
    /// 점이 3개 미만이거나 일직선이면 null.
    /// </summary>
    public static Plane? LeastSquares(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, List<int> indices)
    {
        if (indices.Count < 3) return null;

        double cx = 0, cy = 0, cz = 0;
        foreach (var i in indices) { cx += x[i]; cy += y[i]; cz += z[i]; }
        var n = indices.Count;
        cx /= n; cy /= n; cz /= n;

        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var i in indices)
        {
            double dx = x[i] - cx, dy = y[i] - cy, dz = z[i] - cz;
            xx += dx * dx; xy += dx * dy; xz += dx * dz;
            yy += dy * dy; yz += dy * dz; zz += dz * dz;
        }

        var cov = new[,] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
        var (values, vectors) = SymmetricEigen3(cov);

        // 최소 고유값에 대응하는 고유벡터가 법선이다(그 방향의 분산이 가장 작다).
        var min = 0;
        if (values[1] < values[min]) min = 1;
        if (values[2] < values[min]) min = 2;

        double nx = vectors[0, min], ny = vectors[1, min], nz = vectors[2, min];
        var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12) return null;

        nx /= len; ny /= len; nz /= len;
        return new Plane(nx, ny, nz, nx * cx + ny * cy + nz * cz);
    }

    private static bool TryPlaneFrom3(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z,
        int a, int b, int c, out Plane plane)
    {
        double ux = x[b] - x[a], uy = y[b] - y[a], uz = z[b] - z[a];
        double vx = x[c] - x[a], vy = y[c] - y[a], vz = z[c] - z[a];

        double nx = uy * vz - uz * vy;
        double ny = uz * vx - ux * vz;
        double nz = ux * vy - uy * vx;

        var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-9) { plane = default; return false; }   // 세 점이 일직선

        nx /= len; ny /= len; nz /= len;
        plane = new Plane(nx, ny, nz, nx * x[a] + ny * y[a] + nz * z[a]);
        return true;
    }

    /// <summary>
    /// 3x3 대칭 행렬의 고유분해(Jacobi 회전). 반환은 (고유값[3], 고유벡터 열행렬[3,3]).
    /// 외부 선형대수 라이브러리를 들이지 않으려고 직접 구현했다 — 이 프로젝트는
    /// 의존성을 최소로 유지해야 젯슨 arm64 배포가 단순해진다.
    /// </summary>
    private static (double[] Values, double[,] Vectors) SymmetricEigen3(double[,] input)
    {
        var a = (double[,])input.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int sweep = 0; sweep < 32; sweep++)
        {
            var off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-14) break;

            for (int p = 0; p < 2; p++)
            {
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-18) continue;

                    var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;

                    var cos = 1 / Math.Sqrt(t * t + 1);
                    var sin = t * cos;

                    for (int k = 0; k < 3; k++)
                    {
                        var akp = a[k, p];
                        var akq = a[k, q];
                        a[k, p] = cos * akp - sin * akq;
                        a[k, q] = sin * akp + cos * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        var apk = a[p, k];
                        var aqk = a[q, k];
                        a[p, k] = cos * apk - sin * aqk;
                        a[q, k] = sin * apk + cos * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        var vkp = v[k, p];
                        var vkq = v[k, q];
                        v[k, p] = cos * vkp - sin * vkq;
                        v[k, q] = sin * vkp + cos * vkq;
                    }
                }
            }
        }

        return (new[] { a[0, 0], a[1, 1], a[2, 2] }, v);
    }
}
