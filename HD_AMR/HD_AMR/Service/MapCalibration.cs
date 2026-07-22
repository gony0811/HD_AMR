using HD_AMR.Communication;

namespace HD_AMR.Service;

/// <summary>
/// 맵 정합(2D 강체)·좌표 변환·장착 오프셋(T_A_B) 측정 수학.
///
/// 도면 G(mm)와 SLAM 맵 W(mm) 사이의 2D 강체변환 <c>T_W_G = (θ, tx, ty)</c> 를 대응점으로
/// 최소자승(2D Kabsch) 추정하고(<see cref="SolveRigid2D"/>), 코봇 BASE 점을 AMR pose·장착
/// 오프셋으로 맵 좌표로 환산한다(<see cref="BasePointToMap"/>). 또한 고정점을 여러 AMR 자세에서
/// 터치한 표본으로 장착 오프셋의 평면 성분(rz, tx, ty)을 추정한다(<see cref="SolveMount2D"/>).
/// 회전 규약은 <see cref="FrameMath"/>(ZYX RPY, 도)와 동일, 스케일 1 가정.
/// </summary>
public static class MapCalibration
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    // ── 맵↔도면 2D 강체 정합 ─────────────────────────────────────────
    /// <summary>대응점 (g_i → w_i)로 2D 강체변환 추정. w ≈ R(θ)·g + t.
    /// 반환 (θ[도], tx, ty, RMS[mm]). 점 개수 불일치/2개 미만이면 예외.</summary>
    public static (double ThetaDeg, double Tx, double Ty, double RmsMm) SolveRigid2D(
        IReadOnlyList<(double X, double Y)> g, IReadOnlyList<(double X, double Y)> w)
    {
        if (g.Count != w.Count) throw new ArgumentException("대응점 개수가 서로 다릅니다.");
        int n = g.Count;
        if (n < 2) throw new InvalidOperationException("2D 정합에는 대응점이 최소 2개 필요합니다.");

        double gcx = 0, gcy = 0, wcx = 0, wcy = 0;
        for (int i = 0; i < n; i++) { gcx += g[i].X; gcy += g[i].Y; wcx += w[i].X; wcy += w[i].Y; }
        gcx /= n; gcy /= n; wcx /= n; wcy /= n;

        double a = 0, b = 0;
        for (int i = 0; i < n; i++)
        {
            double dgx = g[i].X - gcx, dgy = g[i].Y - gcy;
            double dwx = w[i].X - wcx, dwy = w[i].Y - wcy;
            a += dgx * dwy - dgy * dwx; // sin 성분(외적)
            b += dgx * dwx + dgy * dwy; // cos 성분(내적)
        }
        double theta = Math.Atan2(a, b);
        double c = Math.Cos(theta), s = Math.Sin(theta);
        double tx = wcx - (c * gcx - s * gcy);
        double ty = wcy - (s * gcx + c * gcy);

        double se = 0;
        for (int i = 0; i < n; i++)
        {
            double px = c * g[i].X - s * g[i].Y + tx;
            double py = s * g[i].X + c * g[i].Y + ty;
            se += (px - w[i].X) * (px - w[i].X) + (py - w[i].Y) * (py - w[i].Y);
        }
        return (theta * Rad2Deg, tx, ty, Math.Sqrt(se / n));
    }

    /// <summary>도면 점(gx,gy)을 정합값으로 맵 점(mm)으로 변환. w = R(θ)·g + t.</summary>
    public static (double X, double Y) Apply(double thetaDeg, double tx, double ty, double gx, double gy)
    {
        double th = thetaDeg * Deg2Rad;
        double c = Math.Cos(th), s = Math.Sin(th);
        return (c * gx - s * gy + tx, s * gx + c * gy + ty);
    }

    // ── 좌표 변환 ────────────────────────────────────────────────────
    /// <summary>코봇 BASE 좌표의 점 <paramref name="pBase"/>(mm)을 맵 좌표(mm)로 환산.
    /// amrPose = 맵 기준 AMR 차체 pose [x,y,z,rx,ry,rz](mm/도), mount = T_A_B [x,y,z,rx,ry,rz](mm/도).
    /// p_W = T_W_A · T_A_B · p_B. 맵 바닥 평면의 (x,y)만 반환.</summary>
    public static (double X, double Y) BasePointToMap(double[] amrPose, double[] mount, double[] pBase)
    {
        var tWA = FrameMath.PoseToMatrix(amrPose);
        var tAB = FrameMath.PoseToMatrix(mount);
        var tWB = FrameMath.Multiply(tWA, tAB);
        double x = tWB[0, 0] * pBase[0] + tWB[0, 1] * pBase[1] + tWB[0, 2] * pBase[2] + tWB[0, 3];
        double y = tWB[1, 0] * pBase[0] + tWB[1, 1] * pBase[1] + tWB[1, 2] * pBase[2] + tWB[1, 3];
        return (x, y);
    }

    /// <summary>AMR pose(맵 기준, m/rad)를 [x,y,z,rx,ry,rz](mm/도)로. z=0, roll=pitch=0.</summary>
    public static double[] AmrPoseToMmDeg(double xMeters, double yMeters, double angleRad)
        => new[] { xMeters * 1000.0, yMeters * 1000.0, 0.0, 0.0, 0.0, angleRad * Rad2Deg };

    // ── 장착 오프셋(T_A_B) 평면 측정 ─────────────────────────────────
    /// <summary>
    /// 평면(2D) 장착 캘리브레이션. 고정 세계점을 여러 AMR 자세에서 코봇으로 터치한 표본으로
    /// 장착 오프셋의 평면 성분(rz=φ, tx, ty)을 최소자승 추정한다.
    /// 표본 = (AMR 맵 pose x,y[mm]·yaw[도], 코봇 BASE 기준 터치점 xy[mm]).
    /// 모델: 모든 k 공통 세계점 q = R(θ_k)·(R(φ)·pB_k + t) + c_k. (rx=ry=0, z는 미관측)
    /// φ는 1D 탐색, 내부 미지수 (t, q)는 선형 최소자승(정규방정식 4×4). 반환 (φ[도], tx, ty, RMS[mm], N).
    /// AMR yaw 다양성이 부족하면 해가 특이해질 수 있다(예외).
    /// </summary>
    public static (double PhiDeg, double Tx, double Ty, double RmsMm, int N) SolveMount2D(
        IReadOnlyList<(double AmrXmm, double AmrYmm, double AmrYawDeg, double Bx, double By)> s)
    {
        int n = s.Count;
        if (n < 3) throw new InvalidOperationException("장착 측정에는 표본이 최소 3개 필요합니다(AMR yaw를 크게 바꿔가며).");

        double best = double.MaxValue, bestPhi = 0, bestTx = 0, bestTy = 0;
        for (double phi = -180; phi < 180; phi += 2.0)
        {
            var (rms, tx, ty) = SolveLinearForPhi(s, phi);
            if (rms < best) { best = rms; bestPhi = phi; bestTx = tx; bestTy = ty; }
        }
        for (double phi = bestPhi - 2.0; phi <= bestPhi + 2.0; phi += 0.05)
        {
            var (rms, tx, ty) = SolveLinearForPhi(s, phi);
            if (rms < best) { best = rms; bestPhi = phi; bestTx = tx; bestTy = ty; }
        }
        double p = bestPhi;
        while (p > 180) p -= 360;
        while (p <= -180) p += 360;
        return (p, bestTx, bestTy, best, n);
    }

    /// <summary>φ 고정 시 (tx,ty,qx,qy)를 선형 최소자승으로 풀고 RMS 반환.</summary>
    private static (double Rms, double Tx, double Ty) SolveLinearForPhi(
        IReadOnlyList<(double AmrXmm, double AmrYmm, double AmrYawDeg, double Bx, double By)> s, double phiDeg)
    {
        double phi = phiDeg * Deg2Rad;
        double cphi = Math.Cos(phi), sphi = Math.Sin(phi);
        int n = s.Count;

        // 미지수 X=[tx,ty,qx,qy]. 표본별 2행:
        //   cosθ·tx − sinθ·ty − qx = −skx
        //   sinθ·tx + cosθ·ty − qy = −sky   (sk = R(θ)·R(φ)·pB + c)
        var ata = new double[4, 4];
        var atb = new double[4];
        for (int k = 0; k < n; k++)
        {
            double th = s[k].AmrYawDeg * Deg2Rad;
            double cth = Math.Cos(th), sth = Math.Sin(th);
            double rx = cphi * s[k].Bx - sphi * s[k].By;
            double ry = sphi * s[k].Bx + cphi * s[k].By;
            double skx = cth * rx - sth * ry + s[k].AmrXmm;
            double sky = sth * rx + cth * ry + s[k].AmrYmm;
            AccumRow(ata, atb, new[] { cth, -sth, -1.0, 0.0 }, -skx);
            AccumRow(ata, atb, new[] { sth, cth, 0.0, -1.0 }, -sky);
        }

        var x = Solve4(ata, atb);
        double tx = x[0], ty = x[1], qx = x[2], qy = x[3];

        double se = 0;
        for (int k = 0; k < n; k++)
        {
            double th = s[k].AmrYawDeg * Deg2Rad;
            double cth = Math.Cos(th), sth = Math.Sin(th);
            double rx = cphi * s[k].Bx - sphi * s[k].By;
            double ry = sphi * s[k].Bx + cphi * s[k].By;
            double skx = cth * rx - sth * ry + s[k].AmrXmm;
            double sky = sth * rx + cth * ry + s[k].AmrYmm;
            double e1 = cth * tx - sth * ty - qx + skx;
            double e2 = sth * tx + cth * ty - qy + sky;
            se += e1 * e1 + e2 * e2;
        }
        return (Math.Sqrt(se / n), tx, ty);
    }

    private static void AccumRow(double[,] ata, double[] atb, double[] row, double rhs)
    {
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++) ata[i, j] += row[i] * row[j];
            atb[i] += row[i] * rhs;
        }
    }

    /// <summary>4×4 선형계 A·x=b (부분 피벗 가우스 소거).</summary>
    private static double[] Solve4(double[,] a, double[] b)
    {
        const int n = 4;
        var m = (double[,])a.Clone();
        var y = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            double max = Math.Abs(m[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = Math.Abs(m[r, col]);
                if (v > max) { max = v; piv = r; }
            }
            if (max < 1e-12)
                throw new InvalidOperationException("장착 해가 특이합니다 — AMR yaw/위치를 더 다양하게 표본하세요.");
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (m[piv, c], m[col, c]) = (m[col, c], m[piv, c]);
                (y[piv], y[col]) = (y[col], y[piv]);
            }
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                for (int c = col; c < n; c++) m[r, c] -= f * m[col, c];
                y[r] -= f * y[col];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = y[i] / m[i, i];
        return x;
    }
}
