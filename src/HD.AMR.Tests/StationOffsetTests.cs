using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>ACS 이동 명령 정차 오프셋(<see cref="StationOffset"/>) — 맵 X(선수–선미)·Y(좌현–우현), mm.</summary>
public class StationOffsetTests
{
    [Fact]
    public void 밀리미터_오프셋을_미터_좌표에_더한다()
    {
        // 2026-11-16 R01C17 노드 (24.077, 2.56) 에서 벽 쪽(맵 −y)으로 100mm, 선미 쪽(맵 +x)으로 50mm.
        var (x, y) = StationOffset.Apply(24.077, 2.56, offsetXMm: 50, offsetYMm: -100);

        Assert.Equal(24.127, x, 6);
        Assert.Equal(2.46, y, 6);
    }

    [Fact]
    public void 오프셋_0이면_좌표_그대로()
    {
        var (x, y) = StationOffset.Apply(23.96, 2.634, 0, 0);
        Assert.Equal(23.96, x, 9);
        Assert.Equal(2.634, y, 9);
    }

    [Theory]
    [InlineData(null, 0.0)]
    [InlineData(150.0, 150.0)]
    [InlineData(-1000.0, -1000.0)]
    [InlineData(1000.1, 0.0)]       // 범위 밖 — 설정 실수로 보고 0
    [InlineData(-5000.0, 0.0)]
    [InlineData(double.NaN, 0.0)]
    public void 범위_밖이나_빈_값은_0(double? raw, double expected)
        => Assert.Equal(expected, StationOffset.Sanitize(raw));
}
