using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class PitchLiftPlanTests
{
    // 실기 2026-11-21 00:46 M2: 점2 가 레이저로 10mm 가까웠다 → 30mm 들어올렸으면 20mm 만 내려 점1 거리와 같게.
    [Fact]
    public void CloserPoint2_LowersLess()
    {
        var (down, _) = BundleTeach.PlanPitchLower(30, z1: 393.6, z2: 383.6);
        Assert.Equal(20.0, down!.Value, 6);
    }

    [Fact]
    public void FartherPoint2_LowersMore()
    {
        var (down, _) = BundleTeach.PlanPitchLower(30, z1: 393.0, z2: 403.6);
        Assert.Equal(40.6, down!.Value, 6);
    }

    [Fact]
    public void NoLaser_LowersByLift()
    {
        Assert.Equal(30.0, BundleTeach.PlanPitchLower(30, null, 400).Down);
        Assert.Equal(30.0, BundleTeach.PlanPitchLower(30, 400, null).Down);
    }

    [Theory]
    [InlineData(400.0, 350.0)]   // 변화 −50mm > 한계 40
    [InlineData(400.0, 365.0)]   // 변화 −35mm — 들어올림 30mm 보다 가깝다 → 내림 음수
    public void UnsafeDrift_DoesNotLower(double z1, double z2)
        => Assert.Null(BundleTeach.PlanPitchLower(30, z1, z2).Down);
}
