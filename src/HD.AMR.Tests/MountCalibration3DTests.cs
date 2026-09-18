using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// 6-DoF 장착 캘리브레이션(T_A_B) 검증 — 합성 표본, 하드웨어 불필요.
///
/// 표본은 <see cref="FrameMath"/> 로 정방향 모델을 세워 역산해 만든다(p_k = inv(T_W_A·T_A_B)·q).
/// 따라서 모든 테스트가 SolveMount3D ↔ FrameMath 의 ZYX 규약 일치까지 동시에 검증한다.
///
/// 고정하는 계약:
///   · 정확 데이터에서 6값 복원(tz 는 타깃 높이 입력이 있을 때만)
///   · tz 미관측 규약 — 높이 미입력 시 나머지 5값은 동일하고 d 만 보고
///   · 퇴화(터치점 공선 / yaw 부족)는 예외가 아니라 Success=false + 한국어 Error
///   · 잔차 항등식(RMS² = 평면내² + 평면²)과 SolveMount2D 와의 상위집합 관계
/// </summary>
public class MountCalibration3DTests
{
    private const double D2R = Math.PI / 180.0;

    // 진값. rz 는 SolveMount2D 의 0.05° 격자 위(37.00)로 잡아 양자화 오차를 분리한다.
    private static readonly double[] Truth = { 320.0, -145.0, 610.0, 2.5, -1.8, 37.0 };
    private static readonly double[] Q = { 4200.0, 1750.0, -180.0 };   // 고정 세계점(맵 mm)

    private static readonly (double X, double Y, double Yaw)[] Poses =
    {
        (3100,  900,   12), (3450, 1600,   95), (2700, 1200, -70),
        (3900,  600,  165), (3000, 2000, -140), (3600, 1400,  40),
    };   // yaw 원형 펼침 ≈ 278°, 정차 위치도 비공선

    private static List<(double, double, double, double, double, double)> Build(
        double[] truth, double[] q, (double X, double Y, double Yaw)[] poses)
    {
        var list = new List<(double, double, double, double, double, double)>();
        foreach (var (x, y, yaw) in poses)
        {
            var tWB = FrameMath.Multiply(
                FrameMath.PoseToMatrix(new[] { x, y, 0.0, 0.0, 0.0, yaw }),
                FrameMath.PoseToMatrix(truth));
            var inv = FrameMath.Invert(tWB);                       // p_k = inv(T_W_B)·q
            double px = inv[0, 0] * q[0] + inv[0, 1] * q[1] + inv[0, 2] * q[2] + inv[0, 3];
            double py = inv[1, 0] * q[0] + inv[1, 1] * q[1] + inv[1, 2] * q[2] + inv[1, 3];
            double pz = inv[2, 0] * q[0] + inv[2, 1] * q[1] + inv[2, 2] * q[2] + inv[2, 3];
            list.Add((x, y, yaw, px, py, pz));
        }
        return list;
    }

