using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 수직 방향 기울기 한 점 실측 — 정적 기울기는 고정값이라 AMR 이 선 자리에 따라 틀린다(실기 2026-11-24 M2: 1.4° 설정에 실제 약 1.7°,
/// 1.6m 아래 줄에서 팁이 벽에서 9mm 멀고 위로 갈수록 붙음). 다른 줄로 처음 넘어간 줄에서 레이저로 벽 거리를 한 번 재서 바로잡는다.
/// </summary>
public class VerticalTiltMeasureTests
{
    private const double Deg = Math.PI / 180.0;

    /// <summary>벽 법선(좌표계 Z) = BASE +X(로봇 쪽), 좌표계 X = BASE +Z(수직 줄, 위로)인 좌표계.</summary>
    private static double[] VerticalFrame(double[] origin)
    {
        var m = new double[4, 4];
        // X = (0,0,1), Z = (1,0,0), Y = Z × X = (0,-1,0)
        m[2, 0] = 1; m[1, 1] = -1; m[0, 2] = 1;
        m[0, 3] = origin[0]; m[1, 3] = origin[1]; m[2, 3] = origin[2];
        m[3, 3] = 1;
        return FrameMath.MatrixToPose(m);
    }

    private static readonly double[] Pivot = { -1539.3, -234.7, 729.7 };

    [Fact]
    public void 아래_줄에서_벽이_더_멀면_기울기를_더한다()
    {
        // 교시한 줄(Z 730)에서 1,622mm 아래 — 벽이 좌표계 기대보다 9mm 멀다(프레임 −Z 쪽).
        var frame = VerticalFrame(new[] { -1566.1, 651.5, -891.3 });
        double[] point = { -1966.0, 651.5, -891.3 };

        var (deg, lever) = InternalWorkpieceFrame.TiltFromOffset(frame, Pivot, point, offsetMm: -9.0);

        Assert.Equal(-1621.0, lever, 6);
        Assert.NotNull(deg);
        Assert.Equal(Math.Asin(9.0 / 1621.0) / Deg, deg!.Value, 6);    // 약 +0.32° — 정적 기울기를 키우는 방향
        Assert.InRange(deg.Value, 0.31, 0.33);
    }

    [Fact]
    public void 구한_각만큼_기울이면_그_자리의_면_높이_차가_없어진다()
    {
        var frame = VerticalFrame(new[] { -1566.1, 651.5, -891.3 });
        // 벽 위 실측점 — 좌표계 기준 높이 −409mm(WD 400 기준 −400 이어야 하므로 차 −9mm).
        var point = new[] { frame[0] - 409.0, 651.5, -891.3 };
        var before = InternalWorkpieceFrame.AlongAndHeight(frame, point).Height;
        Assert.Equal(-409.0, before, 6);

        var (deg, _) = InternalWorkpieceFrame.TiltFromOffset(frame, Pivot, point, before - (-400.0));
        var corrected = InternalWorkpieceFrame.TiltAboutHorizontal(frame, Pivot, deg!.Value);

        // 보정 뒤 그 점의 높이는 기준(−400mm) — 좌표계 면이 벽에서 WD 만큼 떨어진다.
        Assert.Equal(-400.0, InternalWorkpieceFrame.AlongAndHeight(corrected, point).Height, 1);
        // 좌표계 원점은 벽 쪽(BASE −X)으로 약 9mm 들어간다 — 아래 줄에서 팁이 벽에서 멀던 것이 붙는다.
        Assert.InRange(corrected[0] - frame[0], -9.3, -8.7);
        // 교시한 줄 자리(회전축)는 움직이지 않는다.
        var pivotBefore = InternalWorkpieceFrame.AlongAndHeight(frame, Pivot).Height;
        var pivotAfter = InternalWorkpieceFrame.AlongAndHeight(corrected, Pivot).Height;
        Assert.Equal(pivotBefore, pivotAfter, 1);
    }

    [Fact]
    public void 벽이_더_가까우면_기울기를_줄인다()
    {
        // 정적 기울기가 과한 자리 — 아래 줄에서 벽이 기대보다 8.5mm 가깝다(프레임 +Z 쪽).
        var frame = VerticalFrame(new[] { -1566.1, 651.5, -891.3 });
        var (deg, _) = InternalWorkpieceFrame.TiltFromOffset(frame, Pivot, new[] { -1966.0, 651.5, -891.3 }, offsetMm: +8.5);
        Assert.InRange(deg!.Value, -0.31, -0.29);
    }

    [Fact]
    public void 교시한_줄보다_위에서도_같은_방향으로_구한다()
    {
        // 위 줄(Z 1,230 — 축에서 +500mm)에서 벽이 3mm 가깝다 = 위로 갈수록 벽이 더 다가온다 → 기울기를 더한다.
        var frame = VerticalFrame(new[] { -1522.6, 653.9, 1230.0 });
        var (deg, lever) = InternalWorkpieceFrame.TiltFromOffset(frame, Pivot, new[] { -1922.0, 653.9, 1230.0 }, offsetMm: +3.0);
        Assert.Equal(500.3, lever, 6);
        Assert.True(deg!.Value > 0);
    }

    [Theory]
    [InlineData(300.0)]     // 교시한 줄에서 300mm — 너무 가깝다
    [InlineData(-499.0)]
    public void 교시한_줄에서_가까우면_구하지_않는다(double dz)
    {
        var frame = VerticalFrame(new[] { -1540.0, 650.0, Pivot[2] + dz });
        var (deg, lever) = InternalWorkpieceFrame.TiltFromOffset(frame, Pivot, new[] { -1940.0, 650.0, Pivot[2] + dz }, 5.0);
        Assert.Null(deg);
        Assert.Equal(dz, lever, 6);
    }

    [Fact]
    public void 한_점_실측_한계는_1도()
    {
        var frame = VerticalFrame(new[] { -1566.1, 651.5, -891.3 });
        var (deg, _) = InternalWorkpieceFrame.TiltFromOffset(frame, Pivot, new[] { -1966.0, 651.5, -891.3 }, offsetMm: -34.0);
        Assert.True(Math.Abs(deg!.Value) > InternalWorkpieceFrame.MaxMeasuredTiltDeg);   // 34mm(코루게이션 높이)는 한계 밖 — 멈춘다
    }

    [Theory]
    [InlineData(30.0, 30.0)]     // 범위 안 — 움직이지 않고 그 자리에서 잰다
    [InlineData(34.0, 34.0)]
    [InlineData(-16.2, 20.0)]    // 검사 팁 진입 깊이 — 20mm 까지 들어 올림
    [InlineData(100.0, 40.0)]    // 연속 이동 이격 — 40mm 까지만 내림
    public void 측정_높이는_지금_높이를_범위_안으로만_맞춘다(double now, double expected)
        => Assert.Equal(expected, LineSurfaceHeightStep.TiltMeasureHeightMm(now));

    [Theory]
    [InlineData(null, true, true)]     // 비움 — 가스 켬
    [InlineData(null, false, false)]   // 비움 — 비전 끔
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void 사용_여부는_비우면_센서_모드를_따른다(bool? value, bool usesGas, bool expected)
        => Assert.Equal(expected, LineSurfaceHeightStep.ResolveTiltMeasure(value, usesGas));
}
