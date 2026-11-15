using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;

namespace HD.AMR.Tests;

/// <summary>
/// 시퀀스 RGB 입력의 "검출은 컬러, 기준은 IR" 환산 검증. 컬러 영상에서 'IR 화면 중심선의 투영'을 기준으로 잰 d 를
/// 초점거리 비율로 IR 픽셀로 바꾸면, 같은 비드를 IR 모드에서 잰 d 와 같아야 한다(센서 간격·화각이 달라도).
/// </summary>
public class RgbIrGeometryTests
{
    // D435 유사: Depth/IR 848×480 (HFOV≈87°), Color 848×480 (HFOV≈69°), 컬러 센서가 IR 원점에서 +15mm(x).
    private static CameraD2CParams Calib(double tx = 15.0) => new(
        DepthFx: 425, DepthFy: 425, DepthCx: 424, DepthCy: 240, DepthW: 848, DepthH: 480,
        ColorFx: 610, ColorFy: 610, ColorCx: 430, ColorCy: 245, ColorW: 848, ColorH: 480,
        Rot: [1, 0, 0, 0, 1, 0, 0, 0, 1],
        Trans: [tx, 0, 0]);

    /// <summary>IR 카메라 좌표계의 3D 점을 IR(=Depth) 픽셀로 투영.</summary>
    private static (double u, double v) ProjectIr(CameraD2CParams p, double x, double y, double z)
        => (p.DepthFx * x / z + p.DepthCx, p.DepthFy * y / z + p.DepthCy);

    [Theory]
    [InlineData(true, 0.0)]     // 진행축 가로 → d 는 세로(v) 방향
    [InlineData(true, 6.5)]
    [InlineData(true, -12.0)]
    [InlineData(false, 0.0)]    // 진행축 세로 → d 는 가로(u) 방향 — 센서 간격(15mm)이 걸리는 축
    [InlineData(false, 6.5)]
    [InlineData(false, -12.0)]
    public void ColorMeasuredD_ConvertedToIr_EqualsIrMeasuredD(bool horiz, double offsetMm)
    {
        var p = Calib();
        var mapper = new DepthColorMapper(p, 848, 480, 848, 480);
        const double z = 320.0;

        // 측정 진행좌표(IR 픽셀) — Peak 선 위치.
        double progIr = horiz ? 500.0 : 300.0;
        double centerCrossIr = horiz ? 480 / 2.0 : 848 / 2.0;

        // IR 화면 중심선 위 측정점을 3D 로 역투영한 뒤, cross 방향으로 offsetMm 떨어진 곳에 비드 중심이 있다고 둔다.
        double uC = horiz ? progIr : centerCrossIr, vC = horiz ? centerCrossIr : progIr;
        double x0 = (uC - p.DepthCx) / p.DepthFx * z, y0 = (vC - p.DepthCy) / p.DepthFy * z;
        double bx = horiz ? x0 : x0 + offsetMm, by = horiz ? y0 + offsetMm : y0;

        // IR 모드에서 잰 d(px) = 비드 cross − IR 중심선.
        var beadIr = ProjectIr(p, bx, by, z);
        double dIrTruth = (horiz ? beadIr.v : beadIr.u) - centerCrossIr;

        // RGB 경로: 기준점(IR 중심선 투영)과 비드를 컬러로 투영해 컬러 d 를 잰 뒤 IR 픽셀로 환산.
        var refC = mapper.DepthToColor(uC, vC, z)!.Value;
        var beadC = mapper.DepthToColor(beadIr.u, beadIr.v, z)!.Value;
        double dColor = (horiz ? beadC.v : beadC.u) - (horiz ? refC.v : refC.u);
        double dIr = dColor * mapper.CrossScaleColorToDepth(horiz);

        Assert.Equal(dIrTruth, dIr, 6);
    }

    [Fact]
    public void ColorFrameCenter_IsNotIrCenter_WhenSensorsAreOffset()
    {
        // 기준을 컬러 화면 중심으로 두면 안 되는 이유: 같은 거리에서 IR 중심선이 컬러 영상에서 수십 px 어긋난다.
        var p = Calib();
        var mapper = new DepthColorMapper(p, 848, 480, 848, 480);
        var irCenterInColor = mapper.DepthToColor(848 / 2.0, 480 / 2.0, 320.0)!.Value;
        Assert.True(Math.Abs(irCenterInColor.u - 848 / 2.0) > 20,
            $"IR 중심이 컬러에서 u={irCenterInColor.u:0.0} — 컬러 중심과 거의 같으면 테스트 캘리브레이션을 확인");
    }

    [Fact]
    public void MapDepthRoiToColor_ProjectsCornersAndClamps()
    {
        var mapper = new DepthColorMapper(Calib(), 848, 480, 848, 480);

        var roi = mapper.MapDepthRoiToColor(new RoiRect(324, 190, 200, 100), 320.0, 848, 480);
        Assert.NotNull(roi);
        // 컬러 초점거리가 더 길어(화각 좁음) 같은 영역이 더 크게 보인다: 200px × 610/425 ≈ 287px.
        Assert.InRange(roi!.Width, 284, 291);
        Assert.InRange(roi.Height, 141, 147);

        // 일부만 컬러 화면 밖으로 나가는 ROI 는 컬러 프레임 안으로 잘린다.
        var edge = mapper.MapDepthRoiToColor(new RoiRect(600, 380, 200, 90), 320.0, 848, 480);
        Assert.NotNull(edge);
        Assert.True(edge!.X + edge.Width <= 848 && edge.Y + edge.Height <= 480);
        Assert.True(edge.Width >= 2 && edge.Height >= 2);
    }