    /// <summary>결정론적 난수 — System.Random 의 시드 수열은 .NET 버전 간 보장되지 않는다.</summary>
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
    public void SolveMount3D_ExactSynthetic_RecoversPose()
    {
        var r = MapCalibration.SolveMount3D(Build(Truth, Q, Poses), targetZmm: Q[2]);

        Assert.True(r.Success, r.Error);
        Near(Truth[3], r.MountPose[3], 1e-8, "rx");
        Near(Truth[4], r.MountPose[4], 1e-8, "ry");
        Near(Truth[5], r.MountPose[5], 1e-3, "rz");
        Near(Truth[0], r.MountPose[0], 0.2, "tx");
        Near(Truth[1], r.MountPose[1], 0.2, "ty");
        Near(Truth[2], r.MountPose[2], 1e-6, "tz");
        Near(Q[0], r.WorldPointMm[0], 0.2, "qx");
        Near(Q[1], r.WorldPointMm[1], 0.2, "qy");

        Assert.True(r.TzObserved);
        Assert.Equal(6, r.N);
        Assert.Equal(6, r.ResidualsMm.Count);
        Assert.True(r.RmsMm < 0.2, $"RMS {r.RmsMm}");
        Assert.True(r.PlaneRmsMm < 1e-9, $"평면 RMS {r.PlaneRmsMm}");
        Assert.True(r.YawSpanDeg > 250, $"yaw 펼침 {r.YawSpanDeg}");
        Assert.DoesNotContain(r.Warnings, w => w.Contains("yaw 펼침"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("타깃 높이 미입력"));
    }

    // φ 1D 탐색의 0.05° 격자 한계를 명시적으로 고정 — 탐색을 조이면 이 테스트는 통과한 채 남는다.
    [Fact]
    public void SolveMount3D_OffGridRz_WithinSweepQuantization()
    {
        var truth = (double[])Truth.Clone();
        truth[5] = 37.03;
        var r = MapCalibration.SolveMount3D(Build(truth, Q, Poses), targetZmm: Q[2]);

        Assert.True(r.Success, r.Error);
        Assert.True(Math.Abs(r.MountPose[5] - 37.03) <= 0.026,
            $"rz 양자화 오차가 격자 절반을 넘습니다: {r.MountPose[5]}");
    }

    [Fact]
    public void SolveMount3D_WithNoise_TiltFarMorePreciseThanTranslation()
    {
        var rng = new Lcg(20260318);
        var poses = new (double, double, double)[12];
        for (var i = 0; i < 12; i++)
            poses[i] = (3000 + 300 * Math.Cos(i * 1.1), 1400 + 300 * Math.Sin(i * 1.7), -165 + 330.0 * i / 11.0);

        var clean = Build(Truth, Q, poses);
        var noisy = clean.Select(k => (
            k.Item1 + 15.0 * rng.Gauss(),
            k.Item2 + 15.0 * rng.Gauss(),
            k.Item3 + 0.3 * rng.Gauss(),
            k.Item4 + 1.0 * rng.Gauss(),
            k.Item5 + 1.0 * rng.Gauss(),
            k.Item6 + 1.0 * rng.Gauss())).ToList();

        var r = MapCalibration.SolveMount3D(noisy, targetZmm: Q[2]);

        Assert.True(r.Success, r.Error);
        Near(Truth[3], r.MountPose[3], 0.15, "rx(잡음)");
        Near(Truth[4], r.MountPose[4], 0.15, "ry(잡음)");
        Near(Truth[5], r.MountPose[5], 1.5, "rz(잡음)");
        Near(Truth[0], r.MountPose[0], 40, "tx(잡음)");
        Near(Truth[1], r.MountPose[1], 40, "ty(잡음)");
        Near(Truth[2], r.MountPose[2], 5, "tz(잡음)");

        // 채널 분리: 평면(터치) 잔차는 mm 단위, 평면내(SLAM) 잔차는 cm 단위여야 한다.
        Assert.InRange(r.PlaneRmsMm, 0.3, 3.0);
        Assert.InRange(r.PlanarRmsMm, 5.0, 40.0);

        // 보고한 기울기 불확도가 실제 오차를 예측해야 한다.
        double tiltErr = Math.Max(Math.Abs(r.MountPose[3] - Truth[3]), Math.Abs(r.MountPose[4] - Truth[4]));
        Assert.True(tiltErr < 3 * r.TiltSigmaDeg,
            $"기울기 오차 {tiltErr:F3}° 가 보고된 3σ({3 * r.TiltSigmaDeg:F3}°)를 넘습니다.");
        Assert.True(r.TiltSigmaDeg < 0.3, $"기울기 σ {r.TiltSigmaDeg}");
    }

    [Fact]
    public void SolveMount3D_NoTargetZ_ReportsUnobservedTz()
    {
        var samples = Build(Truth, Q, Poses);
        var withZ = MapCalibration.SolveMount3D(samples, targetZmm: Q[2]);
        var noZ = MapCalibration.SolveMount3D(samples, targetZmm: null);

        Assert.True(noZ.Success, noZ.Error);
        Assert.False(noZ.TzObserved);
        Assert.Equal(0.0, noZ.MountPose[2]);
        Near(Q[2] - Truth[2], noZ.PlaneOffsetDmm, 1e-6, "d = q_z − t_z");
        Assert.Contains(noZ.Warnings, w => w.Contains("타깃 높이"));

        // 높이는 tz 이외의 어떤 값에도 영향을 주지 않는다 — 비트 동일.
        for (var i = 0; i < 6; i++)
        {
            if (i == 2) continue;
            Assert.Equal(withZ.MountPose[i], noZ.MountPose[i]);
        }
        // 높이를 나중에 입력해도 재터치 없이 닫힌다.
        Near(Truth[2], Q[2] - noZ.PlaneOffsetDmm, 1e-6, "후입력 tz");
    }

    [Fact]
    public void SolveMount3D_CollinearTouchPoints_Fails()
    {
        // AMR 차체 기준으로 타깃을 한 직선 위에 놓고, 그에 맞는 정차 위치를 역산한다.
        // → yaw 다양성은 그대로 유지한 채 평면 적합만 퇴화시킨다.
        double[] yaws = { 0, 60, 120, 200, 280 };
        var samples = new List<(double, double, double, double, double, double)>();
        var rT = FrameMath.PoseToMatrix(Truth);
        var invT = FrameMath.Invert(rT);
        for (var i = 0; i < yaws.Length; i++)
        {
            double ax = 800 + 140 * i, ay = 250, az = Q[2];   // 차체 기준 — 한 직선
            double th = yaws[i] * D2R;
            double cx = Q[0] - (Math.Cos(th) * ax - Math.Sin(th) * ay);
            double cy = Q[1] - (Math.Sin(th) * ax + Math.Cos(th) * ay);
            double px = invT[0, 0] * ax + invT[0, 1] * ay + invT[0, 2] * az + invT[0, 3];
            double py = invT[1, 0] * ax + invT[1, 1] * ay + invT[1, 2] * az + invT[1, 3];
            double pz = invT[2, 0] * ax + invT[2, 1] * ay + invT[2, 2] * az + invT[2, 3];
            samples.Add((cx, cy, yaws[i], px, py, pz));
        }

        var r = MapCalibration.SolveMount3D(samples, targetZmm: Q[2]);

        Assert.False(r.Success);
        Assert.Contains("직선", r.Error);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void SolveMount3D_InsufficientYawSpan_Fails()
    {
        var poses = new (double, double, double)[]
        {
            (3100, 900, 0), (3450, 1600, 2), (2700, 1200, 4), (3900, 600, 5), (3000, 2000, 3),
        };
        var r = MapCalibration.SolveMount3D(Build(Truth, Q, poses), targetZmm: Q[2]);

        Assert.False(r.Success);
        Assert.Contains("yaw", r.Error);
    }

    /// <summary>
    /// yaw 게이트의 존재 이유. yaw 가 좁으면 해가 <b>편향</b>되는 게 아니라 <b>SLAM 잡음이 증폭</b>된다
    /// (증폭률 ≈ 1/(2·sin(펼침/2)) — 5° 펼침이면 약 11배). 레거시 평면 경로는 그 상태에서도
    /// 예외 없이 tx 가 100mm 넘게 틀린 해를 돌려주는데 잔차는 여전히 작아서 운영자가 합격으로 오인한다.
    /// SolveMount3D 는 같은 데이터를 계산 전에 거부한다.
    /// </summary>
    [Fact]
    public void SolveMount2D_LowYawWithNoise_SilentlyAmplifiesError_WhileSolveMount3DRefuses()
    {
        var poses = new (double X, double Y, double Yaw)[]
        {
            (3100, 900, 0), (3450, 1600, 2), (2700, 1200, 4), (3900, 600, 5), (3000, 2000, 3),
        };
        var rng = new Lcg(4242);
        var samples = Build(Truth, Q, poses).Select(k => (
            k.Item1 + 15.0 * rng.Gauss(), k.Item2 + 15.0 * rng.Gauss(), k.Item3,
            k.Item4, k.Item5, k.Item6)).ToList();
        var planar = samples.Select(k => (k.Item1, k.Item2, k.Item3, k.Item4, k.Item5)).ToList();

        var legacy = MapCalibration.SolveMount2D(planar);
        double txErr = Math.Abs(legacy.Tx - Truth[0]);
        Assert.True(txErr > 100,
            $"yaw 5° 펼침 + SLAM 잡음 15mm 에서 tx 오차가 100mm 이하로 나왔습니다({txErr:F0}mm) — " +
            "증폭 모델이나 게이트 근거를 재검토하세요.");
        Assert.True(legacy.RmsMm < txErr / 2,
            $"잔차({legacy.RmsMm:F0}mm)가 실제 오차({txErr:F0}mm)를 크게 과소평가한다는 점이 요지입니다.");

        var guarded = MapCalibration.SolveMount3D(samples, targetZmm: Q[2]);
        Assert.False(guarded.Success);
        Assert.Contains("yaw", guarded.Error);
    }

    [Fact]
    public void SolveMount3D_RoundTripsThroughBasePointToMap()
    {
        var samples = Build(Truth, Q, Poses);
        var r = MapCalibration.SolveMount3D(samples, targetZmm: Q[2]);
        Assert.True(r.Success, r.Error);

        foreach (var (x, y, yaw, bx, by, bz) in samples)
        {
            // 실제 소비 경로: SLAM(m/rad) → mm/deg 어댑터 → BasePointToMap
            var amrPose = MapCalibration.AmrPoseToMmDeg(x / 1000.0, y / 1000.0, yaw * D2R);
            var (wx, wy) = MapCalibration.BasePointToMap(amrPose, r.MountPose, new[] { bx, by, bz });
            Near(Q[0], wx, 0.3, "맵 X 왕복");
            Near(Q[1], wy, 0.3, "맵 Y 왕복");

            var tWB = FrameMath.Multiply(FrameMath.PoseToMatrix(amrPose), FrameMath.PoseToMatrix(r.MountPose));
            double wz = tWB[2, 0] * bx + tWB[2, 1] * by + tWB[2, 2] * bz + tWB[2, 3];
            Near(Q[2], wz, 0.3, "맵 Z 왕복");
        }
    }

    [Fact]
    public void SolveMount3D_ZeroTiltTruth_MatchesSolveMount2D()
    {
        var truth = new[] { Truth[0], Truth[1], Truth[2], 0.0, 0.0, Truth[5] };
        var samples = Build(truth, Q, Poses);

        var r = MapCalibration.SolveMount3D(samples, targetZmm: Q[2]);
        Assert.True(r.Success, r.Error);
        Assert.True(Math.Abs(r.MountPose[3]) < 1e-9 && Math.Abs(r.MountPose[4]) < 1e-9,
            $"기울기가 0 이 아닙니다: rx={r.MountPose[3]}, ry={r.MountPose[4]}");
        Assert.True(r.PlaneSpanMm > 300, $"면내 펼침 {r.PlaneSpanMm}");

        // M = I 이므로 두 경로는 근사가 아니라 수치적으로 동일해야 한다.
        var planar = samples.Select(k => (k.Item1, k.Item2, k.Item3, k.Item4, k.Item5)).ToList();
        var p2 = MapCalibration.SolveMount2D(planar);
        Near(p2.PhiDeg, r.MountPose[5], 1e-9, "rz(2D 대조)");
        Near(p2.Tx, r.MountPose[0], 1e-9, "tx(2D 대조)");
        Near(p2.Ty, r.MountPose[1], 1e-9, "ty(2D 대조)");
    }

    // 구버전 표본(Bz 미기록) 경로 — 풀리되 경고가 나와야 한다.
    [Fact]
    public void SolveMount3D_AllBzZero_SolvesWithZeroTiltAndWarns()
    {
        var truth = new[] { Truth[0], Truth[1], Truth[2], 0.0, 0.0, Truth[5] };
        var samples = Build(truth, Q, Poses)
            .Select(k => (k.Item1, k.Item2, k.Item3, k.Item4, k.Item5, 0.0)).ToList();

        var r = MapCalibration.SolveMount3D(samples, targetZmm: Q[2]);

        Assert.True(r.Success, r.Error);
        Assert.True(Math.Abs(r.MountPose[3]) < 1e-12 && Math.Abs(r.MountPose[4]) < 1e-12);
        Assert.Contains(r.Warnings, w => w.Contains("터치점 Z"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SolveMount3D_TooFewSamples_Fails(int count)
    {
        var samples = Build(Truth, Q, Poses).Take(count).ToList();
        var r = MapCalibration.SolveMount3D(samples, targetZmm: Q[2]);

        Assert.False(r.Success);
        Assert.Contains("/3", r.Error);
        Assert.Empty(r.ResidualsMm);
    }

    // 내부 일관성 — 고유분해·잔차 분해 버그 탐지기.
    [Fact]
    public void SolveMount3D_ResidualIdentitiesHold()
    {
        var rng = new Lcg(777);
        var samples = Build(Truth, Q, Poses).Select(k => (
            k.Item1 + 12.0 * rng.Gauss(), k.Item2 + 12.0 * rng.Gauss(), k.Item3 + 0.25 * rng.Gauss(),
            k.Item4 + 1.0 * rng.Gauss(), k.Item5 + 1.0 * rng.Gauss(), k.Item6 + 1.0 * rng.Gauss())).ToList();

        var r = MapCalibration.SolveMount3D(samples, targetZmm: Q[2]);
        Assert.True(r.Success, r.Error);

        Near(r.RmsMm * r.RmsMm, r.PlanarRmsMm * r.PlanarRmsMm + r.PlaneRmsMm * r.PlaneRmsMm, 1e-9,
            "RMS² = 평면내² + 평면²");
        Near(r.PlaneRmsMm, Math.Sqrt(r.PlaneLambdaMinMm2 / r.N), 1e-9, "평면 RMS = √(λ₃/N)");
        Near(0.0, r.PlaneResidualsMm.Sum(), 1e-6, "평면 잔차 합(중심 적합 → 0)");
        Assert.Equal(r.ResidualsMm.Max(), r.MaxAbsMm);
    }

    // 현재값 대비 변화량은 성분 차가 아니라 상대 변환이어야 한다(±180° 랩).
    [Fact]
    public void SolveMount3D_DeltaFromCurrentMount_IsRelativeTransform()
    {
        var current = (double[])Truth.Clone();
        current[5] = Truth[5] - 360.0 + 1.0;   // 같은 회전에서 −1° 만 차이(성분 차로는 ≈359°)

        var r = MapCalibration.SolveMount3D(Build(Truth, Q, Poses), targetZmm: Q[2], currentMount: current);

        Assert.True(r.Success, r.Error);
        Assert.NotNull(r.DeltaPose);
        Near(1.0, r.DeltaAngleDeg, 0.05, "상대 회전각");
    }

    [Theory]
    [InlineData(new double[] { 0, 90, 180, 270 }, 270)]
    [InlineData(new double[] { 0, 10, 20 }, 20)]
    [InlineData(new double[] { -170, 170 }, 20)]          // 랩 경계
    [InlineData(new double[] { 350, 10, 30 }, 40)]
    public void CircularSpanDeg_HandlesWrap(double[] angles, double expected)
        => Near(expected, MapCalibration.CircularSpanDeg(angles), 1e-9, "원형 펼침");
}
