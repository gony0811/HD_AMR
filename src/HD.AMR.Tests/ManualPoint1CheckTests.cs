using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 점1 수동 입력 대조 — 값은 2026-11-11~19 실기 로그(점1 수동 + 점2 자동)의 점2 비드 이격·교시 간격 그대로다.
/// 2026-11-19 10:21 F L4-R01C03-1: 점1 에서 비드 위쪽 약 44mm 의 가는 직선을 그어 점2 이격 +53.1mm, 좌표계 2.3° 기울어짐.
/// </summary>
public class ManualPoint1CheckTests
{
    private static string? Check(double d2, double span)
        => ManualPoint1Check.Problem(d2, span, ManualPoint1Check.DefaultMinMm, ManualPoint1Check.DefaultAngleDeg);

    [Theory]
    [InlineData(53.1, 1437)]    // 11-19 F — 이번 사례
    [InlineData(48.2, 363)]     // 11-16 SM
    [InlineData(49.6, 337)]
    [InlineData(54.9, 708)]
    [InlineData(59.2, 1061)]
    [InlineData(-59.5, 407)]    // 11-14 A
    [InlineData(-111.5, 274)]   // 11-12 PU
    public void 다른_선을_그은_실기_사례는_잡는다(double d2, double span)
    {
        var why = Check(d2, span);
        Assert.NotNull(why);
        Assert.Contains("점1 수동 입력 선 의심", why);
    }

    [Theory]
    [InlineData(29.1, 706)]     // 맞게 그은 사례 중 가장 큰 이격(11-14 T)
    [InlineData(23.0, 1068)]
    [InlineData(17.5, 357)]
    [InlineData(-13.1, 353)]    // 11-18 B
    [InlineData(0.4, 360)]
    public void 맞게_그은_실기_사례는_통과한다(double d2, double span)
        => Assert.Null(Check(d2, span));

    [Fact]
    public void 허용은_간격이_길면_각도만큼_늘어난다()
    {
        Assert.Equal(35.0, ManualPoint1Check.AllowedMm(360, 35, 1.5), 6);
        Assert.Equal(1437 * Math.Tan(1.5 * Math.PI / 180), ManualPoint1Check.AllowedMm(1437, 35, 1.5), 6);
        Assert.Null(Check(50, 3000));   // 3 m 간격이면 허용 78.6mm
    }

    [Fact]
    public void 사유에_기울기와_점1_입력_안내를_싣는다()
    {
        var why = ManualPoint1Check.Problem(53.1, 1437, 35, 1.5, " [수동 입력 RGB, 조그 1회 후, 학습 데이터 저장 071_seqP1_20261119_102149_rgb]");
        Assert.NotNull(why);
        Assert.Contains("53.1mm", why);
        Assert.Contains("2.12°", why);
        Assert.Contains("071_seqP1_20261119_102149_rgb", why);
    }

    [Theory]
    [InlineData(double.NaN, 500)]
    [InlineData(80, 0)]
    [InlineData(80, double.NaN)]
    public void 값이_없으면_판단하지_않는다(double d2, double span) => Assert.Null(Check(d2, span));
}
