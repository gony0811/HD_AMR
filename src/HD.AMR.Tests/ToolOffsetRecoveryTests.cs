using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// 지워진 공구 정의 복원 — 교시 위치의 (플랜지 pose, TCP pose) 쌍에서 <c>inv(T_F)·T_T</c> 로 오프셋을 되짚는다.
/// 정방향 모델 <c>T_T = T_F · offset</c> 로 표본을 만들어 라운드트립을 고정한다.
/// </summary>
public class ToolOffsetRecoveryTests
{
    private static double[] Compose(double[] flange, double[] offset)
        => FrameMath.MatrixToPose(FrameMath.Multiply(FrameMath.PoseToMatrix(flange), FrameMath.PoseToMatrix(offset)));

    [Theory]
    [InlineData(-752.6, -148.6, -534.9, 95.63, 88.13, 95.23)]     // 실제 home 교시 자세(짐벌락 근처)
    [InlineData(-920.8, 236.9, -882.8, -0.84, -1.96, -89.6)]      // inspectionGround
    [InlineData(600, 0, 300, 180, 0, 0)]
    [InlineData(500, 200, 400, 120, -35, 60)]
    public void ComputeOffset_RoundTripsThroughFlangePose(double x, double y, double z, double rx, double ry, double rz)
    {
        double[] flange = [x, y, z, rx, ry, rz];
        double[] offset = [12.5, -3.25, 187.4, 0.8, -1.2, 90.0];   // 레이저 헤드·카메라 같은 실제 크기의 TCP

        var tcp = Compose(flange, offset);
        var back = ToolOffsetRecoveryService.ComputeOffset(flange, tcp);

        Assert.True(FrameMath.DistanceMm(back, offset) < 1e-6, $"위치 [{string.Join(",", back)}]");
        Assert.True(FrameMath.RelativeRotationDeg(back, offset) < 1e-6, $"회전 [{string.Join(",", back)}]");
    }

    [Fact]
    public void ComputeOffset_IsIdentityWhenTcpEqualsFlange()
    {
        double[] flange = [100, -50, 300, 178, 3, -40];
        var off = ToolOffsetRecoveryService.ComputeOffset(flange, flange);
        Assert.All(off, v => Assert.True(Math.Abs(v) < 1e-9));
    }

    [Fact]
    public void Spread_IsZeroForIdenticalOffsets_AndReportsTheOddOne()
    {
        double[] a = [10, 20, 150, 0, 0, 45];
        Assert.Equal((0.0, 0.0), ToolOffsetRecoveryService.Spread([a, a, a]));

        double[] b = [10, 20, 152, 0, 0, 45.5];
        var (pos, rot) = ToolOffsetRecoveryService.Spread([a, a, b]);
        Assert.True(Math.Abs(pos - 2.0) < 1e-9, $"pos {pos}");
        Assert.True(Math.Abs(rot - 0.5) < 1e-9, $"rot {rot}");
    }

    [Fact]
    public void Spread_SingleRow_IsZero()
        => Assert.Equal((0.0, 0.0), ToolOffsetRecoveryService.Spread([new double[] { 1, 2, 3, 4, 5, 6 }]));
}
