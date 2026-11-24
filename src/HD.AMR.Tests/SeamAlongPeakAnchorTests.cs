using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 다른 줄 좌표계 재사용의 용접선 방향 위치 — 직각인 줄로 처음 넘어갈 때 Peak 실측으로 한 번 맞추고,
/// 다른 열로 옮길 때는 격자 기울기만큼 용접선 방향으로 따라간다(실기 2026-11-24 M2: 수직 줄이 위로 약 10mm 밀림).
/// </summary>
public class SeamAlongPeakAnchorTests
{
    private const double Deg = Math.PI / 180.0;

    /// <summary>벽 법선 = BASE +X(로봇 쪽), 수평 비드가 BASE −Y 로 가면서 <paramref name="tiltDeg"/> 만큼 올라가는 좌표계.</summary>
    private static double[] HorizontalFrame(double tiltDeg, double[] origin)
    {
        var (c, s) = (Math.Cos(tiltDeg * Deg), Math.Sin(tiltDeg * Deg));
        var m = new double[4, 4];
        // X = (0, −c, s), Y = Z × X = (0, −s, −c), Z = (1, 0, 0)
        m[0, 0] = 0; m[1, 0] = -c; m[2, 0] = s;
        m[0, 1] = 0; m[1, 1] = -s; m[2, 1] = -c;
        m[0, 2] = 1; m[1, 2] = 0; m[2, 2] = 0;
        m[0, 3] = origin[0]; m[1, 3] = origin[1]; m[2, 3] = origin[2];
        m[3, 3] = 1;
        return FrameMath.MatrixToPose(m);
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    private static double[] Add(double[] a, double[] b, double k) => new[] { a[0] + k * b[0], a[1] + k * b[1], a[2] + k * b[2] };

    [Fact]
    public void 수직_줄로_옮길_때_격자_기울기만큼_용접선_방향으로_따라간다()
    {
        // 2026-11-24 M2: 수평 비드 0.19°, 마지막 수평 줄 원점에서 옆 900mm·아래 1620mm 의 수직 줄 시작.
        const double tilt = 0.19;
        var origin = new[] { -1539.3, -292.6, 733.6 };
        var frame = HorizontalFrame(tilt, origin);
        var (c, s) = (Math.Cos(tilt * Deg), Math.Sin(tilt * Deg));
        double[] uReal = { 0, -c, s };   // 실제 수평 비드 방향
        double[] vReal = { 0, s, c };    // 실제 수직 용접선 방향(위)
        double[] from = { -1221.0, -200.0, 607.5 };
        double[] to = { from[0], from[1] + 900.0, from[2] - 1620.0 };   // ACS(수평 가정) 시작점 차이
        var truth = Add(Add(origin, uReal, -900.0), vReal, -1620.0);

        var (moved, rot, err, _, gridMm) = InternalWorkpieceFrame.MoveToOtherLine(frame, from, to, new[] { 0.0, 0.0, 1.0 });

        Assert.NotNull(moved);
        Assert.Equal(-90, rot, 6);
        Assert.Equal(tilt, err, 6);
        // 용접선(수직) 방향으로는 실제 격자 위에 놓인다 — 고치지 않으면 900·sin(0.19°) ≈ 3mm 높다.
        Assert.Equal(0, Dot(Sub(moved!, truth), vReal), 6);
        var alongErr = 900.0 * s + 1620.0 * (1 - c);   // 고치기 전 용접선 방향 오차(위로)
        Assert.Equal(2.99, alongErr, 2);
        Assert.Equal(-alongErr, gridMm, 6);
        // 옆 성분은 ACS 값 그대로(약 1620·sin 만큼 실제 격자와 다르다).
        Assert.Equal(-1620.0 * s + 900.0 * (1 - c), Dot(Sub(moved!, truth), uReal), 6);
        // 새 좌표계 X 는 실제 수직 방향.
        var x = FrameMath.PoseToMatrix(moved!);
        Assert.Equal(vReal[1], x[1, 0], 6);
        Assert.Equal(vReal[2], x[2, 0], 6);

        var (plain, _, _, _, plainMm) = InternalWorkpieceFrame.MoveToOtherLine(frame, from, to, new[] { 0.0, 0.0, 1.0 }, followGridTilt: false);
        Assert.Equal(alongErr, Dot(Sub(plain!, truth), vReal), 6);
        Assert.Equal(0, plainMm, 9);
    }

    [Fact]
    public void 다른_수직_열로_옮겨도_용접선_방향_위치가_유지된다()
    {
        const double tilt = 0.19;
        var frame = HorizontalFrame(tilt, new[] { -1539.3, -292.6, 733.6 });
        var (c, s) = (Math.Cos(tilt * Deg), Math.Sin(tilt * Deg));
        double[] uReal = { 0, -c, s };
        double[] vReal = { 0, s, c };
        double[] up = { 0.0, 0.0, 1.0 };
        double[] a = { -1221.0, -200.0, 607.5 };
        double[] b = { a[0], a[1] + 900.0, a[2] - 1620.0 };
        var (col1, _, _, _, _) = InternalWorkpieceFrame.MoveToOtherLine(frame, a, b, up);

        // 첫 열 원점이 실제 코루게이션 위에 있다고 보고(Peak 실측), 옆으로 1080mm·위로 0 인 둘째 열로 옮긴다.
        double[] c2 = { b[0], b[1] - 1080.0, b[2] };
        var truth = Add(col1!, uReal, 1080.0);
        var (col2, rot, _, _, gridMm) = InternalWorkpieceFrame.MoveToOtherLine(col1!, b, c2, up);

        Assert.NotNull(col2);
        Assert.Equal(0, rot, 6);
        Assert.Equal(0, Dot(Sub(col2!, truth), vReal), 6);     // 같은 코루게이션 높이 — 1080·sin(0.19°) ≈ 3.6mm 올라감
        Assert.Equal(1080.0 * s, gridMm, 6);
    }

    [Fact]
    public void 격자_기울기가_없으면_종전과_같다()
    {
        var frame = HorizontalFrame(0, new[] { -1500.0, 0.0, 700.0 });
        double[] from = { -1200.0, 0.0, 600.0 };
        double[] to = { -1200.0, 900.0, -1020.0 };
        var (a, _, _, _, mm) = InternalWorkpieceFrame.MoveToOtherLine(frame, from, to, new[] { 0.0, 0.0, 1.0 });
        var (b, _, _, _, _) = InternalWorkpieceFrame.MoveToOtherLine(frame, from, to, new[] { 0.0, 0.0, 1.0 }, followGridTilt: false);
        Assert.Equal(0, mm, 9);
        for (var i = 0; i < 6; i++) Assert.Equal(b![i], a![i], 9);
    }

    [Theory]
    [InlineData(0.0, false, false)]      // 교시한 줄과 같은 방향 — 교시 때 맞춤
    [InlineData(-90.0, false, true)]     // 직각인 줄로 처음
    [InlineData(90.0, false, true)]
    [InlineData(270.0, false, true)]
    [InlineData(-90.0, true, false)]     // 이미 맞춤(둘째 열)
    [InlineData(180.0, false, false)]
    [InlineData(-180.0, false, false)]
    public void 직각인_줄로_넘어와_아직_맞추지_않았을_때만_찾는다(double rotation, bool anchored, bool expected)
        => Assert.Equal(expected, InternalWorkpieceFrame.NeedsAlongAnchor(rotation, anchored));

    [Fact]
    public void 좌표계를_용접선_방향으로_옮긴다()
    {
        var frame = HorizontalFrame(0.19, new[] { -1573.2, 607.2, -885.9 });
        var moved = InternalWorkpieceFrame.ShiftAlongX(frame, -10.0);
        var (along, height) = InternalWorkpieceFrame.AlongAndHeight(frame, moved);
        Assert.Equal(-10.0, along, 6);
        Assert.Equal(0, height, 6);
        for (var i = 3; i < 6; i++) Assert.Equal(frame[i], moved[i], 9);
    }

    [Theory]
    [InlineData(null, true, true)]     // 비움 — 가스 켬
    [InlineData(null, false, false)]   // 비움 — 비전 끔
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void 사용_여부는_비우면_센서_모드를_따른다(bool? value, bool usesGas, bool expected)
        => Assert.Equal(expected, SeamAlongPeakAnchor.ResolveEnabled(value, usesGas));

    [Theory]
    [InlineData(null, 40.0)]
    [InlineData(25.0, 25.0)]
    [InlineData(2.0, 40.0)]      // 범위 밖
    [InlineData(500.0, 40.0)]
    [InlineData(double.NaN, 40.0)]
    public void 보정_한계(double? value, double expected)
        => Assert.Equal(expected, SeamAlongPeakAnchor.ResolveMaxShiftMm(value));

    [Fact]
    public void 잰_이격을_보정량으로_판정한다()
    {
        Assert.Equal(-10.2, SeamAlongPeakAnchor.Decide(-10.2, 0.1, -10.2, 40).Mm!.Value, 9);
        Assert.Equal(0.0, SeamAlongPeakAnchor.Decide(0.6, 0.0, 0.6, 40).Mm!.Value, 9);            // 흔들림 — 맞춘 것으로 본다
        Assert.Null(SeamAlongPeakAnchor.Decide(172.0, 0.5, 172.0, 40).Mm);                        // 옆 Peak
        Assert.Contains("한계", SeamAlongPeakAnchor.Decide(172.0, 0.5, 172.0, 40).Reject);
        Assert.Null(SeamAlongPeakAnchor.Decide(0.3, 12.0, 12.0, 40).Mm);                          // 진행축이 용접선과 직각
        Assert.Null(SeamAlongPeakAnchor.Decide(double.NaN, 0, 5, 40).Mm);
    }

    [Fact]
    public void 원점_정면_자세는_높이와_자세를_두고_검사캠_오프셋만큼_옮긴다()
    {
        var frame = HorizontalFrame(0.19, new[] { -1573.2, 607.2, -885.9 });
        // 첫 경유점: 원점에서 용접선 방향 5mm·옆 3mm·면 바깥 33.8mm, 툴 −Z 가 벽 쪽.
        var staged = FrameMath.FromFrame(new[] { 5.0, 3.0, 33.8, 0.0, 0.0, 90.0 }, frame);
        var shift = new[] { -20.0, 202.0, 0.0, 0.0, 0.0, 0.0 };

        var view = SeamAlongPeakAnchor.ViewPose(frame, staged, shift);

        // 오프셋을 되돌리면 원점 바로 위(같은 높이).
        var back = FrameMath.FromFrame(new[] { 20.0, -202.0, 0.0, 0.0, 0.0, 0.0 }, view);
        var rel = FrameMath.ToFrame(back, frame);
        Assert.Equal(0, rel[0], 6);
        Assert.Equal(0, rel[1], 6);
        Assert.Equal(33.8, rel[2], 6);
        for (var i = 3; i < 6; i++) Assert.Equal(staged[i], view[i], 6);
    }

    [Theory]
    [InlineData(33.8, 33.8)]   // 이미 충분히 높다 — 그대로
    [InlineData(0.0, 30.0)]    // 교시 높이 — 30mm 까지 들어 올림
    [InlineData(-16.2, 30.0)]  // 검사 팁 진입 깊이 — 30mm 까지
    [InlineData(80.0, 80.0)]
    public void 옆으로_가기_전에_최소_높이까지_들어_올린다(double stagedHeight, double expectedHeight)
    {
        var frame = HorizontalFrame(0.19, new[] { -1573.2, 607.2, -885.9 });
        var staged = FrameMath.FromFrame(new[] { 5.0, 3.0, stagedHeight, 0.0, 0.0, 180.0 }, frame);
        var outward = LineSurfaceHeightStep.OutwardNormal(frame, staged, ToolAxisDir.MinusZ);

        var lifted = SeamAlongPeakAnchor.LiftedPose(frame, staged, outward);

        var rel = FrameMath.ToFrame(lifted, frame);
        Assert.Equal(5.0, rel[0], 6);            // 옆으로는 움직이지 않는다
        Assert.Equal(3.0, rel[1], 6);
        Assert.Equal(expectedHeight, rel[2], 6);
        for (var i = 3; i < 6; i++) Assert.Equal(staged[i], lifted[i], 6);
        // 원점 정면 자세도 그 높이.
        var view = SeamAlongPeakAnchor.ViewPose(frame, lifted, new[] { -20.0, 202.0, 0.0, 0.0, 0.0, 0.0 });
        Assert.Equal(expectedHeight, FrameMath.ToFrame(view, frame)[2], 6);
    }

    private static SeamAlongPeakAnchor.BeadProbePoint Point(double at, double? bead)
        => new(at, new double[6], bead, bead is null ? null : new[] { 0.0, -bead.Value, 0.0 }, null, "");

    [Fact]
    public void 두_자리의_비드_옆_이격으로_경로_기울기를_구한다()
    {
        // 아래에서 비드가 +0.5mm, 1080mm 위에서 −5.5mm → 1m 에 −5.56mm, 1.8m 에 −10mm.
        var slope = SeamAlongPeakAnchor.BeadSlopeMmPerM(Point(0, 0.5), Point(1080, -5.5));
        Assert.Equal(-6.0 / 1.08, slope!.Value, 6);
        Assert.Equal(-10.0, slope.Value * 1.8, 6);
        // 평행이면 0.
        Assert.Equal(0.0, SeamAlongPeakAnchor.BeadSlopeMmPerM(Point(0, 5.0), Point(1080, 5.0))!.Value, 9);
    }

    [Fact]
    public void 한쪽을_못_쟀으면_기울기를_구하지_않는다()
    {
        Assert.Null(SeamAlongPeakAnchor.BeadSlopeMmPerM(Point(0, null), Point(1080, 3.0)));
        Assert.Null(SeamAlongPeakAnchor.BeadSlopeMmPerM(Point(0, 1.0), Point(1080, null)));
        Assert.Null(SeamAlongPeakAnchor.BeadSlopeMmPerM(Point(0, 1.0), Point(0, 2.0)));
    }

    [Theory]
    [InlineData(null, 1080.0)]
    [InlineData(720.0, 720.0)]
    [InlineData(100.0, 1080.0)]     // 범위 밖
    [InlineData(3000.0, 1080.0)]
    [InlineData(double.NaN, 1080.0)]
    public void 두_자리_사이_거리(double? value, double expected)
        => Assert.Equal(expected, SeamAlongPeakAnchor.ResolveBeadProbeSpanMm(value));

    [Theory]
    [InlineData(null, true, true)]     // 비움 — 가스 켬
    [InlineData(null, false, false)]   // 비움 — 비전 끔
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void 비드_측정_사용_여부는_비우면_센서_모드를_따른다(bool? value, bool usesGas, bool expected)
        => Assert.Equal(expected, SeamAlongPeakAnchor.ResolveBeadProbe(value, usesGas));

    [Fact]
    public void 떨어진_자리의_정면_자세는_용접선_방향으로만_옮겨진다()
    {
        var frame = HorizontalFrame(0.19, new[] { -1573.2, 607.2, -885.9 });
        var staged = FrameMath.FromFrame(new[] { 5.0, 3.0, 33.8, 0.0, 0.0, 180.0 }, frame);
        var shift = new[] { -20.0, 202.0, 0.0, 0.0, 0.0, 0.0 };

        var near = SeamAlongPeakAnchor.ViewPose(frame, staged, shift);
        var far = SeamAlongPeakAnchor.ViewPose(InternalWorkpieceFrame.ShiftAlongX(frame, 1080.0), staged, shift);

        var a = FrameMath.ToFrame(near, frame);
        var b = FrameMath.ToFrame(far, frame);
        Assert.Equal(1080.0, b[0] - a[0], 6);      // 용접선 방향으로 1080mm
        Assert.Equal(a[1], b[1], 6);               // 옆으로는 그대로
        Assert.Equal(a[2], b[2], 6);               // 높이 그대로
        for (var i = 3; i < 6; i++) Assert.Equal(near[i], far[i], 6);
    }

    [Fact]
    public void 툴_이동량을_좌표계_성분으로_바꾼다()
    {
        var frame = HorizontalFrame(0, new[] { -1573.2, 607.2, -885.9 });
        // 툴 X 가 프레임 X 와 반대(RZ 180°)인 순회 자세 — 툴 +X 10mm 는 용접선 방향 −10mm.
        var tool = FrameMath.FromFrame(new[] { 0.0, 0.0, 33.8, 0.0, 0.0, 180.0 }, frame);
        var (along, lateral) = SeamAlongPeakAnchor.ToFrameComponents(frame, tool, new[] { 10.0, 0.0, 0.0, 0.0, 0.0, 0.0 });
        Assert.Equal(-10.0, along, 6);
        Assert.Equal(0, lateral, 6);

        Assert.Equal(0, SeamAlongPeakAnchor.OpticalTiltDeg(frame, tool, ToolAxisDir.MinusZ), 4);
        var tilted = FrameMath.FromFrame(new[] { 0.0, 0.0, 33.8, 0.0, 12.0, 180.0 }, frame);
        Assert.Equal(12.0, SeamAlongPeakAnchor.OpticalTiltDeg(frame, tilted, ToolAxisDir.MinusZ), 4);
    }
}
