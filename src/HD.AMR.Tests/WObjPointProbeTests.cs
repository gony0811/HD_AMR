using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>작업물 좌표계 점1·점2 면 거리 진단(<see cref="WObjPointProbe"/>) — 스냅샷과 두 점 비교.</summary>
public class WObjPointProbeTests
{
    private static LaserChannelReading Ch(int n, double mm, bool enabled = true)
        => new(n, 0, mm, enabled, false, false, false, false, false, false);

    [Fact]
    public void 레이저_스냅샷은_범위_밖_채널을_NaN_으로_둔다()
    {
        var v = WObjPointProbe.LaserOf(new[] { Ch(1, 323.1), Ch(2, 0, enabled: false), Ch(3, 324.0) });

        Assert.Equal(323.1, v[0]);
        Assert.True(double.IsNaN(v[1]));
        Assert.Equal(324.0, v[2]);
        Assert.All(WObjPointProbe.LaserOf(Array.Empty<LaserChannelReading>()), x => Assert.True(double.IsNaN(x)));
    }

    [Fact]
    public void 면과_평행하게_360mm_갔는데_면이_6mm_가까워지면_기울기_약_1도()
    {
        // 툴 z = BASE +z(천장을 봄, 광축 +Z). 점2 는 BASE x 로 360mm — 광축 성분 0.
        var p1 = new WObjPointProbe(new[] { 0.0, 0, 1000, 0, 0, 0 }, new[] { 323.0, 322.5, 323.5 }, 300);
        var p2 = new WObjPointProbe(new[] { 360.0, 0, 1000, 0, 0, 0 }, new[] { 317.0, 316.5, 317.5 }, 294);

        var line = WObjPointProbe.Compare(p1, p2, ToolAxisDir.PlusZ);

        Assert.StartsWith("[점 거리 진단] 점1→점2", line);
        Assert.Contains("CH1=-6.00 CH2=-6.00 CH3=-6.00 평균=-6.00mm", line);
        Assert.Contains("depth=-6mm", line);
        Assert.Contains("면 방향 360.0mm, 광축 성분(+ = 면 쪽) +0.00mm", line);
        Assert.Contains("레이저 기준 -0.95°(360mm 당 -6.0mm)", line);
    }

    [Fact]
    public void 광축이_MinusZ_면_툴_z_반대쪽이_면_쪽이고_한쪽에서만_유효한_채널은_뺀다()
    {
        // rx = 180° — 툴 z 가 BASE −z. 광축 −Z 이므로 면은 BASE +z 쪽. 점2 가 BASE z 로 +5mm = 면 쪽으로 5mm.
        var p1 = new WObjPointProbe(new[] { 0.0, 0, 1000, 180, 0, 0 }, new[] { 323.0, double.NaN, 323.0 }, null);
        var p2 = new WObjPointProbe(new[] { 0.0, 300, 1005, 180, 0, 0 }, new[] { 318.0, 318.0, double.NaN }, 300);

        var line = WObjPointProbe.Compare(p1, p2, ToolAxisDir.MinusZ);

        Assert.Contains("CH1=-5.00 CH2=- CH3=- 평균=-5.00mm", line);
        Assert.Contains("depth=-mm", line);
        Assert.Contains("면 방향 300.0mm, 광축 성분(+ = 면 쪽) +5.00mm", line);
        Assert.Contains("depth 기준 -", line);
    }

    [Fact]
    public void 깊이_중앙값은_중앙_영역의_유효값만_쓴다()
    {
        const int w = 10, h = 10;
        var px = new ushort[w * h];
        Array.Fill(px, (ushort)999);                       // 가장자리 — 무시
        for (int y = 4; y < 6; y++)
            for (int x = 4; x < 6; x++) px[y * w + x] = 300;
        px[4 * w + 4] = 0;                                 // 무효 화소 — 무시
        var bytes = new byte[px.Length * 2];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);

        Assert.Equal(300, WObjPointProbe.CenterDepthMm(new CameraFrame(bytes, w, h, "Z16", DateTime.UtcNow)));
        Assert.Null(WObjPointProbe.CenterDepthMm(null));
    }
}
