using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>레이저 고속 기록 모드 — 간격 해석·기록 조건·CSV 한 줄 형식.</summary>
public class LaserDataLogTests
{
    [Theory]
    [InlineData(null, 100)]
    [InlineData(100, 100)]
    [InlineData(50, 50)]
    [InlineData(20, 20)]
    [InlineData(19, 100)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 100)]
    [InlineData(0, 100)]
    public void 간격은_범위_밖이면_100ms(int? raw, int expected)
        => Assert.Equal(expected, LaserDataLogService.ResolveIntervalMs(raw));

    [Theory]
    [InlineData(false, true, true, true, false)]   // 설정 끔
    [InlineData(true, true, true, false, false)]   // 레이저 미연결
    [InlineData(true, true, false, true, false)]   // 시퀀스 실행 중에만 — 대기 중이면 안 씀
    [InlineData(true, true, true, true, true)]
    [InlineData(true, false, false, true, true)]   // 항상 기록
    public void 기록_조건(bool enabled, bool seqOnly, bool running, bool connected, bool expected)
        => Assert.Equal(expected, LaserDataLogService.ShouldRecord(enabled, seqOnly, running, connected));

    private static LaserChannelReading Ch(int n, double v, bool ok = true)
        => new(n, 0, v, ok, false, false, false, false, false, false);

    [Fact]
    public void CSV_한_줄은_헤더와_칸_수가_같다()
    {
        var plane = new PlanePose(0, 0, 400.5, 1.25, -0.5, 0, new[] { 0.0, 0, 1 }, true, null);
        var line = LaserDataLogService.FormatLine(new DateTime(2026, 11, 24, 15, 20, 46, 82), "laserWd", "order,1",
            new[] { Ch(1, 400.59), Ch(2, 401.56), Ch(3, 450.7, ok: false) }, plane, plane,
            new[] { -1522.9, -383.2, 897.4, 90.0, -0.0, 90.5 }, new[] { 11.5, -72.2, 79.1, -6.9, 101.0, -270.0 });

        Assert.StartsWith("15:20:46.082,laserWd,\"order,1\",400.590,1,401.560,1,450.700,0,400.500,1.2500,-0.5000,1.2500,-0.5000,-1522.90,", line);
        Assert.EndsWith(",-270.000", line);
        // 따옴표 안 쉼표를 빼면 칸 수가 헤더와 같다.
        var cols = line.Replace("\"order,1\"", "o").Split(',').Length;
        Assert.Equal(LaserDataLogService.CsvHeader.Split(',').Length, cols);
    }

    [Fact]
    public void 값이_없으면_빈_칸으로_남긴다()
    {
        var line = LaserDataLogService.FormatLine(new DateTime(2026, 1, 1), null, null,
            Array.Empty<LaserChannelReading>(), null, PlanePose.Invalid("x"), null, null);
        Assert.Equal(LaserDataLogService.CsvHeader.Split(',').Length, line.Split(',').Length);
        Assert.Equal("00:00:00.000" + new string(',', LaserDataLogService.CsvHeader.Split(',').Length - 1), line);
    }
}
