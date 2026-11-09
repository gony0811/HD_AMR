using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

public class SequenceSafetyTests
{
    [Fact]
    public void FullAutoExcludesDryRunOnlySteps()
    {
        var registered = new[]
        {
            "amrMove", "cobotInspection", "bead2Center", "cobotSeamEnd", "wobjPoint2",
            "inspectionRun", "inspectionRunDry", "wobjReset", "cobotHome",
        };
        var keys = SequenceService.FullAutoStepKeys(registered);

        Assert.DoesNotContain("cobotSeamEnd", keys);
        Assert.DoesNotContain("inspectionRunDry", keys);
        Assert.Equal(new[] { "amrMove", "cobotInspection", "bead2Center", "wobjPoint2", "inspectionRun", "wobjReset", "cobotHome" }, keys);
    }

    [Fact]
    public void RetractFailureMessageTellsOperatorToStop()
    {
        var msg = SequenceEntry.RetractFailedMessage("rc=112");
        Assert.Contains("rc=112", msg);
        Assert.Contains("정지", msg);
    }

    [Theory]
    [InlineData(500, 400, 100, true)]     // 전진 100 → 거리 100 감소: 정상
    [InlineData(500, 430, 100, true)]     // 70% 응답: 정상
    [InlineData(500, 480, 100, false)]    // 20% 응답: 멈춘 프레임/매핑 의심
    [InlineData(500, 600, 100, false)]    // 반대로 멀어짐: 부호 오류
    [InlineData(300, 400, -100, true)]    // 후진 100 → 거리 100 증가: 정상
    [InlineData(500, 500, 5, true)]       // 작은 이동은 검사 생략
    public void DistanceResponseCheck(double before, double after, double commanded, bool ok)
        => Assert.Equal(ok, FlatSurfaceCenteringService.CheckDistanceResponse(before, after, commanded) is null);

    [Fact]
    public void DistanceResponseFailsWithoutFreshMeasurement()
        => Assert.NotNull(FlatSurfaceCenteringService.CheckDistanceResponse(500, null, 100));

    [Theory]
    [InlineData(420, 400.0, 200, true)]
    [InlineData(900, 400.0, 200, false)]   // ROI 안 먼 구조물 — 벽 쪽으로 500mm 전진하려던 경우
    [InlineData(900, null, 200, true)]     // ② 미실행 — 검사 안 함
    [InlineData(900, 400.0, 0, true)]      // 허용폭 0 — 검사 끔
    public void ExpectedDistanceCheck(double measured, double? expected, double tol, bool ok)
        => Assert.Equal(ok, FlatSurfaceCenteringService.CheckExpectedDistance(measured, expected, tol) is null);

    [Theory]
    // 2026-11-09 현장 — ② 접근 400mm 직후 depth 측정(카메라–면). 접근 거리(TCP–면)만 기대값으로 쓰면 정상인데도 거부됐다.
    [InlineData(688, true)]    // 천장 T L4-R08C08 V01 (15:56 거부)
    [InlineData(691, true)]    // 같은 영역 V02 (15:58 거부)
    [InlineData(680, true)]    // 천장 T 오전 — 정상 정렬
    [InlineData(645, true)]    // 경사면 PU — 정상 정렬
    [InlineData(1200, false)]  // ROI 안 먼 구조물
    [InlineData(250, false)]   // 최소 측정거리 미만·가까운 구조물
    public void CameraAlignExpectsApproachPlusCameraOffset(double measured, bool ok)
    {
        var expected = CameraAlignStep.ExpectedStartDistanceMm(400, CameraAlignStep.ResolveDepthOffsetMm(null));
        Assert.Equal(680, expected);
        Assert.Equal(ok, FlatSurfaceCenteringService.CheckExpectedDistance(measured, expected, CameraAlignStep.DefaultExpectedToleranceMm) is null);
        // 종전(접근 거리만)이면 현장 정상값도 거부됐다.
        if (measured is 688 or 691)
            Assert.NotNull(FlatSurfaceCenteringService.CheckExpectedDistance(measured, 400, CameraAlignStep.DefaultExpectedToleranceMm));
    }

