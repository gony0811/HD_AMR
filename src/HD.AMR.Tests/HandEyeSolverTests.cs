using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// AX=XB 눈-손 캘리브레이션(Park &amp; Martin) 검증 — 합성 표본, 하드웨어 불필요.
///
/// 표본은 <see cref="FrameMath"/> 로 정방향 모델을 세워 만든다: 마커·AMR·리프트를 고정한 채
/// 코봇 자세만 바꾸면 <c>T_B_F(k) · T_F_C · T_C_Q(k) = 상수</c> 이므로
/// <c>T_C_Q(k) = inv(T_F_C) · inv(T_B_F(k)) · T_B_Q</c> 로 역산한다.
///
/// 고정하는 계약:
///   · 정확 데이터에서 T_F_C 복원 (AMR pose 가 식에 아예 없다 = T_A_B 없이 풀린다)
///   · 회전축이 한 방향뿐이면 <b>표본을 늘려도</b> 실패 — 이 방식의 유일한 치명 조건
///   · 순수 병진 자세만으로는 실패
/// </summary>
public class HandEyeSolverTests
{
    // 진값 T_F_C — 플랜지에서 카메라까지. 실제 장착과 비슷한 크기로.
    private static readonly double[] TruthFC = { 62.0, -18.5, 95.0, -3.0, 7.5, 21.0 };

    // 마커의 BASE 기준 pose(바닥 고정 ArUco 를 코봇 베이스에서 본 값). 상수면 무엇이든 무방.
    private static readonly double[] MarkerInBase = { 850.0, 120.0, -740.0, 178.0, 2.0, 15.0 };

    private static (List<double[]> Flange, List<double[]> Marker) Build(
        IEnumerable<double[]> flangePoses, double[]? truthFc = null)
    {
        var fc = FrameMath.PoseToMatrix(truthFc ?? TruthFC);
        var bq = FrameMath.PoseToMatrix(MarkerInBase);
        var f = new List<double[]>();
        var m = new List<double[]>();
        foreach (var fp in flangePoses)
        {
            var bf = FrameMath.PoseToMatrix(fp);
            // T_C_Q = inv(T_F_C) · inv(T_B_F) · T_B_Q
            var cq = FrameMath.Multiply(FrameMath.Invert(fc),
                     FrameMath.Multiply(FrameMath.Invert(bf), bq));
            f.Add(fp);
            m.Add(FrameMath.MatrixToPose(cq));
        }
        return (f, m);
    }

    /// <summary>회전축이 3방향으로 고루 퍼진 정상 표본 세트.</summary>
    private static double[][] GoodPoses() => new[]
    {
        new[] { 600.0, 0.0, 300.0, 180.0, 0.0, 0.0 },
        new[] { 620.0, 40.0, 320.0, 155.0, 12.0, 20.0 },
        new[] { 580.0, -50.0, 280.0, 200.0, -15.0, -25.0 },
        new[] { 640.0, 30.0, 350.0, 170.0, 25.0, 40.0 },
        new[] { 560.0, -30.0, 260.0, 190.0, -22.0, 35.0 },
        new[] { 610.0, 60.0, 310.0, 160.0, 5.0, -40.0 },
        new[] { 590.0, -10.0, 330.0, 205.0, 18.0, 10.0 },
        new[] { 630.0, 20.0, 290.0, 150.0, -8.0, 55.0 },
    };

    private sealed class Lcg
    {
        private ulong _s;
        public Lcg(ulong seed) => _s = seed | 1;
        private double NextUnit()
        {
            _s = _s * 6364136223846793005UL + 1442695040888963407UL;
            return ((_s >> 11) & ((1UL << 53) - 1)) / (double)(1UL << 53);
        }
        public double Gauss()
        {
            double u1 = Math.Max(NextUnit(), 1e-12), u2 = NextUnit();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }

    private static void Near(double expected, double actual, double tol, string what)
        => Assert.True(Math.Abs(expected - actual) < tol,
            $"{what}: 기대 {expected:G10}, 실제 {actual:G10} (허용 {tol:G6})");

    [Fact]
    public void Solve_ExactSynthetic_RecoversHandEye()
    {
        var (f, m) = Build(GoodPoses());
        var r = HandEyeSolver.Solve(f, m);

        Assert.True(r.Success, r.Error);
        for (var i = 0; i < 3; i++) Near(TruthFC[i], r.PoseFC[i], 1e-6, $"t[{i}]");
        for (var i = 3; i < 6; i++) Near(TruthFC[i], r.PoseFC[i], 1e-6, $"r[{i}]");

        Assert.Equal(8, r.N);
        // 잔차 하한은 acos 의 조건수가 정한다 — 각도 0 근처에서 acos(1−ε) ≈ √(2ε) 라
        // 행렬의 1e-16 오차가 ~1e-6 도로 증폭된다. 솔버 결함이 아니다.
        Assert.True(r.RotationRmsDeg < 1e-4, $"회전 잔차 {r.RotationRmsDeg}");
        Assert.True(r.TranslationRmsMm < 1e-6, $"병진 잔차 {r.TranslationRmsMm}");
        Assert.True(r.AxisSpreadDeg > 15, $"축 다양성 {r.AxisSpreadDeg}");
        Assert.DoesNotContain(r.Warnings, w => w.Contains("회전축 다양성"));
    }

    /// <summary>
    /// 이 방식이 T_A_B 와 무관함을 고정한다 — 표본 생성에 AMR pose 가 전혀 쓰이지 않으므로
    /// (AMR 을 세워 두면 식에서 소거되므로) 순환 의존이 없다.
    /// </summary>
    [Fact]
    public void Solve_IsIndependentOfMarkerPlacement()
    {
        var (f1, m1) = Build(GoodPoses());
        var a = HandEyeSolver.Solve(f1, m1);

        // 마커를 전혀 다른 곳에 둬도(=T_B_Q 가 달라도) 같은 T_F_C 가 나와야 한다.
        var otherMarker = new[] { -300.0, 900.0, -200.0, 90.0, -30.0, 120.0 };
        var fc = FrameMath.PoseToMatrix(TruthFC);
        var bq = FrameMath.PoseToMatrix(otherMarker);
        var f2 = new List<double[]>();
        var m2 = new List<double[]>();
        foreach (var fp in GoodPoses())
        {
            var bf = FrameMath.PoseToMatrix(fp);
            m2.Add(FrameMath.MatrixToPose(FrameMath.Multiply(FrameMath.Invert(fc),
                   FrameMath.Multiply(FrameMath.Invert(bf), bq))));
            f2.Add(fp);
        }
        var b = HandEyeSolver.Solve(f2, m2);

        Assert.True(a.Success && b.Success);
        for (var i = 0; i < 6; i++) Near(a.PoseFC[i], b.PoseFC[i], 1e-6, $"pose[{i}] 마커 위치 무관성");
    }

    /// <summary>
    /// <b>이 방식의 유일한 치명 조건.</b> 모든 상대 회전축이 평행하면(손목을 한 축으로만 돌리면)
    /// MᵀM 이 특이해져 해가 없다 — 표본을 20개로 늘려도 마찬가지다.
    /// </summary>
    [Fact]
    public void Solve_SingleRotationAxis_FailsEvenWithManyPoses()
    {
        var poses = new List<double[]>();
        for (var i = 0; i < 20; i++)
            poses.Add(new[] { 600.0 + 5 * i, 0.0, 300.0, 180.0, 0.0, -60.0 + 6.0 * i });   // rz 만 변화

        var (f, m) = Build(poses);
        var r = HandEyeSolver.Solve(f, m);

        Assert.False(r.Success);
        Assert.Contains("회전축", r.Error);
    }

