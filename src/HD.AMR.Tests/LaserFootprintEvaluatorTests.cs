using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class LaserFootprintEvaluatorTests
{
    // appsettings 현장 헤드 오프셋(툴 XY mm) — 한 변 약 38mm 정삼각형.
    private static readonly (double X, double Y)[] Heads =
    {
        (22.62, -152.79), (4.99, -119.05), (-14.91, -151.02),
    };

    private const int W = 240, H = 240;

    // 1 px = 1 mm (깊이 무관) — 기하만 검증.
    private static (double X, double Y) OneMm(double z) => (1.0, 1.0);

    private static IReadOnlyList<(double X, double Y)> Spots =>
        LaserFootprintEvaluator.SpotOffsetsInImageMm(Heads, ToolAxisDir.PlusX, ToolAxisDir.PlusY);

    private static CameraFrame Frame(Func<int, int, double> depthMm)
    {
        var bytes = new byte[W * H * 2];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var d = (ushort)Math.Round(depthMm(x, y));
            bytes[(y * W + x) * 2] = (byte)(d & 0xFF);
            bytes[(y * W + x) * 2 + 1] = (byte)(d >> 8);
        }
        return new CameraFrame(bytes, W, H, "depth16", DateTime.UtcNow);
    }

    // 깊이는 정수 mm 라 기울기는 넓은 면에서 계단이 된다 — 블록 평균이 계단을 평활하는지도 같이 본다.
    private static double Tilted(int x, int y) => 400 + 0.05 * x - 0.03 * y;

    [Fact]
    public void SpotOffsets_AreCentroidRelative_AndFollowAxisSign()
    {
        var plus = LaserFootprintEvaluator.SpotOffsetsInImageMm(Heads, ToolAxisDir.PlusX, ToolAxisDir.PlusY);
        Assert.Equal(0, plus.Average(s => s.X), 6);
        Assert.Equal(0, plus.Average(s => s.Y), 6);
        // 정삼각형 외접원 반경 ≈ 22mm
        Assert.All(plus, s => Assert.InRange(Math.Sqrt(s.X * s.X + s.Y * s.Y), 20, 24));

        // 이미지 X ← 툴 −Y, 이미지 Y ← 툴 +X : 축 교환·부호 반전
        var swapped = LaserFootprintEvaluator.SpotOffsetsInImageMm(Heads, ToolAxisDir.MinusY, ToolAxisDir.PlusX);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(-plus[i].Y, swapped[i].X, 6);
            Assert.Equal(plus[i].X, swapped[i].Y, 6);
        }
    }

    [Fact]
    public void TiltedFlatSurface_Passes()
    {
        var e = LaserFootprintEvaluator.Evaluate(Frame(Tilted), 0.5, 0.5, Spots, new LaserFootprintOptions(), OneMm);

        Assert.True(e.Valid, e.Note);
        Assert.True(e.WorstTiltDeg < 0.8, e.ShortText());
        Assert.True(e.Passes(new LaserFootprintOptions()));
    }

    [Fact]
    public void BeadUnderOneSpot_Fails_AndIdentifiesSpot()
    {
        // 스팟1(이미지 +X 약 18mm) 자리에 2mm 비드(카메라 쪽으로 볼록 = 깊이 감소) 세로 띠.
        var frame = Frame((x, y) => Tilted(x, y) - (x is >= 132 and <= 150 ? 2.0 : 0.0));
        var e = LaserFootprintEvaluator.Evaluate(frame, 0.5, 0.5, Spots, new LaserFootprintOptions(), OneMm);

        Assert.True(e.Valid, e.Note);
        Assert.True(e.WorstTiltDeg > 1.5, e.ShortText());
        Assert.False(e.Passes(new LaserFootprintOptions()));
        Assert.Equal(0, e.WorstSpot);
    }

    [Fact]
    public void BeadNearSpotWithinMargin_Fails()
    {
        // 셀 중심에선 스팟을 비껴가지만 여유(8mm) 안에 비드 모서리가 들어온다 — 개루프 오차로 걸릴 수 있는 자리.
        var frame = Frame((x, y) => Tilted(x, y) - (x is >= 144 and <= 160 ? 2.0 : 0.0));
        var e = LaserFootprintEvaluator.Evaluate(frame, 0.5, 0.5, Spots, new LaserFootprintOptions(), OneMm);

        Assert.False(e.Passes(new LaserFootprintOptions()), e.ShortText());
        var noMargin = new LaserFootprintOptions(MarginMm: 0);
        Assert.True(LaserFootprintEvaluator.Evaluate(frame, 0.5, 0.5, Spots, noMargin, OneMm).Passes(noMargin));
    }

    [Fact]
    public void NoisyFlatSurface_StaysUnderDefaultThreshold()
    {
        // D435 400mm 수준 픽셀 노이즈 σ≈0.6mm — 블록 평균으로 기본 기준 1.5° 안에 들어와야 한다.
        var rng = new Random(7);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var frame = Frame((x, y) => 400 + 0.6 * Gauss());

        // 1 px ≈ 1 mm 는 실제(약 0.94 mm/px)와 비슷한 밀도.
        var e = LaserFootprintEvaluator.Evaluate(frame, 0.5, 0.5, Spots, new LaserFootprintOptions(), OneMm);
        Assert.True(e.Passes(new LaserFootprintOptions()), e.ShortText());
    }

    [Fact]
    public void Search_MovesFootprintOffTheBead()
    {
        var frame = Frame((x, y) => Tilted(x, y) - (x is >= 132 and <= 150 ? 2.0 : 0.0));
        var o = new LaserFootprintOptions();
        var e = LaserFootprintEvaluator.Search(frame, 0.5, 0.5, 0, 0, 1, 1, Spots, o, OneMm);

        Assert.NotNull(e);
        Assert.True(e!.Passes(o), e.ShortText());
        Assert.True(e.U < 0.5, $"비드(+X) 반대쪽으로 옮겨야 함: u={e.U:0.###}");
    }

    [Fact]
    public void Search_SkipsExcludedSites()
    {
        var o = new LaserFootprintOptions(SearchRangeMm: 12, SearchStepMm: 6, ExcludeRadiusMm: 10);
        var frame = Frame(Tilted);
        var free = LaserFootprintEvaluator.Search(frame, 0.5, 0.5, 0, 0, 1, 1, Spots, o, OneMm);
        Assert.NotNull(free);
        Assert.Equal(0.5, free!.U, 6);   // 평평하면 셀 중심(거리 최소)

        var e = LaserFootprintEvaluator.Search(frame, 0.5, 0.5, 0, 0, 1, 1, Spots, o, OneMm,
            new[] { (0.5, 0.5) });
        Assert.NotNull(e);
        double dx = (e!.U - 0.5) * W, dy = (e.V - 0.5) * H;
        Assert.True(dx * dx + dy * dy >= 100 - 1e-6, $"제외 반경 10mm 밖이어야 함: ({dx:0.#}, {dy:0.#})");
    }

    [Fact]
    public void FootprintOutsideFrame_IsInvalid()
    {
        var e = LaserFootprintEvaluator.Evaluate(Frame(Tilted), 0.02, 0.5, Spots, new LaserFootprintOptions(), OneMm);
        Assert.False(e.Valid);
    }
}
