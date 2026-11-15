using HD.AMR.App.Communication;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ⑧ Peak2 이동 — 툴이 ④ 평탄면 정렬로 기울어 있어도 점2 가 점1 과 같은 면 거리를 유지해야 한다.
/// 실기 2026-11-09 17:00:46 L4-R08C08 #1: 앵커 [-360.04, 1491.0, 1189.92, 179.91, 0.76, 87.95] 에서 툴-X +360mm →
/// BASE Z 1194.65(+4.73mm), 작업물 좌표계 ry −0.76° → 검사·다음 LINE 마다 4.8mm 씩 면(T, BASE +Z)에 가까워짐.
/// </summary>
public class PeakApproachWallParallelTests
{
    private static readonly double[] Anchor = { -360.04, 1491.0, 1189.92, 179.91, 0.76, 87.95 };
    private static readonly double[] WallOutT = { 0.0, 0.0, -1.0 };   // 상부 면 T: 면 바깥 = BASE −Z

    private static double[] ToolMoveInBase(double[] pose, double[] toolOffset)
    {
        var m = FrameMath.PoseToMatrix(pose);
        var v = new double[3];
        for (var i = 0; i < 3; i++)
            v[i] = m[i, 0] * toolOffset[0] + m[i, 1] * toolOffset[1] + m[i, 2] * toolOffset[2];
        return v;
    }

    [Fact]
    public void 툴축_그대로면_실기처럼_면_쪽으로_4_7mm_섞인다()
    {
        var v = ToolMoveInBase(Anchor, new[] { -360.0, 0, 0 });
        Assert.InRange(Math.Abs(v[2]), 4.5, 5.0);   // 실기 +4.73mm (부호는 툴축 매핑에 따라)
    }

    [Theory]
    [InlineData(-360.0, 0.0)]
    [InlineData(360.0, 0.0)]
    [InlineData(0.0, -210.0)]   // 검사캠 시프트 같은 툴 Y 이동도
    public void 면_법선_성분을_빼고_길이는_유지한다(double tx, double ty)
    {
        var raw = ToolMoveInBase(Anchor, new[] { tx, ty, 0 });
        var move = WeldSequenceSupport.ToolOffsetParallelToWall(Anchor, new[] { tx, ty, 0, 0, 0, 0 }, WallOutT);

        Assert.NotNull(move);
        Assert.Equal(0.0, move![2], 9);                                        // BASE Z(면 거리) 그대로
        var len = Math.Sqrt(move[0] * move[0] + move[1] * move[1] + move[2] * move[2]);
        Assert.Equal(Math.Sqrt(tx * tx + ty * ty), len, 6);                    // pitch 길이 유지
        // 면 위 방향은 툴축 이동의 면 투영과 같다(옆으로 틀어지지 않음).
        var rawXy = Math.Sqrt(raw[0] * raw[0] + raw[1] * raw[1]);
        Assert.Equal(raw[0] / rawXy, move[0] / len, 9);
        Assert.Equal(raw[1] / rawXy, move[1] / len, 9);
    }

    [Fact]
    public void 비스듬한_면도_법선_거리를_유지한다()
    {
        var wallOut = new[] { 0.6, 0.0, -0.8 };
        var anchor = new[] { 100.0, 200.0, 300.0, 150.0, 20.0, 45.0 };
        var move = WeldSequenceSupport.ToolOffsetParallelToWall(anchor, new[] { 0.0, 360.0, 0, 0, 0, 0 }, wallOut);

        Assert.NotNull(move);
        Assert.Equal(0.0, move![0] * 0.6 + move[2] * -0.8, 9);
        Assert.Equal(360.0, Math.Sqrt(move[0] * move[0] + move[1] * move[1] + move[2] * move[2]), 6);
    }

    [Fact]
    public void 면_법선과_평행한_이동이면_null()
    {
        // 툴 Z(광축) 로만 가는 이동은 면 평행 성분이 없다 → 호출측이 종전 툴축 이동으로 폴백.
        var pose = new[] { 0.0, 0.0, 0.0, 180.0, 0.0, 0.0 };   // 툴 Z = BASE −Z
        Assert.Null(WeldSequenceSupport.ToolOffsetParallelToWall(pose, new[] { 0.0, 0, 50, 0, 0, 0 }, WallOutT));
        Assert.Null(WeldSequenceSupport.ToolOffsetParallelToWall(pose, new double[6], WallOutT));
        Assert.Null(WeldSequenceSupport.ToolOffsetParallelToWall(pose, new[] { 10.0, 0, 0, 0, 0, 0 }, new double[3]));
    }
}