    [Fact]
    public void MapDepthRoiToColor_ReturnsNull_WhenRoiFallsOutsideNarrowerColorFov()
    {
        // IR 화면 가장자리(컬러 화각 밖) ROI 는 컬러에 투영할 수 없다 → null(시퀀스는 'Bead ROI 투영 실패'로 중단).
        var mapper = new DepthColorMapper(Calib(), 848, 480, 848, 480);
        Assert.Null(mapper.MapDepthRoiToColor(new RoiRect(700, 400, 148, 80), 320.0, 848, 480));
    }

    [Theory]
    [InlineData(100.0, 80.0, 320.0)]
    [InlineData(424.0, 240.0, 250.0)]
    [InlineData(700.0, 400.0, 500.0)]
    public void ColorToDepth_IsExactInverseOfDepthToColor(double u, double v, double z)
    {
        // 약간의 회전(0.3°)까지 포함한 캘리브레이션에서도 정확히 되돌아와야 한다.
        double a = 0.3 * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
        var p = Calib() with { Rot = [ca, 0, sa, 0, 1, 0, -sa, 0, ca], Trans = [15.0, 0.2, 0.0] };
        var mapper = new DepthColorMapper(p, 848, 480, 848, 480);

        var c = mapper.DepthToColor(u, v, z)!.Value;
        // 컬러 기준 깊이는 회전·t_z 때문에 Depth 깊이와 아주 조금 다르다 — 실제 사용(같은 Z 로 왕복)과 같은 조건에서 0.5px 이내.
        var d = mapper.ColorToDepth(c.u, c.v, z)!.Value;
        Assert.InRange(d.u, u - 0.5, u + 0.5);
        Assert.InRange(d.v, v - 0.5, v + 0.5);
    }

    [Fact]
    public void RoiRoundTrip_IrToColorToIr_ReturnsSameRoi()
    {
        // RGB 화면에 표시된 ROI 를 그대로 다시 저장해도 IR 기준 ROI 가 바뀌지 않아야 한다.
        var mapper = new DepthColorMapper(Calib(), 848, 480, 848, 480);
        var ir = new RoiRect(300, 180, 240, 120);
        const double z = 320.0;

        var color = mapper.MapDepthRoiToColor(ir, z, 848, 480)!;
        var back = mapper.MapColorRoiToDepthAt(color, z, 848, 480)!;

        Assert.InRange(back.X, ir.X - 1, ir.X + 1);
        Assert.InRange(back.Y, ir.Y - 1, ir.Y + 1);
        Assert.InRange(back.Width, ir.Width - 1, ir.Width + 1);
        Assert.InRange(back.Height, ir.Height - 1, ir.Height + 1);
    }

    [Fact]
    public void RoiRoundTrip_Repeated_DoesNotCreep()
    {
        // 화면 표시(IR→컬러) ↔ 저장(컬러→IR)을 반복해도 ROI 가 점점 커지거나 밀리지 않아야 한다.
        var mapper = new DepthColorMapper(Calib(), 848, 480, 848, 480);
        var ir = new RoiRect(310, 170, 220, 110);
        var cur = ir;
        for (int i = 0; i < 20; i++)
            cur = mapper.MapColorRoiToDepthAt(mapper.MapDepthRoiToColor(cur, 320.0, 848, 480)!, 320.0, 848, 480)!;

        Assert.InRange(cur.X, ir.X - 1, ir.X + 1);
        Assert.InRange(cur.Y, ir.Y - 1, ir.Y + 1);
        Assert.InRange(cur.Width, ir.Width - 2, ir.Width + 2);
        Assert.InRange(cur.Height, ir.Height - 2, ir.Height + 2);
    }

    [Fact]
    public void ColorDrawnRoi_MapsToDifferentIrPixels_ThanNaiveRatio()
    {
        // 예전 방식(컬러 픽셀을 같은 비율로 IR 에 저장)과 정확 역투영은 수십 px 차이 — 이번 수정이 필요한 이유.
        var mapper = new DepthColorMapper(Calib(), 848, 480, 848, 480);
        var colorRoi = new RoiRect(350, 200, 150, 80);
        var exact = mapper.MapColorRoiToDepthAt(colorRoi, 320.0, 848, 480)!;
        Assert.True(Math.Abs(exact.X - colorRoi.X) > 20 || Math.Abs(exact.Width - colorRoi.Width) > 20);
    }

    [Fact]
    public void Measurement_DefaultsToIrGeometry()
    {
        // 기존 측정 생성 코드(IR 모드)는 IrGeometry 를 지정하지 않아도 IR 기준이어야 한다.
        Assert.True(new PeakMeasurement().IrGeometry);
    }
}
