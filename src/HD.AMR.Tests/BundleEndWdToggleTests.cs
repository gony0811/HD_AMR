using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>⑰⁺ 끝 WD 확인 on/off 파라미터 — 가스 검사는 구역을 끊김 없이 이어 순회하려고 기본으로 끈다.</summary>
public class BundleEndWdToggleTests
{
    [Theory]
    [InlineData(null, true, false)]     // 빈 값 → 가스 기본 끔
    [InlineData("", false, true)]       // 빈 값 → 비전 기본 켬
    [InlineData("  ", true, false)]
    [InlineData("on", true, true)]      // 명시값이 모드 기본보다 우선
    [InlineData("1", true, true)]
    [InlineData("TRUE", true, true)]
    [InlineData("off", false, false)]
    [InlineData("0", false, false)]
    [InlineData("false", true, false)]
    [InlineData("maybe", true, false)]  // 알 수 없는 값 → 모드 기본
    [InlineData("maybe", false, true)]
    public void 사용_여부를_해석한다(string? raw, bool usesGas, bool expected)
        => Assert.Equal(expected, BundleEndWdStep.ResolveEnabled(raw, usesGas));
}
