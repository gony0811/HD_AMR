using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>⑱ 검사 수행의 경유점 순회 속도 결정 — 지정값 → 검사 프로파일 속도 → 기본 검사 속도 파라미터(기본 2%).</summary>
public class InspectionScanVelocityTests
{
    [Fact]
    public void ExplicitValueWins()
    {
        var (vel, source) = InspectionRunStep.ResolveScanVelocity(10, profileRunVel: 3, defaultParam: 2);

        Assert.Equal(10, vel);
        Assert.Equal("지정값", source);
    }

    [Fact]
    public void ProfileSpeedUsedWhenNoExplicitValue()
    {
        var (vel, source) = InspectionRunStep.ResolveScanVelocity(null, profileRunVel: 3, defaultParam: 2);

        Assert.Equal(3, vel);
        Assert.Equal("검사 프로파일 속도", source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DefaultParameterUsedWhenProfileHasNoSpeed(int profileRunVel)
    {
        var (vel, source) = InspectionRunStep.ResolveScanVelocity(null, profileRunVel, defaultParam: 4);

        Assert.Equal(4, vel);
        Assert.Equal("기본 검사 속도 파라미터", source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(150.0)]
    [InlineData(double.NaN)]
    public void MissingOrInvalidParameterFallsBackToTwoPercent(double? defaultParam)
    {
        var (vel, _) = InspectionRunStep.ResolveScanVelocity(null, profileRunVel: 0, defaultParam);

        Assert.Equal(2, vel);
        Assert.Equal(2, InspectionRunStep.DefaultInspectVelPercent);
    }

    [Fact]
    public void ValuesAreClampedToPercentRange()
    {
        Assert.Equal(100, InspectionRunStep.ResolveScanVelocity(250, 0, null).Percent);
        Assert.Equal(1, InspectionRunStep.ResolveScanVelocity(0, 0, null).Percent);
        Assert.Equal(100, InspectionRunStep.ResolveScanVelocity(null, 300, null).Percent);
    }

    // ── NG 구간 재측정 직행 이동 계획 ──

    [Fact]
    public void DirectTravel_FlatPath_GoesStraight()
    {
        var plan = InspectionRunStep.PlanDirectTravel(0, 0, new double[] { 0, 0.3, 0 });

        Assert.False(plan.Rise);
        Assert.False(plan.Descend);
    }

    [Fact]
    public void DirectTravel_IntermediateLowerThanBothEnds_GoesStraight()
    {
        // 사이 경유점이 면 쪽으로 더 들어가 있다(골) — 직선은 그보다 면에서 멀다.
        var plan = InspectionRunStep.PlanDirectTravel(33.8, 20, new double[] { 0, 5, 10 });

        Assert.False(plan.Rise);
        Assert.Equal(33.8, plan.SafeZ);
    }

    [Fact]
    public void DirectTravel_HigherPointBetween_LiftsOverWithClearance()
    {
        // 사이에 코로게이션 마루(33.8)가 있다 — 그 위 여유 높이로 올라가 건너간다.
        var plan = InspectionRunStep.PlanDirectTravel(0, 0, new double[] { 10, 33.8, 10 });

        Assert.True(plan.Rise);
        Assert.True(plan.Descend);
        Assert.Equal(33.8 + InspectionRunStep.DirectTravelClearanceMm, plan.SafeZ, 6);
    }

    [Fact]
    public void DirectTravel_AdjacentPoints_GoStraight()
    {
        var plan = InspectionRunStep.PlanDirectTravel(0, 33.8, Array.Empty<double>());

        Assert.False(plan.Rise);
        Assert.False(plan.Descend);
    }
}
