using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>
/// ② 검사위치 이동의 IK 해 선택 — 총변위-최소(기존)와 J6 와인딩 유지(신규)의 순수 선택 로직,
/// 그리고 손목 특이점(J5 부호반전) 가드를 고정한다.
/// 같은 TCP 자세를 내는 손목-플립 해(J6 ≈ −90° vs +90°) 중 어느 것을 고르는지가 핵심.
/// </summary>
public class IkWristSelectionTests
{
    // 현재(기준) 관절: J6 ≈ −89.3°(와인딩), J5 = +20°.
    private static readonly double[] Reference = { 0, 0, 120, -110, 20, -89.3 };

    // 후보 A: +90° 와인딩 해 — J1~J5 는 현재와 거의 같고 J6 만 ~179° 멀다. 총변위 ≈ 180° (최소).
    private static readonly (int Config, double[] Joints) CandPlus90 = (3, new[] { 0.0, 0, 120, -111, 20, 89.8 });
    // 후보 B: −90° 와인딩(손목-플립) 해 — J6 는 현재와 거의 같지만 J4(+180°)·J5(부호반전)가 커 총변위 ≈ 221° (큼).
    private static readonly (int Config, double[] Joints) CandMinus90 = (5, new[] { 0.0, 0, 120, 70, -20, -90.2 });

    private static double JointDist(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < 6; i++) s += System.Math.Abs(a[i] - b[i]);
        return s;
    }

    [Fact]
    public void 총변위_최소는_J6_대회전_해를_고른다()
    {
        // 기존 규칙(JointDistance) — B 는 J4/J5 변위가 커 A(+90°)가 뽑힌다 → J6 ~180° 회전.
        var best = FairinoRpcClient.SelectBestJoints(
            new[] { CandPlus90, CandMinus90 }, Reference, (j, r) => JointDist(j, r));

        Assert.NotNull(best);
        Assert.Equal(3, best!.Value.Config);
        Assert.Equal(89.8, best.Value.Joints[5], 1);
    }

    [Fact]
    public void J6_가중_선택은_현재_와인딩_해를_고른다()
    {
        // keep-wrist 비용(W6·|ΔJ6| + 총변위) — B(−90°, |ΔJ6|≈0.9)가 A(|ΔJ6|≈179)를 이긴다.
        const double w6 = 8.0;
        var best = FairinoRpcClient.SelectBestJoints(
            new[] { CandPlus90, CandMinus90 }, Reference,
            (j, r) => w6 * System.Math.Abs(j[5] - r[5]) + JointDist(j, r));

        Assert.NotNull(best);
        Assert.Equal(5, best!.Value.Config);
        Assert.Equal(-90.2, best.Value.Joints[5], 1);
    }

    [Fact]
    public void SelectBestJoints_후보가_없으면_null()
    {
        Assert.Null(FairinoRpcClient.SelectBestJoints(
            System.Array.Empty<(int, double[])>(), Reference, (j, r) => JointDist(j, r)));
    }
}
