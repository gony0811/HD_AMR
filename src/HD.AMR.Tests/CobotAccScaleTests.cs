using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>코봇 전역 가속도 배율 파라미터(cobot.accScalePercent) 해석.</summary>
public class CobotAccScaleTests : IDisposable
{
    // 정적 값이라 다른 테스트에 새지 않게 되돌린다.
    public void Dispose() => FairinoRpcClient.ApplyBaseOaccScale(null);

    [Theory]
    [InlineData("40", 40.0)]
    [InlineData("1", 1.0)]
    [InlineData("100", 100.0)]
    [InlineData("37.5", 37.5)]
    public void 범위_안의_값은_그대로_쓴다(string raw, double expected)
    {
        Assert.True(FairinoRpcClient.ApplyBaseOaccScale(raw));
        Assert.Equal(expected, FairinoRpcClient.BaseOaccScalePct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void 비어_있으면_100이고_정상으로_본다(string? raw)
    {
        FairinoRpcClient.ApplyBaseOaccScale("40");
        Assert.True(FairinoRpcClient.ApplyBaseOaccScale(raw));
        Assert.Equal(FairinoRpcClient.DefaultOaccScalePct, FairinoRpcClient.BaseOaccScalePct);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("150")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void 잘못된_값은_100을_쓰고_false(string raw)
    {
        FairinoRpcClient.ApplyBaseOaccScale("40");
        Assert.False(FairinoRpcClient.ApplyBaseOaccScale(raw));
        Assert.Equal(FairinoRpcClient.DefaultOaccScalePct, FairinoRpcClient.BaseOaccScalePct);
    }
}
