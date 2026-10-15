using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 드라이런 끝점 이동 — 점1 기준 평행 이동. 실기 2026-10-15 천장 T: 점1은 카메라 정렬로 ACS 보다 527mm
/// 위(Z 1777)였는데 끝점을 ACS 로 새로 환산해 Z 1250 으로 내려가 작업물 좌표계가 37° 기울었다.
/// </summary>
public class SeamEndParallelMoveTests
{
    [Fact]
    public void 천장_실기값_점1_높이를_유지하고_면_방향으로만_이동()
    {
        // 천장을 보는 자세(rx=180) — 툴 Z 축이 베이스 −Z, 면 법선은 베이스 Z.
        var point1 = new[] { -639.8, -930.2, 1777.4, 180.0, 0.0, 180.0 };
        var startBase = new[] { -639.7, -930.1, 1250.0 };
        var endBase = new[] { -599.7, -231.2, 1250.0 };

        var (pos, normalMm) = CobotSeamEndMoveStep.ParallelFromPoint1(point1, startBase, endBase, ToolAxisDir.MinusZ);

        Assert.Equal(1777.4, pos[2], 1);          // 점1 높이 유지
        Assert.Equal(-599.8, pos[0], 1);
        Assert.Equal(-231.3, pos[1], 1);
        Assert.Equal(0.0, normalMm, 1);
    }

    [Fact]
    public void ACS_벡터의_법선_성분은_버린다()
    {
        var point1 = new[] { 0.0, 0.0, 1000.0, 180.0, 0.0, 0.0 };
        var (pos, normalMm) = CobotSeamEndMoveStep.ParallelFromPoint1(
            point1, new[] { 0.0, 0.0, 0.0 }, new[] { 300.0, 0.0, -100.0 }, ToolAxisDir.PlusZ);

        Assert.Equal(300.0, pos[0], 6);
        Assert.Equal(1000.0, pos[2], 6);
        Assert.Equal(100.0, Math.Abs(normalMm), 6);
    }
}
