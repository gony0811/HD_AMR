using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class LaserOutlierChannelTests
{
    // 현장 실측 (10-27·10-28 로그) — 한 채널만 튄 값은 그 채널을 지목, 정상·면 자체 요철은 −1.
    [Theory]
    [InlineData(346.81, 283.71, 344.54, 1)]   // 10-28 10:25 CH2 −61mm
    [InlineData(359.09, 318.38, 355.73, 1)]   // 10-28 09:28 CH2 −39mm (나머지 둘 3.4mm 차)
    [InlineData(346.12, 345.05, 347.49, -1)]  // 같은 위치 정상
    [InlineData(343.19, 342.78, 343.04, -1)]  // 09:32 성공
    [InlineData(342.63, 325.20, 351.83, -1)]  // 10-27 15:10 — 나머지 둘도 9mm 갈라짐: 단일 튐 아님(면 요철)
    [InlineData(321.70, 316.29, 318.16, -1)]  // 09:56 국부 요철 수준
    [InlineData(300.0, 320.0, 320.5, 0)]
    public void FindsSingleChannelJumpOnly(double a, double b, double c, int expected)
        => Assert.Equal(expected, LaserTiltCorrector.FindOutlierChannel(new[] { a, b, c }));
}
