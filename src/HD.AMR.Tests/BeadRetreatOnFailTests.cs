using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>용접선(비드) 못 찾을 때 후퇴 재검출 — 파라미터 해석.</summary>
public class BeadRetreatOnFailTests
{
    [Theory]
    [InlineData(null, true, 100)]
    [InlineData(null, false, 0)]       // 비전(용접검사기)은 비우면 끔 — 종전 동작
    [InlineData(0.0, true, 0)]         // 끔
    [InlineData(150.0, false, 150)]
    [InlineData(500.0, true, 100)]     // 범위 밖 → 기본값
    [InlineData(-10.0, false, 0)]
    public void 후퇴_거리를_해석한다(double? raw, bool gas, double expected)
        => Assert.Equal(expected, BeadFindStep.ResolveRetreatMm(raw, gas));
}
