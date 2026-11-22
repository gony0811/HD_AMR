using HD.AMR.App.Communication.GasDetector;

namespace HD.AMR.Tests;

/// <summary>무정지 연속 측정 — 한 번의 통과에서 읽은 샘플을 구간별로 나누는 규칙과 블렌딩 반경 제한.</summary>
public class GasPassSplitterTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);

    private static (DateTime At, double Value) S(double sec, double value) => (T0.AddSeconds(sec), value);

    [Fact]
    public void Split_AssignsSamplesByLengthFraction()
    {
        // 100 mm + 300 mm 를 4 초에 통과 — 경계는 1 초 지점.
        var samples = new[] { S(0.5, 1), S(0.9, 2), S(1.5, 3), S(3.9, 4) };

        var parts = GasPassSplitter.Split(samples, T0, T0.AddSeconds(4), TimeSpan.Zero, new double[] { 100, 300 });

        Assert.Equal(new double[] { 1, 2 }, parts[0].Select(x => x.Value));
        Assert.Equal(new double[] { 3, 4 }, parts[1].Select(x => x.Value));
    }

    [Fact]
    public void Split_ShiftsByResponseDelay()
    {
        // 응답 지연 1 초 — 1.5 초에 읽은 값은 0.5 초 지점(첫 구간)의 가스다.
        var samples = new[] { S(1.5, 9), S(2.5, 1) };

        var parts = GasPassSplitter.Split(samples, T0, T0.AddSeconds(2), TimeSpan.FromSeconds(1), new double[] { 100, 100 });

        Assert.Equal(new double[] { 9 }, parts[0].Select(x => x.Value));
        Assert.Equal(new double[] { 1 }, parts[1].Select(x => x.Value));
    }

    [Fact]
    public void Split_TailAfterMoveGoesToLastSegment_AndEarlySamplesToFirst()
    {
        // 이동 전 샘플(지연 보정 후 음수)은 첫 구간, 이동 뒤 꼬리(응답 지연 대기)는 마지막 구간.
        var samples = new[] { S(0.2, 1), S(2.4, 2), S(2.9, 3) };

        var parts = GasPassSplitter.Split(samples, T0, T0.AddSeconds(2), TimeSpan.FromSeconds(0.5), new double[] { 100, 100 });

        Assert.Equal(new double[] { 1 }, parts[0].Select(x => x.Value));
        Assert.Equal(new double[] { 2, 3 }, parts[1].Select(x => x.Value));
    }

    [Fact]
    public void Split_EmptySegmentBorrowsNearestSample()
    {
        // 가운데 10 mm 구간은 샘플 간격보다 빨리 지나가 샘플이 없다 — 가장 가까운 샘플을 빌려 "측정 실패"가 되지 않게 한다.
        var samples = new[] { S(0.5, 1), S(1.2, 7), S(1.9, 2) };

        var parts = GasPassSplitter.Split(samples, T0, T0.AddSeconds(2.1), TimeSpan.Zero, new double[] { 100, 10, 100 });

        Assert.Equal(new double[] { 7 }, parts[1].Select(x => x.Value));
        Assert.All(parts, p => Assert.NotEmpty(p));
    }

    [Fact]
    public void Split_NoSamples_LeavesSegmentsEmpty()
    {
        var parts = GasPassSplitter.Split(Array.Empty<(DateTime, double)>(), T0, T0.AddSeconds(1), TimeSpan.Zero,
            new double[] { 50, 50 });

        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Empty(p));
    }

    [Fact]
    public void Split_ZeroLengths_TreatedAsEqual()
    {
        var samples = new[] { S(0.2, 1), S(0.8, 2) };

        var parts = GasPassSplitter.Split(samples, T0, T0.AddSeconds(1), TimeSpan.Zero, new double[] { 0, 0 });

        Assert.Equal(new double[] { 1 }, parts[0].Select(x => x.Value));
        Assert.Equal(new double[] { 2 }, parts[1].Select(x => x.Value));
    }

    [Fact]
    public void BlendRadii_LimitedByShorterNeighbourSegment()
    {
        // 중간 경유점 2개: 첫째는 20 mm 구간에 걸려 9 mm 로 줄고, 둘째도 20 mm 쪽에 걸린다.
        var radii = GasPassSplitter.BlendRadii(new double[] { 100, 20, 200 }, 30);

        Assert.Equal(2, radii.Length);
        Assert.Equal(9, radii[0], 6);
        Assert.Equal(9, radii[1], 6);
    }

    [Fact]
    public void BlendRadii_UsesConfiguredRadiusWhenSegmentsAreLong_AndNoneForSingleSegment()
    {
        Assert.Equal(new double[] { 5 }, GasPassSplitter.BlendRadii(new double[] { 100, 100 }, 5));
        Assert.Empty(GasPassSplitter.BlendRadii(new double[] { 100 }, 5));
    }

    private static double[] P(double x, double y, double z, double ry = 0) => new[] { x, y, z, 180.0, ry, 0.0 };

    [Fact]
    public void 직선_위_5mm_경유점은_하나로_합친다()
    {
        var poses = Enumerable.Range(0, 21).Select(i => P(i * 5.0, 0, 0)).ToList();   // 100mm 직선
        Assert.Equal(new[] { 0, 20 }, GasPassSplitter.MergeCollinear(poses));
    }

    [Fact]
    public void 꺾이는_곳에서_끊는다()
    {
        // 50mm 직진 → 45° 사면 50mm (코로게이션 마루 모양)
        var poses = Enumerable.Range(0, 11).Select(i => P(i * 5.0, 0, 0))
            .Concat(Enumerable.Range(1, 10).Select(i => P(50 + i * 3.5355, 0, i * 3.5355))).ToList();
        Assert.Equal(new[] { 0, 10, 20 }, GasPassSplitter.MergeCollinear(poses));
    }

    [Fact]
    public void 자세가_바뀌면_끊는다()
    {
        var poses = Enumerable.Range(0, 11).Select(i => P(i * 5.0, 0, 0, ry: i < 6 ? 0 : 10)).ToList();
        Assert.Equal(new[] { 0, 5, 6, 10 }, GasPassSplitter.MergeCollinear(poses));
    }

    [Fact]
    public void 허용_편차_안의_흔들림은_합친다()
    {
        var poses = Enumerable.Range(0, 11).Select(i => P(i * 5.0, i % 2 == 0 ? 0 : 0.1, 0)).ToList();
        Assert.Equal(new[] { 0, 10 }, GasPassSplitter.MergeCollinear(poses));
    }

    [Fact]
    public void 점이_적으면_그대로()
    {
        Assert.Equal(new[] { 0, 1 }, GasPassSplitter.MergeCollinear(new[] { P(0, 0, 0), P(5, 0, 0) }));
        Assert.Equal(new[] { 0 }, GasPassSplitter.MergeCollinear(new[] { P(0, 0, 0) }));
    }

    [Theory]
    [InlineData(0.0, 0.3)]
    [InlineData(-1.0, 0.3)]
    [InlineData(double.NaN, 0.3)]
    [InlineData(0.3, 0.3)]
    [InlineData(1.0, 1.0)]
    [InlineData(99.0, 5.0)]
    public void ResolveMergeTol_비정상은_기본값_상한은_5(double setting, double expected)
        => Assert.Equal(expected, GasPassSplitter.ResolveMergeTol(setting), 9);

    [Fact]
    public void MergeCollinear_허용오차를_키우면_곡선_명령이_줄고_벗어남은_오차_이내()
    {
        // 반경 28.26 원호를 2mm 간격으로 60mm.
        const double r = 28.26;
        var poses = Enumerable.Range(0, 31).Select(i => { var a = i * 2.0 / r; return P(r * Math.Sin(a), 0, r * Math.Cos(a)); }).ToList();
        var tight = GasPassSplitter.MergeCollinear(poses, 0.3);
        var loose = GasPassSplitter.MergeCollinear(poses, 1.0);
        Assert.True(loose.Count < tight.Count);
        Assert.Equal(0, loose[0]);
        Assert.Equal(poses.Count - 1, loose[^1]);

        // 합친 직선에서 사이 경유점까지의 거리가 허용 오차 이내.
        for (var m = 0; m + 1 < loose.Count; m++)
        {
            double[] a = poses[loose[m]], b = poses[loose[m + 1]];
            double dx = b[0] - a[0], dz = b[2] - a[2], len = Math.Sqrt(dx * dx + dz * dz);
            for (var k = loose[m] + 1; k < loose[m + 1]; k++)
                Assert.True(Math.Abs((poses[k][0] - a[0]) * dz - (poses[k][2] - a[2]) * dx) / len <= 1.0 + 1e-9);
        }
    }
}
