using HD.AMR.App.Service.Vision;

namespace HD.AMR.Tests;

public class WeldLineLabelTests
{
    private static WeldLineLabel Horizontal(int thickness = 9) => new()
    {
        Width = 100, Height = 50, Thickness = thickness,
        Points = new() { new[] { 10.0, 20.0 }, new[] { 90.0, 20.0 } },
    };

    [Fact]
    public void 래스터화_두께가_지정값과_같다()
    {
        var l = Horizontal(9);
        var m = l.Rasterize();
        // x=50 열에서 켜진 픽셀 수 = 두께(중심 ±4.5 → 16..24 의 9px).
        int on = 0;
        for (int y = 0; y < l.Height; y++) if (m[y * l.Width + 50] != 0) on++;
        Assert.Equal(9, on);
        Assert.Equal(255, m[20 * l.Width + 50]);
        Assert.Equal(0, m[10 * l.Width + 50]);
    }

    [Fact]
    public void 선_끝_밖은_칠하지_않는다()
    {
        var m = Horizontal().Rasterize();
        Assert.Equal(0, m[20 * 100 + 0]);
        Assert.Equal(0, m[20 * 100 + 99]);
    }

    [Fact]
    public void JSON_왕복()
    {
        var l = Horizontal(7);
        var back = WeldLineLabel.FromJson(l.ToJson());
        Assert.NotNull(back);
        Assert.Equal(7, back!.Thickness);
        Assert.Equal(2, back.Points.Count);
        Assert.Equal(90.0, back.Points[1][0]);
    }

