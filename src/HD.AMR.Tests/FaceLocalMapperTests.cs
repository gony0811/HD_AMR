using HD.AMR.App.Communication.Vision;

namespace HD.AMR.Tests;

/// <summary>작업물 좌표(용접선 시작점 원점, X+ = 용접선) → 면-로컬 (u,v,h) 변환.</summary>
public class FaceLocalMapperTests
{
    [Fact]
    public void NullPlacement_KeepsIdentityMapping()
    {
        Assert.Equal((12, -35, 400), FaceLocalMapper.ToFaceLocal(1, 12.4, -34.6, 400.2, null));
        Assert.Equal(FaceLocalMapper.ToFaceLocal(3, 1, 2, 3), FaceLocalMapper.ToFaceLocal(3, 1, 2, 3, null));
    }

    [Theory]
    // 원점 (1000, 2000) mm, 경유점 X=100(용접선 방향), Y=10(면내 직교), Z=400
    [InlineData(FaceSeamDirection.PlusU, 1100, 2010)]   // ex=(+1,0), ey=(0,+1)
    [InlineData(FaceSeamDirection.MinusU, 900, 1990)]   // ex=(−1,0), ey=(0,−1)
    [InlineData(FaceSeamDirection.PlusV, 990, 2100)]    // ex=(0,+1), ey=(−1,0)
    [InlineData(FaceSeamDirection.MinusV, 1010, 1900)]  // ex=(0,−1), ey=(+1,0)
    public void Placement_RotatesAndOffsetsByOrigin(FaceSeamDirection dir, int expectedU, int expectedV)
    {
        var placement = new FaceSeamPlacement(1000, 2000, dir);
        var (u, v, h) = FaceLocalMapper.ToFaceLocal(1, 100, 10, 400, placement);
        Assert.Equal(expectedU, u);
        Assert.Equal(expectedV, v);
        Assert.Equal(400, h);   // h 는 면 법선 성분 그대로
    }

    [Fact]
    public void Placement_WaypointAtOrigin_IsSeamStartFaceCoordinate()
    {
        var placement = new FaceSeamPlacement(3120, 1420, FaceSeamDirection.MinusV);
        Assert.Equal((3120, 1420, 0), FaceLocalMapper.ToFaceLocal(4, 0, 0, 0, placement));
    }

    [Fact]
    public void Placement_InvalidDirection_Throws()
    {
        var placement = new FaceSeamPlacement(0, 0, (FaceSeamDirection)9);
        Assert.Throws<ArgumentOutOfRangeException>(() => FaceLocalMapper.ToFaceLocal(1, 0, 0, 0, placement));
    }
}
