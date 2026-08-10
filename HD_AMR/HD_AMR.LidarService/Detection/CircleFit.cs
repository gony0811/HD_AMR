namespace HD_AMR.LidarService.Detection;

/// <summary>2차원 원. 좌표 단위는 mm.</summary>
internal readonly record struct Circle(double CenterX, double CenterY, double Radius);

internal sealed record CircleFitResult(Circle Circle, int[] Inliers, double RmsMm);

/// <summary>
/// 2차원 원 피팅.
///
/// <b>왜 원인가.</b> 측정 대상의 마루는 뾰족한 모서리가 아니라 <b>반원 비드</b>(반경 35mm)의
/// 정점이다. 두 평면의 교선으로 찾는 방식은 여기에 원리적으로 맞지 않는다 — 비드 양옆이
/// 같은 평판이라 교차하는 두 평면 자체가 존재하지 않는다.
///
/// <b>정점을 직접 고르지 않는 이유.</b> 반경 35mm 원의 정점 부근은 거의 평평하다. 정점에서
/// 옆으로 10mm 가도 높이는 1.4mm 밖에 안 떨어진다. 픽셀 노이즈가 σ 4~7mm 이므로 "제일 높은
/// 픽셀"을 고르면 가로 위치가 ±30mm 씩 튄다 — 요구 정밀도 10mm 의 세 배다.
///
/// 반면 비드 <b>옆면</b>은 기울기가 가파르다. 옆면 점 수백 개로 원을 맞추면 중심이 잘 정해지고,
/// 정점은 중심에서 반경만큼 떨어진 점으로 해석적으로 나온다. 정점에 유효 픽셀이 하나도 없어도
/// (광택 금속에서 정반사로 포화되는 상황이 실제로 관측됐다) 위치를 낼 수 있다.
/// </summary>
internal static class CircleFit
{
    /// <summary>
    /// RANSAC 으로 지배적 원을 찾은 뒤 인라이어로 대수적 재피팅한다.
    ///
    /// 높이로 걸러낸 비드 점군이라 대체로 깨끗하지만, 용접 비드나 가장자리 잡음이 섞일 수
    /// 있어 RANSAC 을 둔다.
    /// </summary>
    public static CircleFitResult? Ransac(
        ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, int[] candidates,
        double inlierThresholdMm, int iterations, int minInliers, Random rng)
    {
        if (candidates.Length < Math.Max(3, minInliers)) return null;

        var bestCount = 0;
        Circle best = default;

        for (int iter = 0; iter < iterations; iter++)
        {
            var i0 = candidates[rng.Next(candidates.Length)];
            var i1 = candidates[rng.Next(candidates.Length)];
            var i2 = candidates[rng.Next(candidates.Length)];

            if (!TryCircleFrom3(xs, ys, i0, i1, i2, out var circle)) continue;

            var count = 0;
            foreach (var i in candidates)
            {
                if (Residual(circle, xs[i], ys[i]) <= inlierThresholdMm) count++;
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = circle;
            }
        }

        if (bestCount < minInliers) return null;

        // 3점으로 정한 원은 노이즈에 민감하다. 인라이어 전체로 다시 맞춰야 정밀도가 나온다.
        var inliers = new List<int>(bestCount);
        foreach (var i in candidates)
        {
            if (Residual(best, xs[i], ys[i]) <= inlierThresholdMm) inliers.Add(i);
        }

        var refined = Algebraic(xs, ys, inliers);
        if (refined is null) return null;

        var finalInliers = new List<int>(inliers.Count);
        double sumSq = 0;
        foreach (var i in candidates)
        {
            var r = Residual(refined.Value, xs[i], ys[i]);
            if (r > inlierThresholdMm) continue;
            finalInliers.Add(i);
            sumSq += r * r;
        }

        if (finalInliers.Count < minInliers) return null;

        return new CircleFitResult(
            refined.Value, finalInliers.ToArray(), Math.Sqrt(sumSq / finalInliers.Count));
    }

    /// <summary>
    /// Kåsa 대수적 원 피팅. <c>x² + y² + Ax + By + C = 0</c> 의 잔차 제곱합을 최소화하는
    /// 선형 최소제곱이라 닫힌 해가 있다 — 반복 최적화가 필요 없어 미리보기 주기 안에 들어온다.
    ///
    /// 원호가 짧으면 대수적 해가 반경을 과대추정하는 편향이 알려져 있는데, 여기서는 비드
    /// 옆면이 반원의 상당 부분을 덮으므로 문제가 되지 않는다. 실제로 추정된 반경이 실물
    /// 반경과 맞는지는 호출 측이 검증한다.
    /// </summary>
    public static Circle? Algebraic(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, List<int> indices)
    {
        if (indices.Count < 3) return null;

        // 데이터를 원점으로 옮겨 정규방정식의 조건수를 낮춘다. 거리가 mm 단위라 좌표가
        // 수백~수천이고, 그대로 두면 x²+y² 항이 10^6 규모가 되어 정밀도를 잃는다.
        double mx = 0, my = 0;
        foreach (var i in indices) { mx += xs[i]; my += ys[i]; }
        mx /= indices.Count;
        my /= indices.Count;

        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sz = 0, szx = 0, szy = 0;
        var n = (double)indices.Count;

        foreach (var i in indices)
        {
            var x = xs[i] - mx;
            var y = ys[i] - my;
            var z = x * x + y * y;

            sxx += x * x; sxy += x * y; syy += y * y;
            sx += x; sy += y;
            sz += z; szx += z * x; szy += z * y;
        }

        // [sxx sxy sx][A]   [-szx]
        // [sxy syy sy][B] = [-szy]
        // [sx  sy  n ][C]   [-sz ]
        if (!Solve3(
                sxx, sxy, sx,
                sxy, syy, sy,
                sx, sy, n,
                -szx, -szy, -sz,
                out var a, out var b, out var c))
        {
            return null;
        }

        var cx = -a / 2;
        var cy = -b / 2;
        var rSq = cx * cx + cy * cy - c;
        if (rSq <= 0) return null;

        return new Circle(cx + mx, cy + my, Math.Sqrt(rSq));
    }

