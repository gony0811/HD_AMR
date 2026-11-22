using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

/// <summary>다른 줄 좌표계 재사용(재교시 생략 옵션) — 좌표계 옮기기와 재사용 조건.</summary>
public class CrossLineFrameReuseTests
{
    // 벽면이 BASE −X 쪽에 선 경우를 닮은 프레임: 원점 (−1400, 0, 700), 면 법선(프레임 Z) = BASE +X 에서 조금 기운 방향.
    private static readonly double[] Frame = FrameMath.FromFrame(
        new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 12.0 }, new[] { -1400.0, 0.0, 700.0, 3.0, 88.0, -5.0 });

    private static double[] Axis(double[] frame, int col)
    {
        var m = FrameMath.PoseToMatrix(frame);
        return new[] { m[0, col], m[1, col], m[2, col] };
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double[] Add(double[] p, double[] v, double k) => new[] { p[0] + v[0] * k, p[1] + v[1] * k, p[2] + v[2] * k };

    [Fact]
    public void 세로줄로_바뀌면_프레임을_법선_둘레로_90도_돌린다()
    {
        var (x, y, z) = (Axis(Frame, 0), Axis(Frame, 1), Axis(Frame, 2));
        var from = Frame.Take(3).ToArray();
        // 다음 시작점: 직전 줄을 따라 720mm, 옆으로 0mm, 법선 방향 ACS 오차 4mm.
        var to = Add(Add(from, x, 720), z, 4);
        // 이번 용접선은 직전 좌표계 +Y 방향에서 3° 틀어진 ACS 방향.
        var rad = 93.0 * Math.PI / 180.0;
        var seamDir = new[] { x[0] * Math.Cos(rad) + y[0] * Math.Sin(rad), x[1] * Math.Cos(rad) + y[1] * Math.Sin(rad), x[2] * Math.Cos(rad) + y[2] * Math.Sin(rad) };

        var (moved, rotDeg, errDeg, normalMm) = InternalWorkpieceFrame.MoveToOtherLine(Frame, from, to, seamDir);

        Assert.NotNull(moved);
        Assert.Equal(90.0, rotDeg, 6);
        Assert.Equal(3.0, errDeg, 6);
        Assert.Equal(4.0, normalMm, 6);
        // 새 X = 옛 Y(ACS 방향이 아니라 직전 좌표계 축을 따른다), 새 Y = −옛 X, Z 는 그대로.
        Assert.Equal(1.0, Dot(Axis(moved!, 0), y), 6);
        Assert.Equal(-1.0, Dot(Axis(moved!, 1), x), 6);
        Assert.Equal(1.0, Dot(Axis(moved!, 2), z), 6);
        // 원점은 면을 따라서만 옮긴다 — 법선 성분 4mm 는 버린다(면 거리 유지).
        var d = new[] { moved![0] - from[0], moved[1] - from[1], moved[2] - from[2] };
        Assert.Equal(720.0, Dot(d, x), 6);
        Assert.Equal(0.0, Dot(d, y), 6);
        Assert.Equal(0.0, Dot(d, z), 6);
    }

    [Fact]
    public void 평행한_옆줄은_돌리지_않고_옆_성분까지_옮긴다()
    {
        var (x, y) = (Axis(Frame, 0), Axis(Frame, 1));
        var from = Frame.Take(3).ToArray();
        var to = Add(Add(from, x, 10), y, -720);

        var (moved, rotDeg, errDeg, _) = InternalWorkpieceFrame.MoveToOtherLine(Frame, from, to, x);

        Assert.NotNull(moved);
        Assert.Equal(0.0, rotDeg, 6);
        Assert.Equal(0.0, errDeg, 6);
        Assert.Equal(1.0, Dot(Axis(moved!, 0), x), 6);
        var d = new[] { moved![0] - from[0], moved[1] - from[1], moved[2] - from[2] };
        Assert.Equal(10.0, Dot(d, x), 6);
        Assert.Equal(-720.0, Dot(d, y), 6);
    }

    [Fact]
    public void 반대_방향_용접선은_180도_돌린다()
    {
        var x = Axis(Frame, 0);
        var from = Frame.Take(3).ToArray();
        var (moved, rotDeg, _, _) = InternalWorkpieceFrame.MoveToOtherLine(Frame, from, from, new[] { -x[0], -x[1], -x[2] });

        Assert.NotNull(moved);
        Assert.Equal(180.0, Math.Abs(rotDeg), 6);
        Assert.Equal(-1.0, Dot(Axis(moved!, 0), x), 6);
        Assert.Equal(1.0, Dot(Axis(moved!, 2), Axis(Frame, 2)), 6);
    }

    [Fact]
    public void 격자에서_벗어난_사선이나_면에_수직인_방향은_거부한다()
    {
        var (x, y, z) = (Axis(Frame, 0), Axis(Frame, 1), Axis(Frame, 2));
        var from = Frame.Take(3).ToArray();
        var diag = new[] { x[0] + y[0], x[1] + y[1], x[2] + y[2] };   // 45°

        Assert.Null(InternalWorkpieceFrame.MoveToOtherLine(Frame, from, from, diag).Frame);
        Assert.Null(InternalWorkpieceFrame.MoveToOtherLine(Frame, from, from, z).Frame);
    }

    [Theory]
    [InlineData(1, 0, 0, 0.0)]      // 같은 방향
    [InlineData(0, 1, 0, 0.0)]      // 직교
    [InlineData(-1, 0, 0, 0.0)]     // 반대
    [InlineData(1, 1, 0, 45.0)]     // 사선
    public void 격자_방향_오차(double bx, double by, double bz, double expected)
    {
        var o = new[] { 0.0, 0.0, 0.0 };
        Assert.Equal(expected, InternalWorkpieceFrame.GridAngleErrorDeg(o, new[] { 1.0, 0, 0 }, o, new[] { bx, by, bz })!.Value, 6);
    }

    [Fact]
    public void 격자_방향_오차는_끝점이_없으면_null()
    {
        var o = new[] { 0.0, 0.0, 0.0 };
        Assert.Null(InternalWorkpieceFrame.GridAngleErrorDeg(o, o, o, new[] { 1.0, 0, 0 }));
        Assert.Null(InternalWorkpieceFrame.GridAngleErrorDeg(o, new[] { 1.0, 0, 0 }, o, null));
    }

    [Fact]
    public void 사전_검증만_실패하면_물려받은_좌표계는_유지한다()
    {
        Assert.True(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, "inspectionRun", true));
        Assert.True(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, "inspectionRunDry", true));     // 드라이런 순회

        Assert.False(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(false, "inspectionRun", true));    // 이 시퀀스에서 새로 교시한 좌표계
        Assert.False(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, "inspectionRun", false));    // 움직인 뒤 실패
        Assert.False(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, "cobotInspection", true));   // 다른 단계 실패
        Assert.False(WeldInspectionOrchestrator.ShouldKeepAnchorOnFailure(true, null, true));
    }

    [Fact]
    public void 재사용은_같은_지령_같은_정차_같은_면에서만()
    {
        var last = ("order-1", (string?)"G1", (string?)"SM");
        Assert.True(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-1", "G1", "SM", true, 2.0));

        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(false, last, "order-1", "G1", "SM", true, 2.0));   // CORNER
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, null, "order-1", "G1", "SM", true, 2.0));    // 직전 좌표계 없음
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-2", "G1", "SM", true, 2.0));    // 다른 지령
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-1", "G2", "SM", true, 2.0));    // 다른 정차
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-1", "G1", "T", true, 2.0));     // 다른 면
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-1", "G1", null, true, 2.0));    // 면 미상
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-1", "G1", "SM", false, 2.0));   // 프레임·기준점 없음
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-1", "G1", "SM", true, 30.0));   // 사선
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-1", "G1", "SM", true, 90.0));   // 직교(수직↔수평)
        Assert.False(WeldInspectionOrchestrator.CanReuseAcrossLines(true, last, "order-1", "G1", "SM", true, null));   // 방향 미상
    }

    [Theory]
    [InlineData(1, 0, 0, 0.0)]      // 같은 방향
    [InlineData(-1, 0, 0, 0.0)]     // 반대 방향
    [InlineData(0, 1, 0, 90.0)]     // 직교 — 같은 축이 아니다
    [InlineData(1, 1, 0, 45.0)]     // 사선
    public void 같은_축_방향_오차(double bx, double by, double bz, double expected)
    {
        var o = new[] { 0.0, 0.0, 0.0 };
        Assert.Equal(expected, InternalWorkpieceFrame.ParallelAngleErrorDeg(o, new[] { 1.0, 0, 0 }, o, new[] { bx, by, bz })!.Value, 6);
        Assert.Null(InternalWorkpieceFrame.ParallelAngleErrorDeg(o, o, o, new[] { 1.0, 0, 0 }));
    }

    private static WeldInspectionOrchestrator.AxisFrame Axis(double[] startW, double[] endW, double tag)
        => new(startW, endW, new[] { tag, 0, 0, 0, 0, 0 }, new[] { tag, 0, 0 });

    [Fact]
    public void 수직과_수평은_좌표계를_따로_보관하고_같은_축_것만_고른다()
    {
        // 벽면(맵 X-Z 평면): 수평선은 X 방향, 수직선은 Z 방향.
        var frames = new List<WeldInspectionOrchestrator.AxisFrame>();
        var (h1s, h1e) = (new[] { 0.0, 0, 1.0 }, new[] { 0.72, 0, 1.0 });
        var (v1s, v1e) = (new[] { 0.72, 0, 1.0 }, new[] { 0.72, 0, 1.72 });

        // 수평만 교시한 상태 — 수직선은 물려받을 좌표계가 없다(한 번 교시).
        WeldInspectionOrchestrator.StoreAxisFrame(frames, Axis(h1s, h1e, 1));
        Assert.Null(WeldInspectionOrchestrator.FindAxisFrame(frames, v1s, v1e));

        // 수직을 교시해도 수평 좌표계는 남는다.
        WeldInspectionOrchestrator.StoreAxisFrame(frames, Axis(v1s, v1e, 2));
        Assert.Equal(2, frames.Count);

        // 다른 줄의 수평선(반대 방향 포함)은 수평 좌표계를, 수직선은 수직 좌표계를 받는다.
        Assert.Equal(1.0, WeldInspectionOrchestrator.FindAxisFrame(frames, new[] { 0.72, 0, 1.72 }, new[] { 0.0, 0, 1.72 })!.Frame[0]);
        Assert.Equal(2.0, WeldInspectionOrchestrator.FindAxisFrame(frames, new[] { 0.0, 0, 1.0 }, new[] { 0.0, 0, 1.72 })!.Frame[0]);

        // 같은 축을 다시 저장하면 그 축만 새 것으로 바뀐다.
        WeldInspectionOrchestrator.StoreAxisFrame(frames, Axis(new[] { 0.0, 0, 1.72 }, new[] { 0.72, 0, 1.72 }, 3));
        Assert.Equal(2, frames.Count);
        Assert.Equal(3.0, WeldInspectionOrchestrator.FindAxisFrame(frames, h1s, h1e)!.Frame[0]);
        Assert.Equal(2.0, WeldInspectionOrchestrator.FindAxisFrame(frames, v1s, v1e)!.Frame[0]);

        // 사선·끝점 없음은 어느 축도 아니다.
        Assert.Null(WeldInspectionOrchestrator.FindAxisFrame(frames, new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 1.0 }));
        Assert.Null(WeldInspectionOrchestrator.FindAxisFrame(frames, h1s, null));
    }
}