    [Fact]
    public void Solve_TranslationOnly_Fails()
    {
        var poses = new List<double[]>();
        for (var i = 0; i < 10; i++)
            poses.Add(new[] { 550.0 + 20 * i, -40.0 + 10 * i, 280.0 + 8 * i, 180.0, 0.0, 0.0 });

        var (f, m) = Build(poses);
        var r = HandEyeSolver.Solve(f, m);

        Assert.False(r.Success);   // 회전이 없으면 유효 쌍이 아예 만들어지지 않는다
        Assert.Contains("회전", r.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Solve_TooFewPoses_Fails(int count)
    {
        var (f, m) = Build(GoodPoses().Take(count));
        var r = HandEyeSolver.Solve(f, m);

        Assert.False(r.Success);
        Assert.Contains("/3", r.Error);
    }

    [Fact]
    public void Solve_MismatchedCounts_Fails()
    {
        var (f, m) = Build(GoodPoses());
        m.RemoveAt(0);

        var r = HandEyeSolver.Solve(f, m);
        Assert.False(r.Success);
        Assert.Contains("개수", r.Error);
    }

    // 마커 검출 잡음(위치 1mm / 자세 0.3°)에서 실용 정밀도가 나오는지 — 잔차가 실제 오차를 예측해야 한다.
    [Fact]
    public void Solve_WithDetectionNoise_StaysUsable()
    {
        var rng = new Lcg(20260918);
        var (f, m) = Build(GoodPoses());
        var noisy = m.Select(p => new[]
        {
            p[0] + 1.0 * rng.Gauss(), p[1] + 1.0 * rng.Gauss(), p[2] + 1.0 * rng.Gauss(),
            p[3] + 0.3 * rng.Gauss(), p[4] + 0.3 * rng.Gauss(), p[5] + 0.3 * rng.Gauss(),
        }).ToList();

        var r = HandEyeSolver.Solve(f, noisy);

        Assert.True(r.Success, r.Error);
        for (var i = 0; i < 3; i++) Near(TruthFC[i], r.PoseFC[i], 12.0, $"t[{i}](잡음)");
        for (var i = 3; i < 6; i++) Near(TruthFC[i], r.PoseFC[i], 2.0, $"r[{i}](잡음)");
        Assert.True(r.RotationRmsDeg > 0, "잡음이 있으면 잔차가 0 일 수 없다");
    }

    /// <summary>
    /// 180° 상대 회전 쌍은 <b>축 부호가 원리적으로 모호</b>하므로(+π ≡ −π) 솔버가 제외해야 한다.
    /// 아래 자세 세트에는 정확히 180°, 179°, 178.3° 쌍이 섞여 있다 — 이를 버리지 않으면
    /// M = Σβαᵀ 가 오염돼 병진이 mm 단위로 틀어진다(실제로 그렇게 실패했던 회귀 케이스).
    /// </summary>
    [Fact]
    public void Solve_DiscardsNearPiPairs_AndStillRecovers()
    {
        var poses = new[]
        {
            new[] { 600.0, 0.0, 300.0, 179.5, 0.0, 0.0 },
            new[] { 600.0, 0.0, 300.0, 0.5, 0.0, 0.0 },
            new[] { 610.0, 20.0, 310.0, 90.0, 30.0, 0.0 },
            new[] { 590.0, -20.0, 290.0, -90.0, -30.0, 45.0 },
            new[] { 620.0, 10.0, 320.0, 120.0, 10.0, -60.0 },
        };
        var (f, m) = Build(poses);
        var r = HandEyeSolver.Solve(f, m);

        Assert.True(r.Success, r.Error);
        for (var i = 0; i < 6; i++) Near(TruthFC[i], r.PoseFC[i], 1e-6, $"pose[{i}](180° 쌍 제외 후)");

        // 5 자세 = 10 쌍인데 180° 부근 3 쌍이 제외돼야 한다.
        Assert.Equal(7, r.PairCount);
    }
}
