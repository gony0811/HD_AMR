using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>벽면 후퇴 거리 파라미터(<see cref="SequenceEntry.RetractDistanceKey"/>) — 범위 밖·빈 값은 기본 300mm.</summary>
public class RetractDistanceParamTests
{
    [Theory]
    [InlineData(null, 300)]
    [InlineData(200.0, 200)]
    [InlineData(100.0, 100)]
    [InlineData(600.0, 600)]
    [InlineData(99.0, 300)]     // 범위 밖 → 기본
    [InlineData(601.0, 300)]
    [InlineData(double.NaN, 300)]
    public void 범위_밖이나_빈_값은_기본_300mm(double? value, double expected)
        => Assert.Equal(expected, SequenceEntry.ResolveRetractDistance(value));

    [Fact]
    public void 계획은_지정한_거리만큼_물러난다()
    {
        // 2026-10-30 SU 챔퍼 종료 자세 — 수평 후퇴, 거리만 바뀐다.
        var plan = SequenceEntry.PlanRetract(new[] { -1558.1, 93.5, 228.7, -180.0, 45.0, -178.2 }, distanceMm: 200);
        var dxy = Math.Sqrt(plan.OffsetBase[0] * plan.OffsetBase[0] + plan.OffsetBase[1] * plan.OffsetBase[1]);
        Assert.True(plan.Horizontal);
        Assert.Equal(200, dxy, 6);
    }
}
