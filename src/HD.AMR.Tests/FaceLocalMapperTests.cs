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

    // 실기 2026-11-12 03:53 천장 L4-R03C17 CROSS4(CROSS-4-V1): 세로 팔 Y −195~−55 와 중심 4점이 음수 좌표로 나가
    // 비전이 ERR_POSITION 17건으로 거부했다. 면 좌표로 환산하면 시작점 면 좌표 근처의 정상 값이 된다.
    [Theory]
    // 천장(2)은 왼손 면 — +u 용접선의 작업물 +Y 는 −v 쪽
    [InlineData(2, FaceSeamDirection.PlusU, 205.0, -195.0, 5205, 3195)]
    [InlineData(2, FaceSeamDirection.PlusV, 100.0, 10.0, 5010, 3100)]
    // 바닥(1)은 오른손 면 — 같은 입력이 반대쪽
    [InlineData(1, FaceSeamDirection.PlusU, 205.0, -195.0, 5205, 2805)]
    // 우현 수직벽(4) 왼손, 좌현 수직벽(3) 오른손
    [InlineData(4, FaceSeamDirection.MinusU, 100.0, 10.0, 4900, 3010)]
    [InlineData(3, FaceSeamDirection.MinusU, 100.0, 10.0, 4900, 2990)]
    public void 면의_손잡이에_따라_작업물_Y_가_향하는_쪽이_바뀐다(
        int wallId, FaceSeamDirection dir, double x, double y, int expectedU, int expectedV)
    {
        var (u, v, _) = FaceLocalMapper.ToFaceLocal((ushort)wallId, x, y, 0, new FaceSeamPlacement(5000, 3000, dir));
        Assert.Equal((expectedU, expectedV), (u, v));
    }

    [Fact]
    public void 왼손_면은_천장과_우현_세_면이다()
        => Assert.Equal(new[] { 2, 4, 8, 10 },
            Enumerable.Range(1, 10).Where(i => FaceLocalMapper.IsLeftHanded((ushort)i)).ToArray());

    [Theory]
    [InlineData(5.0, 3.0, 5.36, 3.0, FaceSeamDirection.PlusU)]
    [InlineData(5.0, 3.0, 4.64, 3.001, FaceSeamDirection.MinusU)]
    [InlineData(5.0, 3.0, 5.0, 3.36, FaceSeamDirection.PlusV)]
    [InlineData(5.0, 3.0, 5.01, 2.64, FaceSeamDirection.MinusV)]
    public void ACS_시작_끝점_면_좌표에서_원점과_방향을_얻는다(double u0, double v0, double u1, double v1, FaceSeamDirection dir)
    {
        var p = FaceLocalMapper.PlacementFromSeam(u0, v0, u1, v1);
        Assert.NotNull(p);
        Assert.Equal((5000.0, 3000.0, dir), (p!.OriginUMm, p.OriginVMm, p.Direction));
    }

    [Fact]
    public void 끝점이_없거나_같은_점이거나_사선이면_환산하지_않는다()
    {
        Assert.Null(FaceLocalMapper.PlacementFromSeam(5.0, 3.0, null, null));      // 구버전 ACS
        Assert.Null(FaceLocalMapper.PlacementFromSeam(null, null, 5.36, 3.0));
        Assert.Null(FaceLocalMapper.PlacementFromSeam(5.0, 3.0, 5.0, 3.0));        // 시작 = 끝
        Assert.Null(FaceLocalMapper.PlacementFromSeam(5.0, 3.0, 5.3, 3.3));        // 45° 사선
    }

    [Fact]
    public void Placement_InvalidDirection_Throws()
    {
        var placement = new FaceSeamPlacement(0, 0, (FaceSeamDirection)9);
        Assert.Throws<ArgumentOutOfRangeException>(() => FaceLocalMapper.ToFaceLocal(1, 0, 0, 0, placement));
    }
}
