using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

public class WeldInspectionStepSelectionTests
{
    // 등록된 전체 스텝 키(DefaultOrder 순) — Program.cs / ServiceRegistration.cs 등록 집합과 같다.
    private static readonly string[] AllKeys =
    {
        "amrMove", "cobotInspection", "cameraAlign", "flatSurfaceAlign", "laserWorkingDistance",
        "peak1Find", "peak1Center", "bead1Find", "bead1Center", "wobjPoint1",
        "peak2Approach", "peak2Find", "peak2Center", "bead2Find", "bead2Center",
        "cobotSeamEnd", "wobjPoint2", "wobjRegister", "inspectionRun", "inspectionRunDry",
        "wobjReset", "cobotHome", "monitorClose",
    };

    // 일반 레시피 풀시퀀스: 드라이런 전용 스텝(끝점 이동·이동만 순회)은 빠지고 비전 정렬·정규 검사는 남는다.
    [Fact]
    public void FullSequence_NonDryRun_ExcludesDryRunOnlySteps()
    {
        var keys = WeldInspectionOrchestrator.SelectStepKeys(false, null, AllKeys, false, true);

        Assert.DoesNotContain("cobotSeamEnd", keys);
        Assert.DoesNotContain("inspectionRunDry", keys);
        Assert.Contains("flatSurfaceAlign", keys);
        Assert.Contains("wobjPoint2", keys);
        Assert.Contains("inspectionRun", keys);
        Assert.Contains("cobotHome", keys);
    }

    // ① AMR 위치 이동은 페이지 테스트 전용 — ACS 경로는 order 실행기가 이미 주행했으므로 어느 경우에도 빠진다.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmrMove_IsNeverSelectedForAcs(bool dryRun)
    {
        Assert.DoesNotContain("amrMove", WeldInspectionOrchestrator.SelectStepKeys(dryRun, null, AllKeys, false, true));
        Assert.DoesNotContain("amrMove",
            WeldInspectionOrchestrator.SelectStepKeys(false, new[] { "amrMove", "cobotInspection" }, AllKeys, false, true));
    }

    // 레시피가 명시한 키는 현장 튜닝 의도라 그대로 둔다.
    [Fact]
    public void ExplicitRecipeKeys_AreKeptAsIs()
    {
        var recipeKeys = new[] { "cobotInspection", "cobotSeamEnd", "inspectionRun" };
        var keys = WeldInspectionOrchestrator.SelectStepKeys(false, recipeKeys, AllKeys, false, true);

        Assert.Equal(recipeKeys, keys);
    }

    [Fact]
    public void DryRun_UsesFixedSetAndIgnoresRecipeKeys()
    {
        var keys = WeldInspectionOrchestrator.SelectStepKeys(true, new[] { "inspectionRun" }, AllKeys, false, true);

        Assert.Contains("cobotSeamEnd", keys);
        Assert.Contains("inspectionRunDry", keys);
        Assert.DoesNotContain("inspectionRun", keys);
        Assert.DoesNotContain("flatSurfaceAlign", keys);
    }

    // anchor 적중: 정렬 스텝군(⑤~⑯)은 빠지고 ②③④는 매 task 다시 실행한다.
    [Fact]
    public void AnchorHit_SkipsAlignmentButKeepsApproach()
    {
        var keys = WeldInspectionOrchestrator.SelectStepKeys(false, null, AllKeys, true, true);

        Assert.DoesNotContain("peak1Find", keys);
        Assert.DoesNotContain("wobjPoint1", keys);
        Assert.DoesNotContain("wobjRegister", keys);
        Assert.Contains("cobotInspection", keys);
        Assert.Contains("cameraAlign", keys);
        Assert.Contains("flatSurfaceAlign", keys);
        Assert.Contains("inspectionRun", keys);
    }

    [Fact]
    public void NotLastInspection_SkipsHomeReturn()
    {
        var keys = WeldInspectionOrchestrator.SelectStepKeys(false, null, AllKeys, false, false);

        Assert.DoesNotContain("cobotHome", keys);
        Assert.Contains("wobjReset", keys);
    }
}
