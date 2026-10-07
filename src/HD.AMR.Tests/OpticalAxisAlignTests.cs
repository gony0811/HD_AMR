using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ② 2단계 자세 — ready 자세의 광축만 면 법선으로 최소 회전하고 광축 둘레 비틀림(J6)은 유지하는지 고정한다.
/// 현장(2026-10-07): 광축 툴 −Z, ready 가 이미 전면벽 법선을 보고 있는데 계산 법선 자세를 통째로 써서
/// J4 가 크게 돌았다.
/// </summary>
public class OpticalAxisAlignTests
{
    // 현장 ready 티칭 자세 (툴 −Z 가 베이스 −X = 전면벽을 향함).
    private static readonly double[] Ready = { -1142.6, -145.9, -534.6, -115.78, 89.56, -116.18 };

    private static double AngleDeg(double[] p, double[] q)
    {
        var a = FrameMath.PoseToMatrix(p);
        var b = FrameMath.PoseToMatrix(q);
        double tr = 0;
        for (var i = 0; i < 3; i++)
            for (var k = 0; k < 3; k++) tr += a[k, i] * b[k, i];
        return Math.Acos(Math.Clamp((tr - 1) / 2, -1, 1)) * 180 / Math.PI;
    }

    private static double[] Col(double[] p, int c)
    {
        var m = FrameMath.PoseToMatrix(p);
        return new[] { m[0, c], m[1, c], m[2, c] };
    }

    [Fact]
    public void 광축이_이미_같으면_비틀림이_달라도_ready_자세를_유지한다()
    {
        // 계산 자세 = ready 를 광축(툴 Z) 둘레로 90° 비튼 것 — 광축 방향은 같다.
        var computed = FrameMath.FromFrame(new[] { 0.0, 0, 0, 0, 0, 90 }, Ready);
        var r = CobotInspectionMoveStep.AlignOpticalAxisKeepTwist(Ready, computed, ToolAxisDir.MinusZ);
        Assert.True(AngleDeg(r, Ready) < 0.01);
    }

    [Fact]
    public void 광축이_기울면_광축만_맞추고_비틀림은_최소로_바뀐다()
    {
        // 계산 자세 = ready 를 툴 X 둘레 10° 기울이고 광축 둘레 90° 비튼 것.
        var computed = FrameMath.FromFrame(new[] { 0.0, 0, 0, 10, 0, 90 }, Ready);
        var r = CobotInspectionMoveStep.AlignOpticalAxisKeepTwist(Ready, computed, ToolAxisDir.MinusZ);

        var zr = Col(r, 2);
        var zc = Col(computed, 2);
        for (var i = 0; i < 3; i++) Assert.Equal(zc[i], zr[i], 6);   // 광축 일치
        Assert.Equal(10.0, AngleDeg(r, Ready), 3);                    // 회전은 기울기 10° 뿐(비틀림 90° 미적용)
    }
}

/// <summary>
/// <see cref="FrameMath.MatrixToPose"/> 짐벌락(ry=±90°) 왕복 — 수직 용접선 법선 자세가 광축이 뒤집혀 나오던
/// 회귀(2026-10-07)를 고정한다.
/// </summary>
public class MatrixToPoseGimbalTests
{
    [Theory]
    [InlineData(-90.0, 90.0, 0.0)]
    [InlineData(-90.0, -90.0, 0.0)]
    [InlineData(37.0, 90.0, -12.0)]
    [InlineData(37.0, -90.0, -12.0)]
    [InlineData(-115.78, 89.56, -116.18)]
    [InlineData(10.0, 20.0, 30.0)]
    public void 짐벌락_포함_회전행렬_왕복이_같다(double rx, double ry, double rz)
    {
        var m = FrameMath.PoseToMatrix(new[] { 1.0, 2, 3, rx, ry, rz });
        var back = FrameMath.PoseToMatrix(FrameMath.MatrixToPose(m));
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 4; j++)
                Assert.Equal(m[i, j], back[i, j], 9);
    }
}