    [Theory]
    [InlineData(null, 280)]
    [InlineData(300.0, 300)]
    [InlineData(-5.0, 280)]     // 음수 — 기본값
    [InlineData(900.0, 280)]    // 범위 밖 — 기본값
    [InlineData(0.0, 0)]        // 0 — 카메라가 TCP 와 같은 높이(종전 동작)
    public void DepthOffsetParameterIsClamped(double? raw, double expected)
        => Assert.Equal(expected, CameraAlignStep.ResolveDepthOffsetMm(raw));

    [Fact]
    public void RetractGoesOppositeToOpticalAxis()
    {
        // 옆벽 자세(광축 수평) — 툴 Z 가 BASE −X(벽)를 향함.
        var pose = new[] { -1414.0, -174.8, -66.2, -5.1, 89.0, -2.0 };
        var minusZ = SequenceEntry.PlanRetract(pose, opticalAxis: ToolAxisDir.MinusZ);
        var plusZ = SequenceEntry.PlanRetract(pose, opticalAxis: ToolAxisDir.PlusZ);

        Assert.Equal(SequenceEntry.RetractDistanceMm, minusZ.ToolOffset[2], 6);    // 광축 −Z → 툴 +Z 후퇴(종전)
        Assert.Equal(-SequenceEntry.RetractDistanceMm, plusZ.ToolOffset[2], 6);    // 광축 +Z → 툴 −Z 후퇴
        for (var i = 0; i < 3; i++)
            Assert.Equal(-minusZ.OffsetBase[i], plusZ.OffsetBase[i], 6);

        var plusX = SequenceEntry.PlanRetract(pose, opticalAxis: ToolAxisDir.PlusX);
        Assert.Equal(-SequenceEntry.RetractDistanceMm, plusX.ToolOffset[0], 6);
        Assert.Equal(0, plusX.ToolOffset[2], 6);
    }

    [Theory]
    [InlineData(true, 400.0, 403.0, false)]
    [InlineData(true, 400.0, 420.0, true)]
    [InlineData(true, null, 400.0, true)]      // 캐시 시점 높이 모름
    [InlineData(true, 400.0, null, true)]      // 현재 높이 모름
    [InlineData(false, null, 400.0, false)]    // 캐시 없음 — 버릴 것도 없음
    public void AnchorStrokeInvalidation(bool cached, double? cachedMm, double? currentMm, bool invalidate)
        => Assert.Equal(invalidate, WeldInspectionOrchestrator.ShouldInvalidateForStroke(cached, cachedMm, currentMm));

    private static readonly string[] Registered =
        { "cobotInspection", "cameraAlign", "inspectionRun", "cobotSeamEnd", "inspectionRunDry", "wobjReset", "cobotHome" };

    [Fact]
    public void RecipeStepKeysValidation()
    {
        Assert.Null(WeldInspectionOrchestrator.ValidateRecipeStepKeys(null, Registered));
        Assert.Null(WeldInspectionOrchestrator.ValidateRecipeStepKeys(new[] { "cobotInspection", "inspectionRun", "cobotHome" }, Registered));
        Assert.Contains("cobotHome ", WeldInspectionOrchestrator.ValidateRecipeStepKeys(new[] { "cobotInspection", "cobotHome " }, Registered));
        Assert.Contains("드라이런", WeldInspectionOrchestrator.ValidateRecipeStepKeys(new[] { "cobotInspection", "cobotSeamEnd" }, Registered));
        Assert.Contains("cobotInspection", WeldInspectionOrchestrator.ValidateRecipeStepKeys(new[] { "inspectionRun" }, Registered));
    }

    [Fact]
    public void RecipeStepKeysJsonParsing()
    {
        Assert.Equal((null, null), WeldInspectionOrchestrator.ParseStepKeys(null));
        Assert.Equal(new[] { "a", "b" }, WeldInspectionOrchestrator.ParseStepKeys("[\"a\",\"b\"]").Keys);
        Assert.NotNull(WeldInspectionOrchestrator.ParseStepKeys("a,b").Error);
    }
}
