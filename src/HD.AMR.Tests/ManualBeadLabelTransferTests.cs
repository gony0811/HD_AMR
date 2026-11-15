using System.Runtime.InteropServices;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using HD.AMR.App.Service.Vision;

namespace HD.AMR.Tests;

/// <summary>IR ↔ RGB 라벨 변환, 촬영본 캘리브레이션 파일, 라벨 출처 표시.</summary>
public class ManualBeadLabelTransferTests
{
    private const int W = 640, H = 480, CW = 1280, CH = 960;

    /// <summary>깊이·IR 640×480, 컬러 1280×960(초점거리 2배), Depth→Color 평행이동 tx mm.</summary>
    private static ManualBeadSnapshot Snapshot(double tx, ushort depthMm = 300, Func<int, int, ushort>? depthAt = null)
    {
        var d2c = new CameraD2CParams(
            DepthFx: 400, DepthFy: 400, DepthCx: 320, DepthCy: 240, DepthW: W, DepthH: H,
            ColorFx: 800, ColorFy: 800, ColorCx: 640, ColorCy: 480, ColorW: CW, ColorH: CH,
            Rot: new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
            Trans: new[] { tx, 0, 0 });
        var px = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = depthAt?.Invoke(x, y) ?? depthMm;
        var depth = new CameraFrame(MemoryMarshal.AsBytes(px.AsSpan()).ToArray(), W, H, "depth16", DateTime.Now);
        var ir = new CameraFrame(new byte[W * H], W, H, "ir8", DateTime.Now);
        var color = new CameraFrame(new byte[CW * CH * 3], CW, CH, "rgb24", DateTime.Now);
        return new ManualBeadSnapshot
        {
            Frames = new ManualBeadFrames(ir, depth, color, d2c, "test", null),
            Ir = ir,
            Depth = depth,
            Color = color,
            ProjectionZ = depthMm,
        };
    }

    private static readonly PixelPoint[] IrLine = { new(100, 200), new(500, 260) };

    [Fact]
    public void 평행이동이_없으면_IR_좌표는_컬러에서_초점거리_비율만큼_커진다()
    {
        var t = ManualBeadLabelTransfer.Transfer(Snapshot(0), "ir", IrLine)!.Value;

        Assert.Equal("rgb", t.Modality);
        Assert.Equal((CW, CH), (t.Width, t.Height));
        Assert.Equal(2.0, t.ThicknessScale, 6);
        Assert.Equal(200, t.Points[0].X, 6);   // (100−320)·2+640
        Assert.Equal(400, t.Points[0].Y, 6);   // (200−240)·2+480
        Assert.Equal(1000, t.Points[^1].X, 6);
        Assert.Equal(520, t.Points[^1].Y, 6);
    }

    [Fact]
    public void 카메라_간격은_깊이에_따른_시차로_반영된다()
    {
        // tx 15mm, Z 300mm → 컬러 x 가 800·15/300 = 40px 이동.
        var t = ManualBeadLabelTransfer.Transfer(Snapshot(15), "ir", IrLine)!.Value;

        Assert.Equal(240, t.Points[0].X, 6);
    }

    [Fact]
    public void 깊이가_빈_자리는_선의_유효_깊이로_채운다()
    {
        // 왼쪽 절반은 깊이 0(금속광택 구멍) — 선 위 다른 점의 300mm 로 채워 시차가 같아야 한다.
        var snap = Snapshot(15, depthAt: (x, _) => x < 320 ? (ushort)0 : (ushort)300);
        var t = ManualBeadLabelTransfer.Transfer(snap, "ir", IrLine)!.Value;

        Assert.Equal(240, t.Points[0].X, 6);
    }

    [Fact]
    public void RGB에서_IR로_옮기면_원래_IR_점으로_돌아온다()
    {
        var snap = Snapshot(15);
        var toRgb = ManualBeadLabelTransfer.Transfer(snap, "ir", IrLine)!.Value;
        var back = ManualBeadLabelTransfer.Transfer(snap, "rgb", new[] { toRgb.Points[0], toRgb.Points[^1] })!.Value;

        Assert.Equal("ir", back.Modality);
        Assert.Equal(IrLine[0].X, back.Points[0].X, 3);
        Assert.Equal(IrLine[0].Y, back.Points[0].Y, 3);
        Assert.Equal(IrLine[1].X, back.Points[^1].X, 3);
        Assert.Equal(0.5, back.ThicknessScale, 6);
    }

    [Fact]
    public void 컬러_영상_밖으로_나간_점은_버린다()
    {
        // 큰 평행이동으로 오른쪽 끝이 컬러 밖으로 밀린다.
        var t = ManualBeadLabelTransfer.Transfer(Snapshot(120), "ir", IrLine)!.Value;

        Assert.All(t.Points, p => Assert.InRange(p.X, 0, CW - 1));
        Assert.True(t.Points.Count < ManualBeadLabelTransfer.Densify(IrLine, ManualBeadLabelTransfer.DensifyStepPx).Count);
    }

    [Fact]
    public void 컬러나_캘리브레이션이_없으면_변환하지_않는다()
    {
        var s = Snapshot(0);
        var noColor = new ManualBeadSnapshot { Frames = s.Frames, Ir = s.Ir, Depth = s.Depth, Color = null, ProjectionZ = 300 };

        Assert.Null(ManualBeadLabelTransfer.Transfer(noColor, "ir", IrLine));
    }

    [Fact]
    public void 캘리브레이션_파일은_왕복하고_깨진_파일은_null()
    {
        var p = Snapshot(15).Frames.D2C!;
        var back = CameraD2CFile.FromJson(CameraD2CFile.ToJson(p))!;

        Assert.Equal(p.DepthFx, back.DepthFx);
        Assert.Equal(p.Trans, back.Trans);
        Assert.Equal(p.Rot, back.Rot);
        Assert.Null(CameraD2CFile.FromJson("{ broken"));
    }

    [Fact]
    public void 캘리브레이션_파일도_촬영본_stem으로_인식한다()
    {
        Assert.Equal("012_20261108_101500", BeadManualTestService.StemOf("012_20261108_101500_calib.json"));
    }

    [Fact]
    public void 사람이_그린_라벨은_출처가_없고_옮긴_라벨은_표시된다()
    {
        var human = new WeldLineLabel { Width = 10, Height = 10, Points = { new[] { 0.0, 0 }, new[] { 9.0, 9 } } };
        var moved = new WeldLineLabel { Width = 10, Height = 10, Points = human.Points, Origin = "transfer:ir" };

        Assert.DoesNotContain("origin", human.ToJson());
        Assert.False(WeldLineLabel.FromJson(human.ToJson())!.IsTransferred);
        Assert.True(WeldLineLabel.FromJson(moved.ToJson())!.IsTransferred);
    }

    [Fact]
    public void 캘리브레이션_없는_컬러_안내는_라벨_전용이고_IR로_옮기지_않는다()
    {
        var g = ManualBeadGuide.LabelOnly("rgb", CW, CH, horizontal: true, autoLine: null);
        var s = Snapshot(0);
        var noCalib = new ManualBeadSnapshot
        {
            Frames = s.Frames with { D2C = null },
            Ir = s.Ir, Depth = s.Depth, Color = s.Color, ProjectionZ = 300, ColorGuide = g,
        };

        Assert.False(g.Calibrated);
        Assert.Null(g.PeakRoi);
        Assert.Equal((CW, CH), (g.Width, g.Height));
        Assert.Null(ManualBeadLabelTransfer.Transfer(noCalib, "rgb", new[] { new PixelPoint(10, 10), new PixelPoint(900, 500) }));
    }
}
