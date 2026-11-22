using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>가스 무정지 순회 평활화 옵션(FIR 필터·블렌딩 방식) 파라미터 해석.</summary>
public class NonStopSmoothingOptionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("false")]
    public void Fir_비었거나_off_면_끔(string? raw)
        => Assert.Null(InspectionRunStep.ResolveFir(raw));

    [Theory]
    [InlineData("on")]
    [InlineData("1")]
    [InlineData("TRUE")]
    public void Fir_on_이면_문서_예제값(string raw)
        => Assert.Equal(new[] { 1000.0, 1000.0, 1000.0, 1000.0 }, InspectionRunStep.ResolveFir(raw));

    [Fact]
    public void Fir_숫자_4개를_그대로_쓴다()
        => Assert.Equal(new[] { 200.0, 500.0, 800.5, 1000.0 }, InspectionRunStep.ResolveFir(" 200, 500 ,800.5,1000 "));

    [Theory]
    [InlineData("200,500,800")]
    [InlineData("200,500,800,1000,5")]
    [InlineData("200,-1,800,1000")]
    [InlineData("200,0,800,1000")]
    [InlineData("a,b,c,d")]
    public void Fir_형식이_틀리면_끔(string raw)
        => Assert.Null(InspectionRunStep.ResolveFir(raw));

    [Fact]
    public void 순회_툴_오프셋은_숫자_3개를_그대로_쓴다()
        => Assert.Equal(new[] { 0.0, -20.0, 1.5 }, InspectionRunStep.ResolveScanToolOffset(" 0, -20 ,1.5 "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0,0,0")]
    [InlineData("0,-20")]
    [InlineData("0,-20,0,0")]
    [InlineData("0,-60,0")]
    [InlineData("a,b,c")]
    [InlineData("0,NaN,0")]
    public void 순회_툴_오프셋은_비었거나_0이거나_형식이_틀리면_없음(string? raw)
        => Assert.Null(InspectionRunStep.ResolveScanToolOffset(raw));

    [Fact]
    public void 툴_좌표_오프셋은_자세를_유지하고_툴_축_방향으로만_옮긴다()
    {
        var pose = new[] { -1400.0, 100.0, 700.0, 91.0, -3.0, 88.0 };
        var moved = HD.AMR.App.Communication.FrameMath.FromFrame(new[] { 0.0, -20.0, 0.0, 0.0, 0.0, 0.0 }, pose);
        var m = HD.AMR.App.Communication.FrameMath.PoseToMatrix(pose);
        for (var i = 0; i < 3; i++)
            Assert.Equal(pose[i] - 20.0 * m[i, 1], moved[i], 6);
        var mm = HD.AMR.App.Communication.FrameMath.PoseToMatrix(moved);
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                Assert.Equal(m[r, c], mm[r, c], 6);
    }

    [Fact]
    public void 연속_이동_원점_오프셋은_순회_경유점_오프셋과_같은_변위다()
    {
        // 순회 경유점: 좌표계 원점 자리의 툴 pose 에 툴 좌표 오프셋을 준 것.
        var toolPose = new[] { -1425.2, -282.8, 735.5, -89.9, 89.8, -89.6 };
        var offset = new[] { 10.0, -18.0, 0.0 };
        var scanTarget = HD.AMR.App.Communication.FrameMath.FromFrame(new[] { offset[0], offset[1], offset[2], 0.0, 0.0, 0.0 }, toolPose);

        var frame = new[] { toolPose[0], toolPose[1], toolPose[2], 1.0, 2.0, 3.0 };
        var moved = CobotInspectionMoveStep.OffsetOriginByTool(frame, toolPose, offset);

        for (var i = 0; i < 3; i++)
            Assert.Equal(scanTarget[i], moved[i], 6);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, moved[3..]);          // 자세 성분은 그대로
        Assert.Equal(-1425.2, frame[0], 9);                          // 원본은 건드리지 않는다
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData(" 1 ", 1)]
    [InlineData("2", 0)]
    [InlineData("on", 0)]
    public void 블렌딩_방식은_1만_모서리_전이(string? raw, int expected)
        => Assert.Equal(expected, InspectionRunStep.ResolveBlendMode(raw));
}
