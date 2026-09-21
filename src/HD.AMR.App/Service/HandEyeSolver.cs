using HD.AMR.App.Communication;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service;

/// <summary>
/// 눈-손(hand-eye) 캘리브레이션 <c>AX = XB</c> 솔버 — Park &amp; Martin(1994) 방식.
///
/// 목적: 코봇 <b>플랜지 → 카메라 광학 프레임</b> 변환 <c>T_F_C</c> 산출. 카메라에 컨트롤러 TOOL 을
/// 정의할 필요가 없다(플랜지 = tool 0 = identity 기준).
///
/// 원리: 마커를 고정하고 <b>AMR 과 리프트를 세워 둔 채</b> 코봇 자세만 바꾸면
/// <c>T_B_F(k) · X · T_C_Q(k) = 상수</c> 이고, 두 자세의 상대 운동을 취하면
/// <c>A_i X = X B_i</c> 가 된다 (<c>A_i = T_B_F(j)⁻¹·T_B_F(k)</c>, <c>B_i = T_C_Q(j)·T_C_Q(k)⁻¹</c>).
/// AMR pose 가 식에서 완전히 소거되므로 <b>T_A_B 를 몰라도 풀린다</b> — 순환 의존이 없다.
///
/// 해법:
///   · 회전 — αᵢ=log(R_Aᵢ), βᵢ=log(R_Bᵢ) 로 두고 <c>M = Σ βᵢ αᵢᵀ</c>,
///     <c>R_X = (MᵀM)^(−1/2) Mᵀ</c>. 3×3 대칭 고유분해만 필요해
///     <see cref="Sym3Eigen"/> 을 그대로 쓴다(외부 선형대수 패키지 없음).
///   · 병진 — <c>(R_A − I)·t_X = R_X·t_B − t_A</c> 를 전 쌍에 대해 쌓아 3×3 정규방정식으로 최소자승.
///
/// ⚠ <b>회전축 다양성이 전부다.</b> 모든 상대 회전축이 평행하면 MᵀM 이 특이해져 해가 없다 —
/// 표본 개수를 아무리 늘려도 안 풀린다. 손목을 서로 평행하지 않은 2개 이상 축으로 돌려야 한다.
/// 순수 병진 자세는 회전 정보를 0 제공한다.
/// </summary>
public static class HandEyeSolver
{
    private const double Rad2Deg = 180.0 / Math.PI;

    public const int MinPoses = 3;
    private const int RecommendedPoses = 8;

    /// <summary>상대 회전이 이보다 작으면 잡음만 증폭하므로 쌍에서 제외(도).</summary>
    private const double MinPairRotationDeg = 5.0;

    /// <summary>
    /// 상대 회전이 이보다 크면 쌍에서 제외(도). 180° 에서는 축 부호가 <b>원리적으로 모호</b>하다
    /// (+π 회전과 −π 회전이 같은 행렬). Park–Martin 의 <c>M = Σβαᵀ</c> 는 α·β 를 <b>함께</b> 뒤집을 때만
    /// 불변이라, 부호를 A·B 각각 독립으로 복원하면 한쪽만 뒤집혀 그 쌍이 M 을 오염시킨다.
    /// 180° 쌍은 측도 0 의 퇴화 케이스이고 다른 쌍이 충분하므로 버리는 것이 옳다.
    /// </summary>
    private const double MaxPairRotationDeg = 170.0;

    /// <summary>MᵀM 최소 고유값의 상대 하한 — 회전축이 사실상 한 방향이면 여기서 걸린다.</summary>
    private const double AxisRankFloor = 1e-3;

    /// <summary>회전축 유효 펼침각 경고 임계(도).</summary>
    private const double AxisSpreadWarnDeg = 15.0;

    private const double RotResidualWarnDeg = 1.0;
    private const double TransResidualWarnMm = 5.0;

