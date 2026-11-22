using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>작업물 좌표계 점2 면 거리 보정 — 점1·점2 기록 자리의 레이저 채널별 차이로 점2 를 광축 방향으로 옮긴다.</summary>
public class WObjPoint2DistanceCorrectionTests
{
    private static readonly double[] Tcp = { 0, 0, 0, 0, 0, 0 };

    private static WObjPointProbe Probe(double ch1, double ch2, double ch3, double? depth)
        => new(Tcp, new[] { ch1, ch2, ch3 }, depth);

    [Fact]
    public void 실기_수직_묶음_값은_점2를_면에서_멀어지는_쪽으로_보정한다()
    {
        // 2026-11-22 21:44~21:45 M2 수직 묶음 — 점2 가 레이저 −22.35/−32.69/−24.92mm 더 가깝다.
        var p1 = Probe(380.11, 380.81, 381.52, 337);
        var p2 = Probe(357.76, 348.12, 356.60, 316);

        var (mm, note) = WObjPointProbe.Point2DistanceCorrection(p1, p2);

        // 주름에 걸린 CH2(−32.69)는 빼고 서로 맞는 CH1·CH3 평균.
        Assert.NotNull(mm);
        Assert.InRange(mm!.Value, -23.64, -23.63);
        Assert.Contains("2채널", note);
    }

    [Fact]
    public void depth_값은_쓰지_않는다()
    {
        // depth 가 없거나 레이저와 크게 달라도 레이저만으로 보정한다.
        var p1 = Probe(400, 400, 400, 330);
        Assert.Equal(-20.0, WObjPointProbe.Point2DistanceCorrection(p1, Probe(380, 380, 380, 329)).Mm!.Value, 6);
        Assert.Equal(-20.0, WObjPointProbe.Point2DistanceCorrection(p1, Probe(380, 380, 380, null)).Mm!.Value, 6);
        Assert.Equal(-20.0, WObjPointProbe.Point2DistanceCorrection(Probe(400, 400, 400, null), Probe(380, 380, 380, 300)).Mm!.Value, 6);
    }

    [Fact]
    public void 세_채널이_모두_맞으면_세_채널_평균()
    {
        var (mm, note) = WObjPointProbe.Point2DistanceCorrection(Probe(400, 400, 400, null), Probe(410, 412, 411, null));
        Assert.Equal(11.0, mm!.Value, 6);
        Assert.Contains("3채널", note);
    }

    [Fact]
    public void 채널끼리_맞지_않으면_보정하지_않는다()
    {
        // 차이 −10 / −20 / −30 — 어느 두 채널도 5mm 안으로 맞지 않는다.
        var (mm, note) = WObjPointProbe.Point2DistanceCorrection(Probe(400, 400, 400, null), Probe(390, 380, 370, null));
        Assert.Null(mm);
        Assert.Contains("다름", note);
    }

    [Fact]
    public void 유효_채널이_부족하면_보정하지_않는다()
    {
        var nan = double.NaN;
        Assert.Null(WObjPointProbe.Point2DistanceCorrection(Probe(400, nan, nan, 330), Probe(390, 390, 390, 320)).Mm);   // 1채널
        Assert.Null(WObjPointProbe.Point2DistanceCorrection(Probe(nan, nan, nan, 330), Probe(390, 390, 390, 320)).Mm);   // 0채널

        // 2채널이면 두 차이가 맞을 때 그 평균을 쓴다.
        var (mm, _) = WObjPointProbe.Point2DistanceCorrection(Probe(400, 400, nan, 330), Probe(390, 392, 391, 320));
        Assert.Equal((-10.0 + -8.0) / 2, mm!.Value, 6);
    }

    [Fact]
    public void 너무_크거나_작은_차이는_보정하지_않는다()
    {
        Assert.Null(WObjPointProbe.Point2DistanceCorrection(Probe(400, 400, 400, 330), Probe(470, 470, 470, 400)).Mm);       // 70mm
        Assert.Null(WObjPointProbe.Point2DistanceCorrection(Probe(400, 400, 400, 330), Probe(400.4, 400.6, 400.5, 330)).Mm); // 0.5mm
    }

    [Theory]
    [InlineData(null, true, true)]     // 비움 — 가스 켬
    [InlineData(null, false, false)]   // 비움 — 비전 끔
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void 사용_여부는_비우면_센서_모드를_따른다(bool? value, bool usesGas, bool expected)
        => Assert.Equal(expected, WObjPointStep.ResolvePoint2DistanceCorrection(value, usesGas));

    [Fact]
    public void 점2가_더_가까우면_좌표계_X축이_면에서_멀어지는_쪽으로_기운다()
    {
        // 면 법선 = BASE +X(툴 −Z 가 면 쪽), 수직 용접선 = BASE +Z. 두 점의 TCP 는 BASE X 가 같다(좌표계는 기울기 0 으로 본다).
        var toolToWall = new[] { 0.0, 90.0, 0.0 };   // 툴 +Z → BASE +X, 광축 MinusZ 가 면(BASE −X) 쪽
        var p1 = new[] { -1400.0, 0, 0, toolToWall[0], toolToWall[1], toolToWall[2] };
        var p2 = new[] { -1400.0, 0, 720, toolToWall[0], toolToWall[1], toolToWall[2] };

        // 점2 가 24mm 더 가깝다 → 광축(면 쪽)의 반대로 24mm 옮긴다.
        var corrected = WObjPointStep.ApplyCorrection(p2, WObjPointStep.DistanceCorrectionOffset(ToolAxisDir.MinusZ, -24.0));

        Assert.Equal(-1376.0, corrected[0], 6);
        Assert.Equal(720.0, corrected[2], 6);
        // 보정 전에는 기울기 0, 보정 뒤에는 atan(24/720) 만큼 면에서 멀어지는 쪽.
        Assert.Equal(0.0, Math.Abs(InternalWorkpieceFrame.SlopeAlongSeamDeg(p1, p2)), 6);
        Assert.Equal(Math.Atan2(24, 720) * 180 / Math.PI, Math.Abs(InternalWorkpieceFrame.SlopeAlongSeamDeg(p1, corrected)), 3);
    }
}