    /// <summary>
    /// 반경을 <b>고정</b>하고 중심만 맞춘다. 가우스-뉴턴 2변수 최소화.
    ///
    /// <b>왜 반경을 미지수로 두면 안 되는가.</b> 반원 단면에서 정점 부근만 보이면 — 광택 금속은
    /// 정점이 정반사로, 뿌리가 스침각으로 날아가 실제로 그렇게 된다 — 곡률이 완만한 구간만 남아
    /// 반경이 원리적으로 잘 정해지지 않는다. 실측에서 실물 35mm 에 대해 회차마다 32.0~49.1mm 로
    /// 나왔고, 평균적으로 크게 치우쳤다(9회 중 6회가 40mm 이상).
    ///
    /// 그 흔들림이 그대로 정밀도가 된다. 정점 = 중심 + 반경이므로, 반경이 14mm 틀리면 정점이
    /// 14mm 틀린다. 실측 수직 편차 27.8mm 중 최대 성분이 깊이 방향 26.1mm 였던 이유다.
    ///
    /// <b>반경은 이미 아는 값이다.</b> 대상 제원이 확정되어 있으므로 자유도를 하나 줄이면
    /// 정점 높이가 훨씬 안정된다. 다만 자유 적합의 반경 추정은 <b>"이게 정말 코러게이션인가"</b>
    /// 를 가리는 안전장치로 여전히 필요하므로, 자유 적합으로 검증한 뒤 고정 반경으로 다시
    /// 맞추는 2단 구조를 쓴다.
    /// </summary>
    /// <param name="seed">시작 중심. 자유 적합 결과를 넣으면 몇 번 만에 수렴한다.</param>
    public static Circle FixedRadius(
        ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, int[] indices, double radius, in Circle seed)
    {
        double cx = seed.CenterX, cy = seed.CenterY;

        for (int iter = 0; iter < 30; iter++)
        {
            double a = 0, b = 0, c = 0, p = 0, q = 0;

            foreach (var i in indices)
            {
                var dx = xs[i] - cx;
                var dy = ys[i] - cy;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 1e-9) continue;

                // 잔차 r = d - R, 편미분 ∂r/∂cx = -dx/d, ∂r/∂cy = -dy/d
                var r = d - radius;
                var j0 = -dx / d;
                var j1 = -dy / d;

                a += j0 * j0; b += j0 * j1; c += j1 * j1;
                p += j0 * r; q += j1 * r;
            }

            var det = a * c - b * b;
            if (Math.Abs(det) < 1e-12) break;

            // JᵀJ Δ = -Jᵀr 의 2x2 해
            var dcx = (-p * c + q * b) / det;
            var dcy = (-a * q + b * p) / det;

            cx += dcx;
            cy += dcy;

            if (Math.Abs(dcx) + Math.Abs(dcy) < 1e-6) break;
        }

        return new Circle(cx, cy, radius);
    }

    /// <summary>점에서 원둘레까지의 거리(mm).</summary>
    public static double Residual(in Circle circle, double x, double y)
    {
        var dx = x - circle.CenterX;
        var dy = y - circle.CenterY;
        return Math.Abs(Math.Sqrt(dx * dx + dy * dy) - circle.Radius);
    }

    /// <summary>세 점의 외접원. 일직선이면 false.</summary>
    private static bool TryCircleFrom3(
        ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, int a, int b, int c, out Circle circle)
    {
        double ax = xs[a], ay = ys[a];
        double bx = xs[b], by = ys[b];
        double cx = xs[c], cy = ys[c];

        var d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(d) < 1e-9) { circle = default; return false; }

        var aSq = ax * ax + ay * ay;
        var bSq = bx * bx + by * by;
        var cSq = cx * cx + cy * cy;

        var ux = (aSq * (by - cy) + bSq * (cy - ay) + cSq * (ay - by)) / d;
        var uy = (aSq * (cx - bx) + bSq * (ax - cx) + cSq * (bx - ax)) / d;

        circle = new Circle(ux, uy, Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy)));
        return true;
    }

    /// <summary>3x3 선형계를 부분 피벗 가우스 소거로 푼다.</summary>
    private static bool Solve3(
        double m00, double m01, double m02,
        double m10, double m11, double m12,
        double m20, double m21, double m22,
        double r0, double r1, double r2,
        out double x0, out double x1, out double x2)
    {
        var m = new[,] { { m00, m01, m02, r0 }, { m10, m11, m12, r1 }, { m20, m21, m22, r2 } };
        x0 = x1 = x2 = 0;

        for (int col = 0; col < 3; col++)
        {
            var pivot = col;
            for (int row = col + 1; row < 3; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;

            if (Math.Abs(m[pivot, col]) < 1e-12) return false;

            if (pivot != col)
                for (int k = col; k < 4; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);

            for (int row = col + 1; row < 3; row++)
            {
                var f = m[row, col] / m[col, col];
                for (int k = col; k < 4; k++) m[row, k] -= f * m[col, k];
            }
        }

        x2 = m[2, 3] / m[2, 2];
        x1 = (m[1, 3] - m[1, 2] * x2) / m[1, 1];
        x0 = (m[0, 3] - m[0, 1] * x1 - m[0, 2] * x2) / m[0, 0];
        return true;
    }
}
