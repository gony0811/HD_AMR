using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>
/// 작업물 좌표계 3점법 클라이언트 계산(<see cref="FairinoRpcClient.ComputeFramePose"/>) 검증.
/// 실물 재현 케이스: 코봇 페이지에서 점1 (-790.3, 58.8, -903.1) / 점2 (-790.3, -661.2, -903.2) /
/// 점3 (-790.4, -661.3, -853.2) 을 캡처했는데 컨트롤러 3점 버퍼가 점1을 (0,0,0)으로 기록해
/// (0,0,0, 0.1, 41.2, -140.1) 이 등록됐고, 그 원점(=베이스 원점) 이동이 112 를 냈다.
/// </summary>
public class WObjFramePoseTests
{
    private static readonly double[] P1 = { -790.3, 58.8, -903.1, 180, 0, 90 };
    private static readonly double[] P2 = { -790.3, -661.2, -903.2, 180, 0, 90 };
    private static readonly double[] P3 = { -790.4, -661.3, -853.2, 180, 0, 90 };

    [Fact]
    public void Method0_BasePoints_OriginIsPoint1_XAlongMinusY()
    {
        var pose = FairinoRpcClient.ComputeFramePose(P1, P2, P3, method: 0);

        Assert.Equal(P1[0], pose[0], 3);
        Assert.Equal(P1[1], pose[1], 3);
        Assert.Equal(P1[2], pose[2], 3);
        Assert.Equal(0.0, pose[3], 0);      // rx ≈ 0.1
        Assert.Equal(0.0, pose[4], 0);      // ry ≈ 0
        Assert.Equal(-90.0, pose[5], 0);    // rz: X축 = 베이스 -Y 방향
    }

    [Fact]
    public void Method0_OriginRecordedAsZero_ReproducesBadControllerFrame()
    {
        // 점1이 활성 작업물 프레임(#2 원점) 기준으로 (0,0,0) 기록된 경우 — 실물에서 등록된 값과 일치해야 한다.
        var pose = FairinoRpcClient.ComputeFramePose(new double[6], P2, P3, method: 0);

        Assert.Equal(0.0, pose[0], 3);
        Assert.Equal(0.0, pose[1], 3);
        Assert.Equal(0.0, pose[2], 3);
        Assert.Equal(41.2, pose[4], 1);
        Assert.Equal(-140.1, pose[5], 1);
    }

    [Fact]
    public void Method1_XYPlane_ZIsBasePlusZ()
    {
        var p3 = new[] { -790.3 + 100, 58.8, -903.1, 0, 0, 0 };   // 원점에서 베이스 +X 로 100mm (XY 평면 위)
        var pose = FairinoRpcClient.ComputeFramePose(P1, P2, p3, method: 1);
        // x = -Y, 평면점 = +X → z = x × v = (-Y) × (+X) = +Z
        Assert.Equal(0.0, pose[3], 0);
        Assert.Equal(0.0, pose[4], 0);
        Assert.Equal(-90.0, pose[5], 0);
    }

    [Fact]
    public void CoincidentPoints_Throw()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FairinoRpcClient.ComputeFramePose(P1, P1, P3, 0));
        Assert.Contains("가깝", ex.Message);
    }

    [Fact]
    public void CollinearPoints_Throw()
    {
        var mid = new[] { (P1[0] + P2[0]) / 2, (P1[1] + P2[1]) / 2, (P1[2] + P2[2]) / 2, 0, 0, 0 };
        var ex = Assert.Throws<InvalidOperationException>(() => FairinoRpcClient.ComputeFramePose(P1, P2, mid, 0));
        Assert.Contains("일직선", ex.Message);
    }

    [Fact]
    public void Point1PoseInNewFrame_RoundTripsToPoint1()
    {
        // 등록 후 오프셋 이동 프리필: 점1 자세를 새 프레임 기준으로 환산 → 원점 + 그 회전으로 FromFrame 하면 점1 포즈.
        var frame = FairinoRpcClient.ComputeFramePose(P1, P2, P3, 0);
        var p1InFrame = FrameMath.ToFrame(P1, frame);
        Assert.Equal(0.0, p1InFrame[0], 6);
        Assert.Equal(0.0, p1InFrame[1], 6);
        Assert.Equal(0.0, p1InFrame[2], 6);

        var target = new[] { 0.0, 0.0, 0.0, p1InFrame[3], p1InFrame[4], p1InFrame[5] };
        var back = FrameMath.FromFrame(target, frame);
        for (int i = 0; i < 3; i++) Assert.Equal(P1[i], back[i], 6);
        // 회전 성분은 행렬 비교(오일러 다의성 회피)
        var mA = FrameMath.PoseToMatrix(P1);
        var mB = FrameMath.PoseToMatrix(back);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(mA[r, c], mB[r, c], 6);
    }
}
