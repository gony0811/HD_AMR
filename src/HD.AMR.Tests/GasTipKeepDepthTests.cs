using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>검사 팁 진입 깊이 유지 — 앞 구역이 진입 깊이로 끝났으면 다음 구역은 빠졌다 들어오지 않는다.</summary>
public class GasTipKeepDepthTests
{
    private static readonly double[] Raw = { 0, 0, 0, 0, 0, 0 };     // 티칭 높이 첫 경유점
    private static readonly double[] Tip = { -10, 0, 0, 0, 0, 0 };   // 벽면 쪽 10mm 진입한 첫 경유점

    [Fact]
    public void 진입_깊이에_서_있으면_이미_진입한_상태다()
        => Assert.True(GasTipApproachStep.IsAlreadyIn(new double[] { -10, 0, 1, 0, 0, 0 }, Tip, Raw));

    [Fact]
    public void 티칭_높이에_서_있으면_진입한_상태가_아니다()
        => Assert.False(GasTipApproachStep.IsAlreadyIn(new double[] { 0, 0, 1, 0, 0, 0 }, Tip, Raw));

    [Fact]
    public void 멀리_있으면_진입한_상태가_아니다()
        => Assert.False(GasTipApproachStep.IsAlreadyIn(new double[] { -10, 0, 360, 0, 0, 0 }, Tip, Raw));
}
