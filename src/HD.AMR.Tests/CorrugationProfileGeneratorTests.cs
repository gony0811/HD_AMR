using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

public class CorrugationProfileGeneratorTests
{
    private static readonly CorrugationProfileShape G = CorrugationProfileShape.MembraneA;

    // 도면 MEMBRANE-A_ONE (10-45) (260716).dxf 의 Top→다음 Top 구간 점(첫 코로게이션 우측 + 둘째 좌측, 기울기 보정 전).
    // 형상 모델이 이 점들을 지나야 한다.
    public static TheoryData<double, double> DxfPoints => new()
    {
        { 0.0, 33.8 }, { 4.9075, 33.3707 }, { 9.6658, 32.0957 }, { 14.1305, 30.0138 }, { 18.1658, 27.1882 },
        { 21.6491, 23.7048 }, { 23.2038, 21.6718 }, { 25.7617, 17.1586 }, { 27.5430, 11.8688 }, { 30.9289, 2.9289 },
        { 59.9689, 0.0 }, { 180.0, 0.0 }, { 329.0711, 2.9289 }, { 360.0, 33.8 },
    };

    [Theory]
    [MemberData(nameof(DxfPoints))]
    public void Shape_PassesThroughDxfPoints(double x, double z)
        => Assert.InRange(CorrugationProfileGenerator.Evaluate(G, x).Z, z - 0.002, z + 0.002);

    [Fact]
    public void Shape_IsC1AndMonotoneOnHalfPitch()
    {
        var prevZ = double.MaxValue;
        var prevSlope = 0.0;
        for (var x = 0.0; x <= 180.0; x += 0.001)
        {
            var (z, slope) = CorrugationProfileGenerator.Evaluate(G, x);
            Assert.True(z <= prevZ + 1e-9, $"x={x}: Z 증가");
            Assert.True(Math.Abs(slope - prevSlope) < 0.05, $"x={x}: 기울기 불연속 {prevSlope}→{slope}");
            prevZ = z;
            prevSlope = slope;
        }
    }

    [Fact]
    public void Generate_Default_TopToTopAt2mmAlongCurve_WristFixed()
    {
        Assert.True(CorrugationProfileGenerator.TrySample(G, new CorrugationSamplingOptions(), out var s, out var err), err);
        var length = CorrugationProfileGenerator.Length(G);
        Assert.InRange(length, 394.5, 395.5);
        Assert.Equal((int)Math.Ceiling(length / 2.0) + 1, s.Count);

        // 양 끝은 점1·점2 Top.
        Assert.Equal((0.0, 33.8), (s[0].X, s[0].Z));
        Assert.Equal(360.0, s[^1].X, 9);
        Assert.Equal(33.8, s[^1].Z, 6);

        // 곡선을 따라 잰 간격이 모두 2mm 이하로 같고, 직선 거리도 2mm 를 넘지 않는다.
        for (var i = 1; i < s.Count; i++)
        {
            Assert.InRange(s[i].S - s[i - 1].S, 1.99, 2.0);
            var chord = Math.Sqrt(Math.Pow(s[i].X - s[i - 1].X, 2) + Math.Pow(s[i].Z - s[i - 1].Z, 2));
            Assert.InRange(chord, 1.9, 2.0 + 1e-9);
        }

        // 1피치 = 좌우 대칭.
        for (var i = 0; i < s.Count; i++)
        {
            Assert.Equal(360.0 - s[i].X, s[^(i + 1)].X, 6);
            Assert.Equal(s[i].Z, s[^(i + 1)].Z, 6);
        }

        Assert.True(CorrugationProfileGenerator.TryGenerate(G, new CorrugationSamplingOptions(), out var wp, out _));
        Assert.All(wp, w => Assert.Equal((0.0, 0.0, 0.0, 0.0), (w.Theta, w.RxDeg, w.RzDeg, w.Y)));
        Assert.Equal((byte)SurfaceType.Corrugation, wp[0].Surface);
        Assert.Equal((byte)SurfaceType.Flat, wp[wp.Count / 2].Surface);
    }

    [Fact]
    public void Generate_StandoffLiftsAlongPlaneNormal()
    {
        CorrugationProfileGenerator.TrySample(G, new CorrugationSamplingOptions(), out var a, out _);
        Assert.True(CorrugationProfileGenerator.TrySample(G, new CorrugationSamplingOptions(StandoffMm: 5), out var b, out var err), err);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].X, b[i].X, 9);
            Assert.Equal(a[i].Z + 5, b[i].Z, 9);
        }
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-1.0, 0.0)]
    [InlineData(2.0, -1.0)]
    public void Generate_RejectsBadOptions(double spacing, double standoff)
        => Assert.False(CorrugationProfileGenerator.TrySample(G,
            new CorrugationSamplingOptions(spacing, standoff), out _, out _));
}
