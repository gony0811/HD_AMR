using HD.AMR.App.Service;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

public class InspectionHomePolicyTests
{
    [Fact]
    public void 정차의_마지막_검사가_실패하면_홈으로_복귀한다()
        => Assert.True(InspectionHomePolicy.ShouldReturnHome("startWeldInspection", false, isLastInspection: true, "inspectionFailed"));

    [Fact]
    public void 성공한_검사는_순서_안의_홈_복귀_단계가_처리하므로_따로_복귀하지_않는다()
        => Assert.False(InspectionHomePolicy.ShouldReturnHome("startWeldInspection", true, isLastInspection: true, null));

    [Fact]
    public void 중간_용접선의_일반_실패는_복귀하지_않는다()
        => Assert.False(InspectionHomePolicy.ShouldReturnHome("startWeldInspection", false, isLastInspection: false, "inspectionFailed"));

    [Theory]
    [InlineData("equipmentError")]
    [InlineData(CobotReachPrecheck.BlockedErrorType)]
    public void 뒤_검사를_실행하지_않게_되는_실패는_중간이어도_복귀한다(string errorType)
        => Assert.True(InspectionHomePolicy.ShouldReturnHome("startWeldInspection", false, isLastInspection: false, errorType));

    [Fact]
    public void 코봇_이동_시험은_접근_자세에_머무는_것이_목적이라_복귀하지_않는다()
        => Assert.False(InspectionHomePolicy.ShouldReturnHome("moveToSeamStart", false, isLastInspection: true, "inspectionFailed"));

    [Theory]
    [InlineData(6, 1, true)]    // 6개 중 1개 불가 — 나머지 5개 수행
    [InlineData(6, 5, true)]
    [InlineData(6, 0, true)]    // 따로는 다 닿는다 — 묶음만 풀고 수행
    [InlineData(6, 6, false)]   // 전부 불가 — 정차 전체 미실행
    [InlineData(1, 1, false)]
    [InlineData(0, 0, false)]
    public void 닿는_용접선이_하나라도_있으면_닿지_않는_것만_빼고_수행한다(int inspections, int unreachable, bool expected)
        => Assert.Equal(expected, ReachPartialPolicy.CanRunPartially(inspections, unreachable));
}
