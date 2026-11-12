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
}