    /// <summary>
    /// 표본(플랜지 pose + 마커 pose 쌍)으로 <c>T_F_C</c> 를 산출한다.
    /// 오퍼레이터 입력 문제로는 예외를 던지지 않고 <see cref="HandEyeResult.Success"/>=false 로 반환한다.
    /// </summary>
    /// <param name="flangePoses">각 표본의 <c>T_B_F</c> — 플랜지 pose(BASE 기준, mm/도).</param>
    /// <param name="markerPoses">각 표본의 <c>T_C_Q</c> — 카메라 기준 마커 pose(mm/도).</param>
    public static HandEyeResult Solve(
        IReadOnlyList<double[]> flangePoses, IReadOnlyList<double[]> markerPoses)
    {
        if (flangePoses.Count != markerPoses.Count)
            return HandEyeResult.Fail("플랜지 pose 와 마커 pose 개수가 다릅니다.");

        int n = flangePoses.Count;
        if (n < MinPoses)
            return HandEyeResult.Fail(
                $"표본이 부족합니다({n}/{MinPoses}) — 코봇 자세를 바꿔가며 더 촬영하세요(권장 {RecommendedPoses}개 이상).");

        // ① 모든 자세 쌍에서 상대 운동을 만든다. 회전이 너무 작은 쌍은 잡음만 키우므로 버린다.
        var pairs = new List<(double[,] A, double[,] B, double[] Alpha, double[] Beta)>();
        for (var i = 0; i < n; i++)
        {
            var fi = FrameMath.PoseToMatrix(flangePoses[i]);
            var mi = FrameMath.PoseToMatrix(markerPoses[i]);
            for (var j = i + 1; j < n; j++)
            {
                var fj = FrameMath.PoseToMatrix(flangePoses[j]);
                var mj = FrameMath.PoseToMatrix(markerPoses[j]);

                var a = FrameMath.Multiply(FrameMath.Invert(fj), fi);   // A = T_B_F(j)⁻¹·T_B_F(i)
                var b = FrameMath.Multiply(mj, FrameMath.Invert(mi));   // B = T_C_Q(j)·T_C_Q(i)⁻¹

                var alpha = LogRotation(a);
                var beta = LogRotation(b);
                // 각도는 trace 로 직접 구한다 — log 벡터의 노름은 π 근처에서 신뢰할 수 없다.
                double angA = RotationAngleDeg(a);
                if (angA < MinPairRotationDeg || angA > MaxPairRotationDeg) continue;

                pairs.Add((a, b, alpha, beta));
            }
        }

        if (pairs.Count < 2)
            return HandEyeResult.Fail(
                $"유효한 상대 운동 쌍이 부족합니다({pairs.Count}) — 자세 간 상대 회전이 " +
                $"{MinPairRotationDeg:0}~{MaxPairRotationDeg:0}° 범위에 들어야 합니다" +
                $"(180° 부근은 회전축 부호가 원리적으로 모호해 제외됩니다). 손목을 다양하게 돌려가며 재촬영하세요.");

        // ② 회전: M = Σ βαᵀ, R_X = (MᵀM)^(−1/2)·Mᵀ
        var m = new double[3, 3];
        foreach (var (_, _, alpha, beta) in pairs)
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    m[r, c] += beta[r] * alpha[c];

        var mtm = new double[3, 3];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                for (var k = 0; k < 3; k++)
                    mtm[r, c] += m[k, r] * m[k, c];

        var (eig, vec) = Sym3Eigen.Jacobi(mtm);
        double lamMax = Math.Max(eig[0], 0), lamMin = Math.Max(eig[2], 0);

        // 회전축이 사실상 한 방향이면 MᵀM 이 특이하다 — 표본을 늘려도 해결되지 않는다.
        double axisRank = lamMax > 0 ? lamMin / lamMax : 0;
        double axisSpreadDeg = Math.Asin(Math.Clamp(Math.Sqrt(Math.Sqrt(axisRank)), 0, 1)) * Rad2Deg;
        if (axisRank < AxisRankFloor)
            return HandEyeResult.Fail(
                $"상대 회전축이 거의 한 방향입니다(축 다양성 {axisSpreadDeg:0.0}°) — " +
                "AX=XB 는 서로 평행하지 않은 2개 이상의 회전축이 있어야 풀립니다. " +
                "손목을 다른 축으로도 돌려가며 재촬영하세요.");

        // (MᵀM)^(−1/2) = V·diag(λ^(−1/2))·Vᵀ
        var invSqrt = new double[3, 3];
        for (var k = 0; k < 3; k++)
        {
            double s = 1.0 / Math.Sqrt(Math.Max(eig[k], 1e-300));
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    invSqrt[r, c] += s * vec[k][r] * vec[k][c];
        }

        var rx = new double[3, 3];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                for (var k = 0; k < 3; k++)
                    rx[r, c] += invSqrt[r, k] * m[c, k];   // (MᵀM)^(−1/2)·Mᵀ

        Orthonormalize(rx);

        // ③ 병진: (R_A − I)·t_X = R_X·t_B − t_A 를 전 쌍에 쌓아 최소자승.
        var ata = new double[3, 3];
        var atb = new double[3];
        foreach (var (a, b, _, _) in pairs)
        {
            var rowBase = new double[3, 3];
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    rowBase[r, c] = a[r, c] - (r == c ? 1.0 : 0.0);

            var tb = new[] { b[0, 3], b[1, 3], b[2, 3] };
            var rhs = new double[3];
            for (var r = 0; r < 3; r++)
                rhs[r] = rx[r, 0] * tb[0] + rx[r, 1] * tb[1] + rx[r, 2] * tb[2] - a[r, 3];

            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                {
                    for (var k = 0; k < 3; k++) ata[c, k] += rowBase[r, c] * rowBase[r, k];
                    atb[c] += rowBase[r, c] * rhs[r];
                }
            }
        }

