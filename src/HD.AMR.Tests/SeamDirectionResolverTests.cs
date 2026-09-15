using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Tests;

public class SeamDirectionResolverTests
{
    private const double HalfPi = Math.PI / 2;

    // 수직벽(theta=π/2, 벽면 탄젠트 = −X축): 벽면-수평 seam → Horizontal.
    // §8.4 골든 예시 좌표 그대로 — seam 이 맵 X 방향, 노드 theta 1.571.
    [Fact]
    public void Resolve_GoldenExample_HorizontalSeam_IsHorizontal()
    {
        var dir = SeamDirectionResolver.Resolve(
            new[] { 12.510, 5.980, 1.420 }, new[] { 13.310, 5.980, 1.420 }, 1.571, out var reason);

        Assert.Equal(InspectionMoveDirection.Horizontal, dir);
        Assert.Contains("Horizontal", reason);
    }

    // 연직(z) seam 은 노드 theta 와 무관하게 Vertical.
    [Theory]
    [InlineData(0.0)]
    [InlineData(HalfPi)]
    [InlineData(Math.PI)]
    [InlineData(-HalfPi)]
    public void Resolve_VerticalSeam_AnyTheta_IsVertical(double theta)
    {
        var dir = SeamDirectionResolver.Resolve(
            new[] { 1.0, 2.0, 1.0 }, new[] { 1.0, 2.0, 2.0 }, theta, out _);

        Assert.Equal(InspectionMoveDirection.Vertical, dir);
    }

    // 벽면-수평 seam 은 theta 0/π/−π/2 에서도 Horizontal (탄젠트 방향이 theta 를 따라 회전).
    [Theory]
    [InlineData(0.0, 0.0, 1.0)]      // theta 0 → 탄젠트 (0,1): seam 을 y 방향으로
    [InlineData(Math.PI, 0.0, -1.0)] // theta π → 탄젠트 (0,−1)
    [InlineData(-HalfPi, 1.0, 0.0)]  // theta −π/2 → 탄젠트 (1,0)
    public void Resolve_TangentSeam_IsHorizontal(double theta, double dx, double dy)
    {
        var dir = SeamDirectionResolver.Resolve(
            new[] { 0.0, 0.0, 1.0 }, new[] { dx, dy, 1.0 }, theta, out _);

        Assert.Equal(InspectionMoveDirection.Horizontal, dir);
    }

    // 45° 챔퍼의 경사 방향(면내 상향) seam: 수평 성분이 벽 법선 방향(탄젠트 직교) + z —
    // 탄젠트 투영 du=0 이므로 Vertical.
    [Fact]
    public void Resolve_ChamferUpSlopeSeam_IsVertical()
    {
        // theta=0(벽 정면 +x) → 탄젠트 (0,1). seam = (0.5, 0, 0.5): 법선 수평 성분 + z.
        var dir = SeamDirectionResolver.Resolve(
            new[] { 0.0, 0.0, 0.0 }, new[] { 0.5, 0.0, 0.5 }, 0.0, out _);

        Assert.Equal(InspectionMoveDirection.Vertical, dir);
    }

    // 바닥(B): 탄젠트 방향 seam = Horizontal, 접근 방향(법선 수평) seam = Vertical.
    [Fact]
    public void Resolve_FloorSeams_TangentHorizontal_ApproachVertical()
    {
        // theta=0 → 탄젠트 (0,1), 접근 (1,0). 바닥 seam 은 z 성분 0.
        var tangent = SeamDirectionResolver.Resolve(
            new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 }, 0.0, out _);
        var approach = SeamDirectionResolver.Resolve(
            new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, 0.0, out _);

        Assert.Equal(InspectionMoveDirection.Horizontal, tangent);
        Assert.Equal(InspectionMoveDirection.Vertical, approach);
    }

    // 45°±10° 경계 — 모호 판정은 Horizontal 폴백 + 사유.
    [Fact]
    public void Resolve_AmbiguousDiagonal_FallsBackHorizontal()
    {
        // theta=π/2 → 탄젠트 (−1,0). seam (1,0,1): |du|=|dv| → 정확히 45°.
        var dir = SeamDirectionResolver.Resolve(
            new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 1.0 }, HalfPi, out var reason);

        Assert.Equal(InspectionMoveDirection.Horizontal, dir);
        Assert.Contains("45", reason);
    }

    // 경계 밖(45°+10° 초과)은 정상 판정.
    [Fact]
    public void Resolve_SteepDiagonal_OutsideBand_IsVertical()
    {
        // 60° 기울기: du=1, dv=√3 → 판정각 60° > 55°.
        var dir = SeamDirectionResolver.Resolve(
            new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, Math.Sqrt(3) }, HalfPi, out _);

        Assert.Equal(InspectionMoveDirection.Vertical, dir);
    }

    // 퇴화: 길이 10mm 미만 → Horizontal 폴백.
    [Fact]
    public void Resolve_TinySeam_FallsBackHorizontal()
    {
        var dir = SeamDirectionResolver.Resolve(
            new[] { 1.0, 1.0, 1.0 }, new[] { 1.0, 1.0, 1.005 }, 0.0, out var reason);

        Assert.Equal(InspectionMoveDirection.Horizontal, dir);
        Assert.Contains("길이", reason);
    }

    // 방어: 좌표 형식 이상([x,y,z] 아님) → Horizontal 폴백.
    [Fact]
    public void Resolve_MalformedCoordinates_FallsBackHorizontal()
    {
        var dir = SeamDirectionResolver.Resolve(
            new[] { 1.0, 2.0 }, new[] { 3.0, 4.0, 5.0 }, 0.0, out var reason);

        Assert.Equal(InspectionMoveDirection.Horizontal, dir);
        Assert.Contains("형식", reason);
    }
}
