using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>
/// 2026-10-08 현장 헤드 캘리브레이션(5° 프로브) 실측 거리로 고정한 회귀 테스트 — '보정 적용'이
/// 수렴하지 않은 원인은 appsettings 헤드 오프셋(H1/H2 위치 뒤바뀜)이었다.
/// 측정 오프셋은 순수 툴 Rx 회전을 rx 단일 축으로, 옛 설정은 rx/ry 혼합으로 읽는다.
/// </summary>
public class LaserHeadOffsetRegressionTests
{
    private static readonly double[] D0 = { 412.671, 412.807, 412.640 };
    private static readonly double[] RxPlus = { -10.654, -13.539, -13.362 };   // 툴 Rx +5° Δd
    private static readonly double[] RxMinus = { 11.573, 14.130, 14.323 };     // 툴 Rx −5° Δd

    private static readonly double[] MeasX = { 15.3, 32.7, -1.0 }, MeasY = { -127.0, -158.1, -158.2 };
    private static readonly double[] OldX = { 22.62, 4.99, -14.91 }, OldY = { -152.79, -119.05, -151.02 };

    private static (double DRx, double DRy) RxProbeResponse(double[] hx, double[] hy)
    {
        double[] Add(double[] a) => D0.Zip(a, (d, x) => d + x).ToArray();
        var p = PlanePoseCalculator.ComputePose(hx, hy, Add(RxPlus), 0, true);
        var m = PlanePoseCalculator.ComputePose(hx, hy, Add(RxMinus), 0, true);
        Assert.True(p.Valid && m.Valid);
        return ((p.Rx - m.Rx) / 2, (p.Ry - m.Ry) / 2);
    }

    [Fact]
    public void MeasuredOffsets_ReadPureRxTiltOnRxAxisOnly()
    {
        var (dRx, dRy) = RxProbeResponse(MeasX, MeasY);
        Assert.InRange(Math.Abs(dRx), 4.5, 5.5);
        Assert.InRange(Math.Abs(dRy), 0, 0.5);
    }

    [Fact]
    public void OldConfigOffsets_MixAxes()
    {
        var (_, dRy) = RxProbeResponse(OldX, OldY);
        Assert.True(Math.Abs(dRy) > 1.5, $"옛 설정은 Rx 회전을 ry 로도 읽어야 한다 (dRy={dRy:0.##})");
    }
}