        var tx = Solve3(ata, atb);
        if (tx is null)
            return HandEyeResult.Fail(
                "병진 해가 특이합니다 — 자세 간 회전이 더 다양해야 합니다(순수 병진 자세만으로는 풀리지 않습니다).");

        // ④ 결과 pose 와 잔차.
        var x = new double[4, 4];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++) x[r, c] = rx[r, c];
            x[r, 3] = tx[r];
        }
        x[3, 3] = 1.0;
        var pose = FrameMath.MatrixToPose(x);

        double rotSe = 0, transSe = 0, rotMax = 0, transMax = 0;
        foreach (var (a, b, _, _) in pairs)
        {
            // AX − XB 잔차
            var ax = FrameMath.Multiply(a, x);
            var xb = FrameMath.Multiply(x, b);
            var diff = FrameMath.Multiply(FrameMath.Invert(ax), xb);

            double ang = RotationAngleDeg(diff);
            double dt = Math.Sqrt(diff[0, 3] * diff[0, 3] + diff[1, 3] * diff[1, 3] + diff[2, 3] * diff[2, 3]);
            rotSe += ang * ang; transSe += dt * dt;
            rotMax = Math.Max(rotMax, ang); transMax = Math.Max(transMax, dt);
        }
        double rotRms = Math.Sqrt(rotSe / pairs.Count);
        double transRms = Math.Sqrt(transSe / pairs.Count);

        var warnings = new List<string>();
        if (n < RecommendedPoses)
            warnings.Add($"자세가 {n}개뿐입니다 — 권장 {RecommendedPoses}개 이상(회전축을 다양하게).");
        if (axisSpreadDeg < AxisSpreadWarnDeg)
            warnings.Add($"회전축 다양성이 낮습니다(유효 펼침 {axisSpreadDeg:0.0}° < {AxisSpreadWarnDeg:0}°) — " +
                         "손목을 서로 평행하지 않은 축으로 더 크게 돌리면 정밀도가 올라갑니다.");
        if (rotRms > RotResidualWarnDeg)
            warnings.Add($"회전 잔차 RMS {rotRms:0.00}° > {RotResidualWarnDeg:0.0}° — " +
                         "마커 검출 품질(크기·초점·기울기) 또는 자세 정지 여부를 확인하세요.");
        if (transRms > TransResidualWarnMm)
            warnings.Add($"병진 잔차 RMS {transRms:0.0}mm > {TransResidualWarnMm:0.0}mm — " +
                         "마커가 화면에서 너무 작거나 거리(깊이) 추정이 불안정할 수 있습니다.");

        return new HandEyeResult(
            Success: true, Error: null, PoseFC: pose, N: n, PairCount: pairs.Count,
            RotationRmsDeg: rotRms, RotationMaxDeg: rotMax,
            TranslationRmsMm: transRms, TranslationMaxMm: transMax,
            AxisSpreadDeg: axisSpreadDeg, Warnings: warnings);
    }

    // ── 보조 ────────────────────────────────────────────────────────
    /// <summary>회전행렬 → 축각 벡터(log map). 크기 = 회전각(rad), 방향 = 회전축.</summary>
    private static double[] LogRotation(double[,] t)
    {
        double trace = t[0, 0] + t[1, 1] + t[2, 2];
        double cos = Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0);
        double angle = Math.Acos(cos);

        if (angle < 1e-9) return new double[3];

        // 180° 근처는 sin→0 이라 대칭 성분에서 축을 뽑는다.
        if (Math.PI - angle < 1e-6)
        {
            var axis = new double[3];
            for (var i = 0; i < 3; i++) axis[i] = Math.Sqrt(Math.Max((t[i, i] + 1.0) / 2.0, 0));
            // 부호는 비대칭 성분에서 복원(전부 0 이면 임의 — 축 방향만 필요).
            if (t[2, 1] - t[1, 2] < 0) axis[0] = -axis[0];
            if (t[0, 2] - t[2, 0] < 0) axis[1] = -axis[1];
            if (t[1, 0] - t[0, 1] < 0) axis[2] = -axis[2];
            double len = Norm(axis);
            if (len < 1e-12) return new double[3];
            for (var i = 0; i < 3; i++) axis[i] = axis[i] / len * angle;
            return axis;
        }

        double k = angle / (2.0 * Math.Sin(angle));
        return new[]
        {
            k * (t[2, 1] - t[1, 2]),
            k * (t[0, 2] - t[2, 0]),
            k * (t[1, 0] - t[0, 1]),
        };
    }

    /// <summary>회전각(도) — trace 기반. π 근처에서도 안정적이다(log 벡터 노름과 달리).</summary>
    private static double RotationAngleDeg(double[,] t)
        => Math.Acos(Math.Clamp((t[0, 0] + t[1, 1] + t[2, 2] - 1.0) / 2.0, -1.0, 1.0)) * Rad2Deg;

    private static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    /// <summary>수치 오차로 어긋난 회전행렬을 그람-슈미트로 정규직교화.</summary>
    private static void Orthonormalize(double[,] r)
    {
        double[] c0 = { r[0, 0], r[1, 0], r[2, 0] };
        double[] c1 = { r[0, 1], r[1, 1], r[2, 1] };
        double n0 = Norm(c0);
        for (var i = 0; i < 3; i++) c0[i] /= n0;
        double d = c0[0] * c1[0] + c0[1] * c1[1] + c0[2] * c1[2];
        for (var i = 0; i < 3; i++) c1[i] -= d * c0[i];
        double n1 = Norm(c1);
        for (var i = 0; i < 3; i++) c1[i] /= n1;
        double[] c2 =
        {
            c0[1] * c1[2] - c0[2] * c1[1],
            c0[2] * c1[0] - c0[0] * c1[2],
            c0[0] * c1[1] - c0[1] * c1[0],
        };
        for (var i = 0; i < 3; i++) { r[i, 0] = c0[i]; r[i, 1] = c1[i]; r[i, 2] = c2[i]; }
    }

    /// <summary>3×3 선형계(부분 피벗 가우스). 특이하면 null.</summary>
    private static double[]? Solve3(double[,] a, double[] b)
    {
        var m = (double[,])a.Clone();
        var y = (double[])b.Clone();
        for (var col = 0; col < 3; col++)
        {
            int piv = col;
            double max = Math.Abs(m[col, col]);
            for (var r = col + 1; r < 3; r++)
                if (Math.Abs(m[r, col]) > max) { max = Math.Abs(m[r, col]); piv = r; }
            if (max < 1e-12) return null;
            if (piv != col)
            {
                for (var c = 0; c < 3; c++) (m[piv, c], m[col, c]) = (m[col, c], m[piv, c]);
                (y[piv], y[col]) = (y[col], y[piv]);
            }
            for (var r = 0; r < 3; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                for (var c = col; c < 3; c++) m[r, c] -= f * m[col, c];
                y[r] -= f * y[col];
            }
        }
        return new[] { y[0] / m[0, 0], y[1] / m[1, 1], y[2] / m[2, 2] };
    }
}
