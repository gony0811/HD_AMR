using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>
/// MoveJ desc 를 관절값 정기구학으로 채울 때 티칭 desc 와의 차이 판정 — 실기 2026-11-28 17:57~: 공구 #1 재보정 뒤
/// 작업 준비 위치 티칭 desc [-1142.22,-144.85,-374.52] 와 컨트롤러 정기구학 [-1140.11,-144.91,-374.58] 이 2.1mm
/// 어긋나 MoveJ 가 모두 rc=154 로 거부됐다.
/// </summary>
public class MoveJDescGapTests
{
    [Fact]
    public void Field_case_gap_is_about_2mm_and_logged()
    {
        var taught = new[] { -1142.22, -144.85, -374.52, -117.48, 89.56, -117.87 };
        var fk = new[] { -1140.11, -144.91, -374.58, -117.17, 89.56, -117.56 };
        var gap = FairinoRpcClient.DescPositionGapMm(taught, fk);
        Assert.InRange(gap, 2.0, 2.2);
        Assert.True(gap > FairinoRpcClient.DescGapLogMm);
    }

    [Fact]
    public void Same_position_has_no_gap()
        => Assert.Equal(0, FairinoRpcClient.DescPositionGapMm(new double[] { 1, 2, 3, 0, 0, 0 }, new double[] { 1, 2, 3, 90, 0, 0 }));
}
