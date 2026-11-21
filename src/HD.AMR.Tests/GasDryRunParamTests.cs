using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>가스 측정 없는 드라이런 파라미터 해석 — on 계열만 켬, 빈 값·그 밖은 끔.</summary>
public class GasDryRunParamTests
{
    [Theory]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("yes", true)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("maybe", false)]
    public void 값을_해석한다(string? raw, bool expected)
        => Assert.Equal(expected, InspectionRunStep.ResolveGasDryRun(raw));
}