    [Fact]
    public void 점이_1개면_무효()
    {
        var json = "{\"width\":10,\"height\":10,\"thickness\":5,\"points\":[[1,1]]}";
        Assert.Null(WeldLineLabel.FromJson(json));
        Assert.Null(WeldLineLabel.FromJson("not json"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 회전한_라벨의_래스터는_회전한_마스크와_같다(bool cw)
    {
        var l = new WeldLineLabel
        {
            Width = 40, Height = 30, Thickness = 5,
            Points = new() { new[] { 5.0, 8.0 }, new[] { 33.0, 21.0 } },
        };
        var m = l.Rasterize();
        var r = l.Rotate90(cw);
        Assert.Equal(30, r.Width);
        Assert.Equal(40, r.Height);
        var rm = r.Rasterize();

        // OpenCV Rotate90Clockwise: src(x,y) → dst(h-1-y, x). 반시계: src(x,y) → dst(y, w-1-x).
        int mismatch = 0;
        for (int y = 0; y < l.Height; y++)
            for (int x = 0; x < l.Width; x++)
            {
                int dx = cw ? l.Height - 1 - y : y;
                int dy = cw ? x : l.Width - 1 - x;
                if ((m[y * l.Width + x] != 0) != (rm[dy * r.Width + dx] != 0)) mismatch++;
            }
        Assert.Equal(0, mismatch);
    }
}

public class DatasetSplitterTests
{
    [Fact]
    public void 시간_간격으로_세션을_나누고_최근_세션을_val_로()
    {
        var stems = new[]
        {
            "001_20261101_090000", "002_20261101_090010", "003_20261101_090020", "004_20261101_090030",
            "005_20261102_140000", "006_20261102_140005",
        };
        var r = DatasetSplitter.Split(stems, valFraction: 0.3);
        Assert.Equal(2, r.SessionCount);
        Assert.False(r.SingleSession);
        Assert.Equal(new[] { "005_20261102_140000", "006_20261102_140005" }, r.Val);
        Assert.Equal(4, r.Train.Count);
    }

    [Fact]
    public void 회전본은_원본과_같은_쪽으로()
    {
        var stems = new[]
        {
            "001_20261101_090000", "001_20261101_090000_cw90",
            "002_20261103_090000", "002_20261103_090000_ccw90",
        };
        var r = DatasetSplitter.Split(stems, valFraction: 0.5);
        Assert.Contains("002_20261103_090000", r.Val);
        Assert.Contains("002_20261103_090000_ccw90", r.Val);
        Assert.Contains("001_20261101_090000_cw90", r.Train);
    }

    [Fact]
    public void 세션이_하나면_시간순_뒤쪽을_val_로_하고_경고()
    {
        var stems = Enumerable.Range(0, 10).Select(i => $"{i + 1:D3}_20261101_0900{i:D2}").ToList();
        var r = DatasetSplitter.Split(stems, valFraction: 0.2);
        Assert.True(r.SingleSession);
        Assert.Equal(new[] { "009_20261101_090008", "010_20261101_090009" }, r.Val);
    }

    [Fact]
    public void 첫_세션은_항상_train_에_남는다()
    {
        var stems = new[] { "001_20261101_090000", "002_20261102_090000" };
        var r = DatasetSplitter.Split(stems, valFraction: 0.9);
        Assert.Contains("001_20261101_090000", r.Train);
        Assert.Single(r.Val);
    }

    [Fact]
    public void hard_접두_stem_도_타임스탬프를_읽는다()
    {
        Assert.Equal(new DateTime(2026, 11, 5, 13, 2, 3), DatasetSplitter.ParseTimestamp("012_hard_20261105_130203_2"));
        Assert.Null(DatasetSplitter.ParseTimestamp("legacy"));
    }

}

/// <summary>선 라벨 끊김(Breaks) — 떨어진 비드를 한 라벨에.</summary>
public class WeldLineLabelBreakTests
{
    // 끊김: 점 0-1 한 선, 점 2-3 다른 선(가로 같은 높이, x 40~60 은 빈 구간).
    private static WeldLineLabel Broken() => new()
    {
        Width = 100, Height = 50, Thickness = 5,
        Points = new() { new[] { 10.0, 20.0 }, new[] { 40.0, 20.0 }, new[] { 60.0, 20.0 }, new[] { 90.0, 20.0 } },
        Breaks = new() { 2 },
    };

    [Fact]
    public void 끊긴_구간은_칠하지_않는다()
    {
        var l = Broken();
        var m = l.Rasterize();
        Assert.NotEqual(0, m[20 * 100 + 25]);
        Assert.NotEqual(0, m[20 * 100 + 75]);
        Assert.Equal(0, m[20 * 100 + 50]);   // 40~60 사이(두께 반경 밖)
        Assert.Equal(2, l.Segments().Count);
        Assert.Equal(2, l.Edges().Count());
    }

    [Fact]
    public void 끊김_JSON_왕복_및_예전_파일_호환()
    {
        var back = WeldLineLabel.FromJson(Broken().ToJson())!;
        Assert.Equal(new[] { 2 }, back.Breaks!);

        var plain = new WeldLineLabel { Width = 100, Height = 50, Points = new() { new[] { 10.0, 20.0 }, new[] { 90.0, 20.0 } } }.ToJson();
        Assert.DoesNotContain("breaks", plain);          // 끊김 없으면 저장 안 함
        var old = WeldLineLabel.FromJson("{\"width\":100,\"height\":50,\"thickness\":9,\"points\":[[10,20],[90,20]]}")!;
        Assert.Null(old.Breaks);
        Assert.Single(old.Segments());
    }

    [Fact]
    public void 점_하나짜리_조각만_있으면_무효()
    {
        var l = new WeldLineLabel
        {
            Width = 100, Height = 50,
            Points = new() { new[] { 10.0, 20.0 }, new[] { 60.0, 20.0 } },
            Breaks = new() { 1 },
        };
        Assert.False(l.IsValid);
    }

    [Fact]
    public void 회전해도_끊김_유지()
    {
        var r = Broken().Rotate90(true);
        Assert.Equal(new[] { 2 }, r.Breaks!);
        Assert.Equal(0, r.Rasterize()[50 * r.Width + (r.Width - 1 - 20)]);   // 빈 구간(x=50 → 회전 후 y=50)
    }
}
