using System.Runtime.InteropServices;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ④ 평탄면 정렬 안전 가드 — 실기 2026-11-24 13:30 M2: 레이저 CH3 가 −10.4mm 로 두 번 튐 판정된 뒤 세 번째 −9.995mm 가
/// 기준(10mm)을 통과해 가짜 틸트(−15.2°/19.6°)로 툴을 10°/10° 돌렸고, 벽 쪽으로 다가가 비상정지.
/// </summary>
public class FlatSurfaceTiltGuardTests
{
    [Fact]
    public void 실기_세_번째_측정은_평소_기준으로는_통과하지만_튐_이력이_있으면_걸린다()
    {
        // 1·2번째(−10.4mm)는 평소 기준으로도 튐.
        Assert.Equal(2, LaserTiltCorrector.FindOutlierChannel(new[] { 408.11, 408.70, 398.02 }));
        // 3번째(−9.995mm)는 평소 기준 10mm 에 못 미쳐 통과했다.
        var third = new[] { 407.66, 408.33, 398.00 };
        Assert.Equal(-1, LaserTiltCorrector.FindOutlierChannel(third));
        // 이 자리에서 이미 튐이 나왔으면 엄격한 기준으로 본다 — 걸린다.
        Assert.Equal(2, LaserTiltCorrector.FindOutlierChannel(third, LaserTiltCorrector.OutlierDeviationFor(sawOutlier: true, keepHistory: true)));
    }

    [Theory]
    [InlineData(false, true, 10.0)]    // 아직 튐 없음 — 평소 기준
    [InlineData(true, true, 5.0)]      // 튐 뒤 — 엄격한 기준
    [InlineData(true, false, 10.0)]    // 이력 유지 끔 — 종전
    [InlineData(false, false, 10.0)]
    public void 튐_판정_기준(bool sawOutlier, bool keepHistory, double expected)
        => Assert.Equal(expected, LaserTiltCorrector.OutlierDeviationFor(sawOutlier, keepHistory));

    [Fact]
    public void 엄격한_기준도_실제_기울기_몇_도는_튐으로_보지_않는다()
    {
        // 같은 날 정상 측정(rx 2.55° / ry −3.12°): 채널 차 1.65mm.
        Assert.Equal(-1, LaserTiltCorrector.FindOutlierChannel(new[] { 407.23, 407.19, 408.86 }, LaserTiltCorrector.OutlierDeviationAfterJumpMm));
        // rx 6.7°(12:54): 채널 차 약 3.3mm.
        Assert.Equal(-1, LaserTiltCorrector.FindOutlierChannel(new[] { 395.80, 398.87, 399.26 }, LaserTiltCorrector.OutlierDeviationAfterJumpMm));
    }

    [Theory]
    [InlineData(300.0, 264.0, 20.0, true)]     // 36mm 다가감 — 되돌린다
    [InlineData(300.0, 285.0, 20.0, false)]    // 15mm — 허용
    [InlineData(300.0, 280.0, 20.0, false)]    // 정확히 한계 — 허용
    [InlineData(300.0, 310.0, 20.0, false)]    // 멀어짐
    [InlineData(300.0, 264.0, 0.0, false)]     // 끔
    [InlineData(null, 264.0, 20.0, false)]     // 회전 전 값 없음 — 판정 안 함
    [InlineData(300.0, null, 20.0, false)]     // 회전 뒤 값 없음
    public void 회전_뒤_벽_접근_판정(double? before, double? after, double limit, bool expected)
        => Assert.Equal(expected, LaserTiltCorrector.ApproachedTooMuch(before, after, limit));

    [Theory]
    [InlineData(null, 20.0)]
    [InlineData(0.0, 0.0)]       // 끔
    [InlineData(35.0, 35.0)]
    [InlineData(-5.0, 20.0)]     // 범위 밖
    [InlineData(500.0, 20.0)]
    [InlineData(double.NaN, 20.0)]
    public void 벽_접근_한계_파라미터(double? value, double expected)
        => Assert.Equal(expected, FlatSurfaceAlignStep.ResolveMaxApproachMm(value));

    private static CameraFrame DepthFrame(int width, int height, Func<int, int, ushort> depth)
    {
        var px = new ushort[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                px[y * width + x] = depth(x, y);
        return new CameraFrame(MemoryMarshal.AsBytes(px.AsSpan()).ToArray(), width, height, "depth16", DateTime.UtcNow);
    }

    [Fact]
    public void 가까운_쪽_깊이는_픽셀_하나의_잡음에_끌려가지_않는다()
    {
        // 100×100: 대부분 350mm, 5%(위 5줄)는 300mm(코루게이션), 픽셀 하나만 120mm(잡음), 몇 개는 무효(0).
        var f = DepthFrame(100, 100, (x, y) =>
            x == 50 && y == 50 ? (ushort)120 : y < 5 ? (ushort)300 : x == 0 ? (ushort)0 : (ushort)350);

        var near = CameraService.NearDepthMm(f, 0, 0, 1, 1);
        Assert.Equal(300.0, near);                                   // 최솟값 120 이 아니라 가까운 1% 째
        Assert.Equal(350.0, CameraService.NearDepthMm(f, 0, 0.5, 1, 0.4));   // 코루게이션이 없는 영역
    }

    [Fact]
    public void 프레임이_없거나_유효_픽셀이_없으면_null()
    {
        Assert.Null(CameraService.NearDepthMm(null, 0, 0, 1, 1));
        Assert.Null(CameraService.NearDepthMm(DepthFrame(10, 10, (_, _) => 0), 0, 0, 1, 1));
    }
}
