namespace HD.AMR.App.Service;

/// <summary>
/// 대칭 3×3 고유분해(순환 Jacobi). 외부 선형대수 패키지 없이 동작하며 고유값 내림차순과
/// 정규직교 고유벡터를 반환한다. 평면 총최소자승 적합(<see cref="MapCalibration.SolveMount3D"/>)용.
///
/// 폐형식(특성다항식) 대신 Jacobi 를 쓰는 이유: 고유<b>값</b>은 폐형식으로도 안정적이지만
/// 고유<b>벡터</b>는 (S − λI) 의 영공간을 외적으로 뽑아야 하고, 그 정확도가 λ₂ ≈ λ₃ 인 구간
/// — 즉 우리가 반드시 검출해야 하는 <b>터치점 공선 퇴화</b> 구간 — 에서 무너진다.
/// Jacobi 는 V 의 직교성을 구성적으로 보존하므로 퇴화가 잘못된 법선이 아니라 작은 λ₂ 로 정직하게 드러난다.
///
/// 결정론성: 삼각함수 없는 회전식(Math.Sqrt/Abs 만 사용), 고정 피벗 순서, 고정 스윕 상한 →
/// x64·Apple Silicon 에서 동일 결과. (.NET 은 FMA 자동 축약을 하지 않는다.)
/// </summary>
public static class Sym3Eigen
{
    private const int MaxSweeps = 24;

    /// <summary>
    /// A = V·diag(λ)·Vᵀ 로 분해. <c>Values</c> 는 내림차순, <c>Vectors[i]</c> 는 <c>Values[i]</c> 의 단위 고유벡터.
    /// 입력은 방어적으로 대칭화한다.
    /// </summary>
    public static (double[] Values, double[][] Vectors) Jacobi(double[,] a)
    {
        // 대칭화 — 호출자가 누적한 산란행렬의 부동소수 비대칭을 제거.
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = 0.5 * (a[i, j] + a[j, i]);

        // V = I (열 벡터가 고유벡터).
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (var sweep = 0; sweep < MaxSweeps; sweep++)
        {
            double off = m[0, 1] * m[0, 1] + m[0, 2] * m[0, 2] + m[1, 2] * m[1, 2];
            double diag = m[0, 0] * m[0, 0] + m[1, 1] * m[1, 1] + m[2, 2] * m[2, 2];
            if (off <= 1e-30 * (diag + 1e-300)) break;

            Rotate(m, v, 0, 1);
            Rotate(m, v, 0, 2);
            Rotate(m, v, 1, 2);
        }

        var values = new[] { m[0, 0], m[1, 1], m[2, 2] };
        var vectors = new[]
        {
            new[] { v[0, 0], v[1, 0], v[2, 0] },
            new[] { v[0, 1], v[1, 1], v[2, 1] },
            new[] { v[0, 2], v[1, 2], v[2, 2] },
        };

        // 내림차순 정렬(3원소 삽입정렬 — 벡터 동반 이동).
        for (var i = 1; i < 3; i++)
        {
            for (var j = i; j > 0 && values[j] > values[j - 1]; j--)
            {
                (values[j], values[j - 1]) = (values[j - 1], values[j]);
                (vectors[j], vectors[j - 1]) = (vectors[j - 1], vectors[j]);
            }
        }

        return (values, vectors);
    }

    /// <summary>(p,q) 성분을 0 으로 만드는 Jacobi 회전을 m 과 v 에 적용. 삼각함수 미사용.</summary>
    private static void Rotate(double[,] m, double[,] v, int p, int q)
    {
        double apq = m[p, q];
        if (Math.Abs(apq) < 1e-300) return;

        // θ = (a_qq − a_pp)/(2·a_pq), t = sign(θ)/(|θ| + √(θ²+1))
        double theta = (m[q, q] - m[p, p]) / (2.0 * apq);
        double t = (theta >= 0 ? 1.0 : -1.0) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
        double c = 1.0 / Math.Sqrt(t * t + 1.0);
        double s = t * c;

        int r = 3 - p - q;   // 회전에 관여하지 않는 나머지 인덱스

        double app = m[p, p], aqq = m[q, q], apr = m[p, r], aqr = m[q, r];
        m[p, p] = app - t * apq;
        m[q, q] = aqq + t * apq;
        m[p, q] = m[q, p] = 0.0;
        m[p, r] = m[r, p] = c * apr - s * aqr;
        m[q, r] = m[r, q] = s * apr + c * aqr;

        for (var i = 0; i < 3; i++)
        {
            double vip = v[i, p], viq = v[i, q];
            v[i, p] = c * vip - s * viq;
            v[i, q] = s * vip + c * viq;
        }
    }
}
