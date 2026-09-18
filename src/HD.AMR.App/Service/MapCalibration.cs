using HD.AMR.App.Communication;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service;

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
        var r = SolveMount2DDetailed(s);
        return (r.PhiDeg, r.Tx, r.Ty, r.RmsMm, r.N);
    }

    /// <summary>
    /// <see cref="SolveMount2D"/> 와 동일한 계산이되 내부에서만 쓰고 버리던 추정 고정점 q(=qx,qy)까지 반환한다.
    /// 6-DoF 경로(<see cref="SolveMount3D"/>)가 표본별 xy 잔차를 계산하려면 q 가 필요하다.
    /// ⚠ φ 는 0.05° 격자에 양자화된다 — ‖t‖≈700mm 에서 최대 0.31mm 상당이며 SLAM 잡음(10~20mm) 대비 무시 가능.
    /// </summary>
    public static (double PhiDeg, double Tx, double Ty, double Qx, double Qy, double RmsMm, int N)
        SolveMount2DDetailed(
            IReadOnlyList<(double AmrXmm, double AmrYmm, double AmrYawDeg, double Bx, double By)> s)
    {
        int n = s.Count;
        if (n < 3) throw new InvalidOperationException("장착 측정에는 표본이 최소 3개 필요합니다(AMR yaw를 크게 바꿔가며).");

        double best = double.MaxValue, bestPhi = 0, bestTx = 0, bestTy = 0, bestQx = 0, bestQy = 0;
        for (double phi = -180; phi < 180; phi += 2.0)
        {
            var (rms, tx, ty, qx, qy) = SolveLinearForPhi(s, phi);
            if (rms < best) { best = rms; bestPhi = phi; bestTx = tx; bestTy = ty; bestQx = qx; bestQy = qy; }
        }
        for (double phi = bestPhi - 2.0; phi <= bestPhi + 2.0; phi += 0.05)
        {
            var (rms, tx, ty, qx, qy) = SolveLinearForPhi(s, phi);
            if (rms < best) { best = rms; bestPhi = phi; bestTx = tx; bestTy = ty; bestQx = qx; bestQy = qy; }
        }
        double p = bestPhi;
        while (p > 180) p -= 360;
        while (p <= -180) p += 360;
        return (p, bestTx, bestTy, bestQx, bestQy, best, n);
    }

    /// <summary>φ 고정 시 (tx,ty,qx,qy)를 선형 최소자승으로 풀고 RMS 반환.</summary>
    private static (double Rms, double Tx, double Ty, double Qx, double Qy) SolveLinearForPhi(
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
        return (Math.Sqrt(se / n), tx, ty, qx, qy);
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

    // ── 장착 오프셋(T_A_B) 6-DoF 측정 ───────────────────────────────
    //
    // 모델: 고정 세계점 q 를 여러 AMR 자세에서 코봇 TCP 로 터치 → q = T_W_A(k)·T_A_B·p_k.
    // T_W_A 는 평면(yaw θ_k, z=0, roll=pitch=0)이므로 R = Rz(rz)·M (M = Ry(ry)·Rx(rx)) 로 쪼개면
    // z 식이 분리된다:  q_z = r3·p_k + t_z,  r3 = R의 3행 = (−sin ry, cos ry·sin rx, cos ry·cos rx).
    //   · 모든 p_k 는 BASE 에서 법선 r3 인 한 평면 위 → 총최소자승 평면 적합으로 rx·ry 관측 가능.
    //   · Rz 는 z 를 섞지 않으므로 xy 식은 정확히 기존 평면 문제 → 터치점을 M 으로 선회전해
    //     SolveMount2DDetailed 에 위임하면 rz·tx·ty 가 나온다.
    //   · t_z 는 언제나 미지의 q_z 와 d = q_z − t_z 로만 묶인다 → 외부 입력 1개(타깃 높이) 필요.
    //
    // 2단 분리가 결합 최소자승의 근사인 이유(의도적): xy 잔차도 rx·ry 에 의존하므로 J 는 엄밀히
    // 분리되지 않는다. 그러나 rx·ry 에 대한 Fisher 정보는 z 채널(감도 = 면내 펼침 ~300mm/rad,
    // 잡음 = 터치 ~1mm)이 xy 채널(감도 = |p_z| ~500mm/rad, 잡음 = SLAM ~15mm)보다 2~3차수 크다.
    // 따라서 순차 추정이 결합 최적해와 1% 미만 차이다 — Gauss-Newton 보정을 붙일 이유가 없다.

    private const int MountMinSamples = 3;
    private const int MountRecommendedSamples = 5;
    private const double MountYawSpanFailDeg = 15.0;
    private const double MountYawSpanWarnDeg = 90.0;
    private const double MountPlaneSpanFailMm = 50.0;
    private const double MountPlaneSpanWarnMm = 300.0;
    private const double MountUprightMinRz = 0.05;          // |r3_z| 하한(≈87° 기울기) — 자료 무결성 가드
    private const double MountTouchSigmaFloorMm = 1.0;      // n=3 에서 평면 잔차가 0 이 되는 것을 보정
    private const double MountTiltSigmaWarnDeg = 0.20;
    private const double MountPlaneRmsWarnMm = 2.0;
    private const double MountPlaneResidualWarnMm = 3.0;
    private const double MountPlanarRmsWarnMm = 25.0;
    private const double MountPlanarResidualWarnMm = 40.0;
    private const double MountPlaneOffsetSaneMm = 2000.0;

    /// <summary>
    /// 6-DoF 장착 캘리브레이션(T_A_B). 고정 세계점 1개를 여러 AMR 자세에서 코봇 TCP 로 터치한
    /// 표본으로 (rx, ry, rz, tx, ty) 를 산출한다. tz 는 원리상 관측 불가 —
    /// <paramref name="targetZmm"/>(AMR 차체 원점 기준 타깃 높이, mm; 바닥 타깃이면 음수)를 주면
    /// tz = q_z − d 로 닫고, 주지 않으면 tz=0 · TzObserved=false 로 두고 d 만 보고한다.
    ///
    /// 표본 다양성 요구는 <b>두 가지가 서로 다르다</b>:
    ///   · rx·ry(평면 적합) — 터치점이 평면 안에서 비공선일 것. yaw 를 안 바꾸고 정차 위치만
    ///     여러 방향으로 옮겨도 충족된다.
    ///   · rz·tx·ty(평면 문제) — AMR yaw 다양성이 필요하다.
    /// 그래서 PlaneSpanMm 과 YawSpanDeg 를 따로 보고하고 경고 문구도 따로 낸다.
    ///
    /// 오퍼레이터 입력 문제로는 <b>예외를 던지지 않는다</b> — Success=false + 한국어 Error 로 반환.
    /// 기대 정밀도: rx·ry ±0.05~0.2°, rz ±0.5~1.5°, tx·ty ±10~25mm(SLAM 지배, √N 개선),
    /// tz 는 입력 높이 정확도와 1:1.
    /// </summary>
    /// <param name="s">표본 — AMR 맵 pose(x,y[mm], yaw[도]) + 코봇 BASE 기준 터치점(mm).</param>
    /// <param name="targetZmm">타깃 높이 q_z. null 이면 tz 미관측으로 보고.</param>
    /// <param name="currentMount">현재 저장된 T_A_B — 주면 상대 변환으로 변화량을 함께 보고.</param>
    public static MountCalibrationResult SolveMount3D(
        IReadOnlyList<(double AmrXmm, double AmrYmm, double AmrYawDeg,
                       double Bx, double By, double Bz)> s,
        double? targetZmm = null,
        double[]? currentMount = null)
    {
        int n = s.Count;
        if (n < MountMinSamples)
            return MountCalibrationResult.Fail(
                $"표본이 부족합니다({n}/{MountMinSamples}) — AMR 자세를 바꿔가며 같은 점을 더 터치하세요.");

        // ① yaw 다양성 게이트 — SolveMount2D 의 특이 예외보다 먼저, 조치 가능한 메시지로.
        double yawSpan = CircularSpanDeg(s.Select(x => x.AmrYawDeg).ToList());
        if (yawSpan < MountYawSpanFailDeg)
            return MountCalibrationResult.Fail(
                $"AMR yaw 다양성이 부족합니다(펼침 {yawSpan:0.#}° < {MountYawSpanFailDeg:0}°) — " +
                "최소 90° 이상 벌려 재표본하세요.");

        // ② 터치점 평면 총최소자승 적합.
        double mx = s.Average(x => x.Bx), my = s.Average(x => x.By), mz = s.Average(x => x.Bz);
        double scale = 0;
        foreach (var k in s)
        {
            double dx = k.Bx - mx, dy = k.By - my, dz = k.Bz - mz;
            scale = Math.Max(scale, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        if (scale < 1e-9)
            return MountCalibrationResult.Fail("터치점이 모두 같은 위치입니다 — AMR 자세를 바꿔 재표본하세요.");

        // 정규화 산란행렬(성분 O(1) 유지 — 고유값은 scale² 로 되돌린다).
        var scatter = new double[3, 3];
        foreach (var k in s)
        {
            double dx = (k.Bx - mx) / scale, dy = (k.By - my) / scale, dz = (k.Bz - mz) / scale;
            scatter[0, 0] += dx * dx; scatter[0, 1] += dx * dy; scatter[0, 2] += dx * dz;
            scatter[1, 1] += dy * dy; scatter[1, 2] += dy * dz; scatter[2, 2] += dz * dz;
        }
        scatter[1, 0] = scatter[0, 1]; scatter[2, 0] = scatter[0, 2]; scatter[2, 1] = scatter[1, 2];

        var (eig, vec) = Sym3Eigen.Jacobi(scatter);
        double lambda2 = Math.Max(eig[1], 0) * scale * scale;
        double lambda3 = Math.Max(eig[2], 0) * scale * scale;
        var r3 = vec[2];
        if (r3[2] < 0) r3 = new[] { -r3[0], -r3[1], -r3[2] };   // 코봇 BASE 정립 가정 → r3_z > 0

        double planeSpanMm = Math.Sqrt(lambda2 / n);
        double planeRmsMm = Math.Sqrt(lambda3 / n);

        // λ₃ 로는 게이트하지 않는다 — rx=ry=0 인 정상 장착에서 Bz 가 상수면 λ₃=0 이 정답이다.
        // 진짜 퇴화는 "터치점 공선"이고 그것은 λ₂ 가 잡는다.
        if (planeSpanMm < MountPlaneSpanFailMm)
            return MountCalibrationResult.Fail(
                $"터치점이 거의 한 직선 위에 있습니다(약축 펼침 {planeSpanMm:0.#}mm) — " +
                "AMR 정차 위치를 서로 다른 방향으로 벌려 재표본하세요.");
        if (Math.Abs(r3[2]) < MountUprightMinRz)
            return MountCalibrationResult.Fail(
                "코봇 BASE 가 거의 수평으로 산출되었습니다 — 표본 또는 터치점 Z 값을 확인하세요.");

        // ③ 기울기 복원.
        double ryDeg = -Math.Asin(Math.Clamp(r3[0], -1.0, 1.0)) * Rad2Deg;
        double rxDeg = Math.Atan2(r3[1], r3[2]) * Rad2Deg;
        double d = r3[0] * mx + r3[1] * my + r3[2] * mz;

        // ④ M = Ry(ry)·Rx(rx) 로 선회전 — 3×3 곱 없이 두 줄로.
        double sa = Math.Sin(rxDeg * Deg2Rad), ca = Math.Cos(rxDeg * Deg2Rad);
        double sb = Math.Sin(ryDeg * Deg2Rad), cb = Math.Cos(ryDeg * Deg2Rad);
        var planar = new List<(double, double, double, double, double)>(n);
        var ux = new double[n];
        var uy = new double[n];
        for (var i = 0; i < n; i++)
        {
            var k = s[i];
            ux[i] = cb * k.Bx + sb * sa * k.By + sb * ca * k.Bz;
            uy[i] = ca * k.By - sa * k.Bz;
            planar.Add((k.AmrXmm, k.AmrYmm, k.AmrYawDeg, ux[i], uy[i]));
        }

        // ⑤ 평면 문제 위임(기존 검증된 경로). 특이해는 결과로 변환한다.
        double rzDeg, tx, ty, qx, qy, planarRmsMm;
        try
        {
            var r = SolveMount2DDetailed(planar);
            rzDeg = r.PhiDeg; tx = r.Tx; ty = r.Ty; qx = r.Qx; qy = r.Qy; planarRmsMm = r.RmsMm;
        }
        catch (InvalidOperationException ex)
        {
            return MountCalibrationResult.Fail(ex.Message);
        }

        // ⑥ tz 닫기.
        bool tzObserved = targetZmm.HasValue;
        double tz = tzObserved ? targetZmm!.Value - d : 0.0;
        var mountPose = new[] { tx, ty, tz, rxDeg, ryDeg, rzDeg };

        // ⑦ 표본별 잔차.
        var planeRes = new double[n];
        var planarRes = new double[n];
        var res = new double[n];
        double crz = Math.Cos(rzDeg * Deg2Rad), srz = Math.Sin(rzDeg * Deg2Rad);
        for (var i = 0; i < n; i++)
        {
            var k = s[i];
            planeRes[i] = (r3[0] * k.Bx + r3[1] * k.By + r3[2] * k.Bz) - d;

            double vx = crz * ux[i] - srz * uy[i] + tx;
            double vy = srz * ux[i] + crz * uy[i] + ty;
            double th = k.AmrYawDeg * Deg2Rad;
            double cth = Math.Cos(th), sth = Math.Sin(th);
            double ex = cth * vx - sth * vy + k.AmrXmm - qx;
            double ey = sth * vx + cth * vy + k.AmrYmm - qy;
            planarRes[i] = Math.Sqrt(ex * ex + ey * ey);
            res[i] = Math.Sqrt(planarRes[i] * planarRes[i] + planeRes[i] * planeRes[i]);
        }
        double rmsMm = Math.Sqrt(res.Sum(v => v * v) / n);
        double maxAbsMm = res.Max();

        // ⑧ 기울기 정밀도. n=3 이면 평면 적합이 정확해 잔차가 0 → 터치 반복도를 하한으로 둔다.
        double planeSigma = n > 3 ? Math.Sqrt(planeRes.Sum(v => v * v) / (n - 3)) : MountTouchSigmaFloorMm;
        double tiltSigmaDeg = Math.Atan2(Math.Max(planeSigma, MountTouchSigmaFloorMm),
                                         planeSpanMm * Math.Sqrt(n)) * Rad2Deg;

        // ⑨ 현재값 대비 변화량 — 성분 차가 아니라 상대 변환으로(±180° 랩 문제 회피).
        double[]? deltaPose = null;
        double deltaPosMm = 0, deltaAngleDeg = 0;
        if (currentMount is { Length: 6 })
        {
            var rel = FrameMath.Multiply(FrameMath.Invert(FrameMath.PoseToMatrix(currentMount)),
                                         FrameMath.PoseToMatrix(mountPose));
            deltaPose = FrameMath.MatrixToPose(rel);
            deltaPosMm = Math.Sqrt(rel[0, 3] * rel[0, 3] + rel[1, 3] * rel[1, 3] + rel[2, 3] * rel[2, 3]);
            double trace = rel[0, 0] + rel[1, 1] + rel[2, 2];
            deltaAngleDeg = Math.Acos(Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0)) * Rad2Deg;
        }

        // ⑩ 경고 — 순서 고정(테스트 가능하도록).
        var warnings = new List<string>();
        if (n < MountRecommendedSamples)
            warnings.Add($"표본이 {n}개뿐입니다 — 3~4개면 잔차가 0에 가깝게 나와도 정확도를 보장하지 않습니다" +
                         $"(권장 {MountRecommendedSamples}개 이상).");
        if (yawSpan < MountYawSpanWarnDeg)
        {
            double amp = 1.0 / (2.0 * Math.Max(Math.Sin(yawSpan / 2.0 * Deg2Rad), 1e-6));
            warnings.Add($"AMR yaw 펼침 {yawSpan:0.#}° < {MountYawSpanWarnDeg:0}° — " +
                         $"tx/ty 에 SLAM 잡음이 약 {amp:0.0}배 증폭됩니다. 90° 이상 벌리세요.");
        }
        if (planeSpanMm < MountPlaneSpanWarnMm)
            warnings.Add($"터치점 펼침이 좁습니다({planeSpanMm:0}mm < {MountPlaneSpanWarnMm:0}mm) — " +
                         "rx/ry 정밀도가 떨어집니다. AMR 정차 위치를 여러 방향으로 벌리세요.");
        if (tiltSigmaDeg > MountTiltSigmaWarnDeg)
            warnings.Add($"장착 기울기 추정 오차 ≈ ±{tiltSigmaDeg:0.00}° " +
                         $"(도달거리 1m 에서 약 {tiltSigmaDeg * 17.45:0.#}mm) — 표본을 늘리거나 더 넓게 벌리세요.");
        double bzSpread = s.Max(x => x.Bz) - s.Min(x => x.Bz);
        if (bzSpread < 1e-9)
            warnings.Add("터치점 Z 가 모두 동일합니다 — rx/ry 는 0 으로 산출됩니다. " +
                         "코봇 BASE Z 값이 실제로 기록되었는지 확인하세요.");
        if (planeRmsMm > MountPlaneRmsWarnMm)
            warnings.Add($"평면 잔차 RMS {planeRmsMm:0.0}mm > {MountPlaneRmsWarnMm:0.0}mm — " +
                         "터치 정확도 또는 AMR 차체 기울기(바닥 요철)를 의심하세요.");
        for (var i = 0; i < n; i++)
            if (Math.Abs(planeRes[i]) > MountPlaneResidualWarnMm)
                warnings.Add($"표본 #{i + 1} 평면 잔차 {planeRes[i]:+0.0;-0.0}mm > {MountPlaneResidualWarnMm:0.0}mm — " +
                             "터치 접촉 상태를 재확인하거나 삭제하세요.");
        if (planarRmsMm > MountPlanarRmsWarnMm)
            warnings.Add($"평면내 잔차 RMS {planarRmsMm:0}mm > {MountPlanarRmsWarnMm:0}mm — " +
                         "SLAM pose 잡음 상한을 넘었습니다. 정차 후 pose 가 안정된 뒤 캡처했는지 확인하세요.");
        for (var i = 0; i < n; i++)
            if (planarRes[i] > MountPlanarResidualWarnMm)
                warnings.Add($"표본 #{i + 1} 평면내 잔차 {planarRes[i]:0}mm > {MountPlanarResidualWarnMm:0}mm — " +
                             "AMR pose 오독 또는 오터치 의심.");
        if (!tzObserved)
            warnings.Add($"타깃 높이 미입력 — Z 는 0 으로 두었습니다. d={d:0.0}mm 만 산출되었으므로 " +
                         "높이 입력 시 tz = q_z − d 로 즉시 확정됩니다.");
        else if (Math.Abs(d) > MountPlaneOffsetSaneMm)
            warnings.Add($"타깃 평면까지 거리 {d:0}mm 가 비정상입니다(±2m 초과) — " +
                         "타깃 높이 입력값의 기준(AMR 차체 원점)을 확인하세요.");

        return new MountCalibrationResult(
            Success: true,
            Error: null,
            MountPose: mountPose,
            TzObserved: tzObserved,
            TargetZmm: targetZmm ?? 0.0,
            PlaneOffsetDmm: d,
            WorldPointMm: new[] { qx, qy, tzObserved ? targetZmm!.Value : 0.0 },
            ResidualsMm: res,
            PlaneResidualsMm: planeRes,
            PlanarResidualsMm: planarRes,
            RmsMm: rmsMm,
            MaxAbsMm: maxAbsMm,
            PlaneRmsMm: planeRmsMm,
            PlanarRmsMm: planarRmsMm,
            PlaneSpanMm: planeSpanMm,
            PlaneLambdaMinMm2: lambda3,
            TiltSigmaDeg: tiltSigmaDeg,
            YawSpanDeg: yawSpan,
            N: n,
            DeltaPose: deltaPose,
            DeltaPosMm: deltaPosMm,
            DeltaAngleDeg: deltaAngleDeg,
            Warnings: warnings);
    }

    /// <summary>각도를 (−180, 180] 로 정규화.</summary>
    public static double NormalizeDeg(double deg)
    {
        double d = deg % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d <= -180.0) d += 360.0;
        return d;
    }

    /// <summary>각도 목록의 원형 펼침(도) = 360 − 최대 인접 공백. 1개 이하면 0.</summary>
    public static double CircularSpanDeg(IReadOnlyList<double> anglesDeg)
    {
        int n = anglesDeg.Count;
        if (n < 2) return 0;
        var a = anglesDeg.Select(v => ((v % 360.0) + 360.0) % 360.0).OrderBy(v => v).ToList();
        double maxGap = a[0] + 360.0 - a[n - 1];
        for (var i = 1; i < n; i++) maxGap = Math.Max(maxGap, a[i] - a[i - 1]);
        return 360.0 - maxGap;
    }
}
