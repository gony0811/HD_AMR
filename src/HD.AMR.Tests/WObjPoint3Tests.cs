using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>CROSS4 영역 점3(면 기울기) — 2점 좌표계의 Y 방향 기울기를 점3 실측으로 보정.</summary>
public class WObjPoint3Tests
{
    // 점1 툴 +Z = BASE +Z, 점1→점2 = BASE +X → 작업물 Y = BASE +Y.
    private static readonly double[] P1 = { 0, 0, 0, 0, 0, 0 };
    private static readonly double[] P2 = { 360, 0, 0, 0, 0, 0 };
    private const double Wd = 317;

    [Fact]
    public void 같은_높이면_보정하지_않는다()
    {
        var r = InternalWorkpieceFrame.Point3Normal(P1, P2, new double[] { 360, 250, 0 }, Wd, Wd)!;
        Assert.Equal(0, r.RollDeg, 6);
        Assert.Equal(0, r.HeightMm, 6);
        Assert.Equal(1, r.NormalBase[2], 6);
    }

    [Fact]
    public void Y_쪽_면이_멀면_법선을_그쪽으로_기울인다()
    {
        // 점3 에서 레이저가 10mm 더 멀다 = 면이 10mm 낮다 → 면이 +Y 로 내려감.
        var r = InternalWorkpieceFrame.Point3Normal(P1, P2, new double[] { 360, 250, 0 }, Wd + 10, Wd)!;
        Assert.Equal(-10, r.HeightMm, 6);
        Assert.Equal(Math.Atan2(-10, 250) * 180 / Math.PI, r.RollDeg, 6);
        Assert.True(r.NormalBase[1] > 0);           // +Y 쪽으로 기움
        Assert.Equal(0, r.NormalBase[0], 6);        // X축 둘레로만 돈다
    }

    [Fact]
    public void 반대쪽_Y_에서_재도_같은_면이면_같은_법선()
    {
        // 면이 +Y 로 내려가는 같은 면을 −Y 쪽에서 재면 레이저가 10mm 더 가깝다.
        var plus = InternalWorkpieceFrame.Point3Normal(P1, P2, new double[] { 360, 250, 0 }, Wd + 10, Wd)!;
        var minus = InternalWorkpieceFrame.Point3Normal(P1, P2, new double[] { 360, -250, 0 }, Wd - 10, Wd)!;
        for (var i = 0; i < 3; i++) Assert.Equal(plus.NormalBase[i], minus.NormalBase[i], 6);
        Assert.Equal(plus.RollDeg, minus.RollDeg, 6);
    }

    [Fact]
    public void 툴이_면_쪽으로_옮겨져_있으면_그만큼_뺀다()
    {
        // TCP 가 10mm 높은 자리에서 10mm 더 멀게 읽으면 면 높이는 같다.
        var r = InternalWorkpieceFrame.Point3Normal(P1, P2, new double[] { 360, 250, 10 }, Wd + 10, Wd)!;
        Assert.Equal(0, r.HeightMm, 6);
    }

    [Fact]
    public void Y_로_충분히_떨어지지_않으면_쓰지_않는다()
        => Assert.Null(InternalWorkpieceFrame.Point3Normal(P1, P2, new double[] { 360, 50, 0 }, Wd + 10, Wd));

    [Fact]
    public void 좌표계_Z_가_점3_법선을_따른다()
    {
        var r = InternalWorkpieceFrame.Point3Normal(P1, P2, new double[] { 360, 250, 0 }, Wd + 10, Wd)!;
        var frame = InternalWorkpieceFrame.Compute(P1, P2, 0, r.NormalBase);
        var o = FrameMath.FromFrame(new double[] { 0, 0, 0, 0, 0, 0 }, frame);
        var z = FrameMath.FromFrame(new double[] { 0, 0, 100, 0, 0, 0 }, frame);
        for (var i = 0; i < 3; i++) Assert.Equal(100 * r.NormalBase[i], z[i] - o[i], 4);
        // 법선 없이 잡으면 종전처럼 점1 툴 +Z.
        var legacy = InternalWorkpieceFrame.Compute(P1, P2, 0);
        var lz = FrameMath.FromFrame(new double[] { 0, 0, 100, 0, 0, 0 }, legacy);
        Assert.Equal(100, lz[2], 4);
    }

    [Fact]
    public void 점3_이동은_작업물_Y_방향이다()
    {
        var m = WObjPoint3Step.LateralOffsetBase(P1, P2, 250)!;
        Assert.Equal(0, m[0], 6);
        Assert.Equal(250, m[1], 6);
        Assert.Equal(0, m[2], 6);
    }

    [Fact]
    public void 묶음에_CROSS4_가_하나라도_있으면_표시한다()
    {
        var h = InspectionMoveDirection.Horizontal;
        BundleTaskInput T(string id, double x, bool cross)
            => new(id, id, "F", 0, h, new[] { x, 0, 1.0 }, new[] { x + 0.36, 0, 1.0 }, cross);
        var a = TaskBundlePlanner.Assign(new[] { T("h1", 0, false), T("h2", 0.36, true), T("h3", 0.72, false), });
        Assert.All(a, x => Assert.True(x.HasCross4));
        Assert.Equal(BundleRole.Leader, a[0].Role);

        var line = TaskBundlePlanner.Assign(new[] { T("h1", 0, false), T("h2", 0.36, false) });
        Assert.All(line, x => Assert.False(x.HasCross4));
    }

    [Theory]
    [InlineData(SeamTypeKind.Cross4L, true)]
    [InlineData(SeamTypeKind.Cross4R, true)]
    [InlineData(SeamTypeKind.Cross, true)]
    [InlineData(SeamTypeKind.Line, false)]
    public void CROSS4_종류를_가린다(SeamTypeKind kind, bool expected)
        => Assert.Equal(expected, TaskBundlePlanner.IsCross4(kind));
}
