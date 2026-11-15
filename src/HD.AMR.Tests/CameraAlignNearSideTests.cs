using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>카메라 거리 정렬의 기대 거리 점검 — 가까운 쪽만 목표 거리까지 넓힌다(먼 쪽은 그대로).</summary>
public class CameraAlignNearSideTests
{
    private const double Expected = 680, Tol = 200, Target = 300;

    [Theory]
    // 2026-11-14 F L4-R02C04 — 벽이 모델보다 약 220 mm 가까웠다(측정 437~466). 종전에는 전부 거부.
    [InlineData(466, true)]
    [InlineData(437, true)]
    [InlineData(300, true)]     // 목표 거리 — 하한
    [InlineData(299, false)]    // 목표보다 가까움 — 뒤로 빠져야 하는 상황은 멈춘다
    [InlineData(250, false)]
    [InlineData(556, true)]     // 종전에도 통과하던 값
    [InlineData(880, true)]     // 먼 쪽 상한 그대로
    [InlineData(881, false)]
    [InlineData(1200, false)]   // ROI 안 먼 구조물 — 벽 쪽으로 많이 전진하게 되는 쪽은 그대로 막는다
    public void 가까운_쪽은_목표_거리까지_먼_쪽은_종전대로(double measured, bool ok)
        => Assert.Equal(ok, FlatSurfaceCenteringService.CheckExpectedDistance(measured, Expected, Tol, Target) is null);

    [Fact]
    public void 하한을_주지_않으면_종전처럼_대칭이다()
    {
        Assert.NotNull(FlatSurfaceCenteringService.CheckExpectedDistance(466, Expected, Tol));
        Assert.Null(FlatSurfaceCenteringService.CheckExpectedDistance(480, Expected, Tol));
    }

    [Fact]
    public void 목표가_기대_범위보다_멀어도_하한을_좁히지_않는다()
        => Assert.Null(FlatSurfaceCenteringService.CheckExpectedDistance(500, Expected, Tol, nearFloorMm: 600));

    [Fact]
    public void 모델보다_가까운_양은_허용폭을_벗어났을_때만_알린다()
    {
        Assert.Equal(214, FlatSurfaceCenteringService.CloserThanModelMm(466, Expected, Tol));
        Assert.Null(FlatSurfaceCenteringService.CloserThanModelMm(556, Expected, Tol));   // 허용폭 안 — 평소 편차
        Assert.Null(FlatSurfaceCenteringService.CloserThanModelMm(900, Expected, Tol));
        Assert.Null(FlatSurfaceCenteringService.CloserThanModelMm(466, null, Tol));
    }
}
