using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>리프트를 쓰지 않는 운영 — 높이 지령을 무시하고 고정 높이로 계획한다.</summary>
public class LiftDisabledTests
{
    [Fact]
    public void 리프트가_꺼져_있으면_지령과_무관하게_고정_높이다()
    {
        Assert.Equal(0, TelescopicService.PlanStroke(enabled: false, disabledStrokeMm: 0, requestedMm: 500));
        Assert.Equal(120, TelescopicService.PlanStroke(false, 120, 0));
        Assert.Equal(0, TelescopicService.PlanStroke(false, 0, null));
    }

    [Fact]
    public void 리프트가_켜져_있으면_지령_그대로다()
    {
        Assert.Equal(500, TelescopicService.PlanStroke(true, 0, 500));
        Assert.Null(TelescopicService.PlanStroke(true, 0, null));
    }
}
