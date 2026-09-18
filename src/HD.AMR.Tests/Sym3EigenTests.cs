using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// 대칭 3×3 Jacobi 고유분해 검증. 평면 총최소자승 적합이 여기에 의존하므로
/// (a) 고유값 내림차순, (b) 고유벡터 정규직교, (c) A = V·diag(λ)·Vᵀ 재구성, (d) 결정론성을 고정한다.
/// </summary>
public class Sym3EigenTests
{
    private static double MaxAbsDiff(double[,] a, double[,] b)
    {
        double max = 0;
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                max = Math.Max(max, Math.Abs(a[i, j] - b[i, j]));
        return max;
    }

    private static double[,] Reconstruct(double[] values, double[][] vectors)
    {
        var r = new double[3, 3];
        for (var k = 0; k < 3; k++)
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    r[i, j] += values[k] * vectors[k][i] * vectors[k][j];
        return r;
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    // 부호는 규약이 없으므로 |성분| 으로 비교한다.
    private static void AssertAxis(double[] v, double x, double y, double z, double tol = 1e-12)
    {
        Assert.True(Math.Abs(Math.Abs(v[0]) - Math.Abs(x)) < tol &&
                    Math.Abs(Math.Abs(v[1]) - Math.Abs(y)) < tol &&
                    Math.Abs(Math.Abs(v[2]) - Math.Abs(z)) < tol,
            $"고유벡터 불일치: ({v[0]:G6},{v[1]:G6},{v[2]:G6}) vs ±({x:G6},{y:G6},{z:G6})");
    }

    [Fact]
    public void Jacobi_Diagonal_ReturnsSortedValuesAndAxes()
    {
        var (values, vectors) = Sym3Eigen.Jacobi(new double[,] { { 3, 0, 0 }, { 0, 1, 0 }, { 0, 0, 2 } });

        Assert.Equal(3.0, values[0], 12);
        Assert.Equal(2.0, values[1], 12);
        Assert.Equal(1.0, values[2], 12);
        AssertAxis(vectors[0], 1, 0, 0);
        AssertAxis(vectors[1], 0, 0, 1);
        AssertAxis(vectors[2], 0, 1, 0);
    }

    [Fact]
    public void Jacobi_KnownSpectrum_MatchesAnalyticSolution()
    {
        // [[2,1,0],[1,2,0],[0,0,5]] → λ = 5, 3, 1
        var (values, vectors) = Sym3Eigen.Jacobi(new double[,] { { 2, 1, 0 }, { 1, 2, 0 }, { 0, 0, 5 } });

        Assert.Equal(5.0, values[0], 12);
        Assert.Equal(3.0, values[1], 12);
        Assert.Equal(1.0, values[2], 12);

        double h = 1.0 / Math.Sqrt(2.0);
        AssertAxis(vectors[0], 0, 0, 1);
        AssertAxis(vectors[1], h, h, 0);
        AssertAxis(vectors[2], h, h, 0);
        // λ₂·λ₃ 고유벡터는 서로 직교해야 한다((1,1,0) vs (1,−1,0)).
        Assert.True(Math.Abs(Dot(vectors[1], vectors[2])) < 1e-12, "λ₂·λ₃ 고유벡터가 직교하지 않습니다.");
    }

    // 근특이·근중복 고유값을 포함 — 공선 퇴화 검출이 여기에 달려 있다.
    public static TheoryData<double[,]> Matrices => new()
    {
        new double[,] { { 4, 1, 2 }, { 1, 3, 0 }, { 2, 0, 5 } },
        new double[,] { { 1e6, 3, 1 }, { 3, 2, 0 }, { 1, 0, 1e-9 } },      // 근특이
        new double[,] { { 2, 0, 0 }, { 0, 2.0000001, 0 }, { 0, 0, 7 } },   // 근중복 λ₂≈λ₃
        new double[,] { { 0.5, -0.25, 0.125 }, { -0.25, 0.5, -0.25 }, { 0.125, -0.25, 0.5 } },
    };

    [Theory]
    [MemberData(nameof(Matrices))]
    public void Jacobi_ReconstructsAndIsOrthonormal(double[,] a)
    {
        var (values, vectors) = Sym3Eigen.Jacobi(a);

        double scale = 0;
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                scale = Math.Max(scale, Math.Abs(a[i, j]));

        Assert.True(MaxAbsDiff(a, Reconstruct(values, vectors)) < 1e-10 * Math.Max(scale, 1),
            "V·diag(λ)·Vᵀ 가 원행렬을 복원하지 못했습니다.");

        for (var i = 0; i < 3; i++)
        {
            Assert.True(Math.Abs(Dot(vectors[i], vectors[i]) - 1.0) < 1e-12, $"고유벡터 {i} 가 단위벡터가 아닙니다.");
            for (var j = i + 1; j < 3; j++)
                Assert.True(Math.Abs(Dot(vectors[i], vectors[j])) < 1e-12, $"고유벡터 {i}·{j} 가 직교하지 않습니다.");
        }

        Assert.True(values[0] >= values[1] && values[1] >= values[2], "고유값이 내림차순이 아닙니다.");
    }

    [Fact]
    public void Jacobi_Identity_RepeatedEigenvalues()
    {
        var (values, vectors) = Sym3Eigen.Jacobi(new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } });

        Assert.All(values, v => Assert.Equal(1.0, v, 12));
        // 중복 고유값은 고유벡터가 유일하지 않다 — 직교성만 요구한다.
        for (var i = 0; i < 3; i++)
            for (var j = i + 1; j < 3; j++)
                Assert.True(Math.Abs(Dot(vectors[i], vectors[j])) < 1e-12, "단위행렬 고유벡터가 직교하지 않습니다.");
    }

    [Fact]
    public void Jacobi_IsDeterministic()
    {
        var a = new double[,] { { 4, 1, 2 }, { 1, 3, 0 }, { 2, 0, 5 } };
        var (v1, e1) = Sym3Eigen.Jacobi(a);
        var (v2, e2) = Sym3Eigen.Jacobi(a);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(v1[i], v2[i]);   // 비트 동일
            for (var j = 0; j < 3; j++) Assert.Equal(e1[i][j], e2[i][j]);
        }
    }
}
