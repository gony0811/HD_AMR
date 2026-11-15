using System.Runtime.InteropServices;
using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Sequence;

/// <summary>
/// 수동 비드 라벨을 <b>IR ↔ RGB 영상 사이로 옮긴다</b>. D435 의 IR(왼쪽 IR) 영상과 Depth 는 같은 센서·같은 좌표라,
/// IR 라벨의 각 점은 그 자리의 깊이로 3D 점이 되고 공장 캘리브레이션(Depth→Color)으로 컬러 영상에 정확히 투영된다.
/// 같은 순간·같은 물리적 용접선이므로 한쪽에 라벨링하면 다른 쪽 학습 데이터도 함께 생긴다.
///
/// 비드는 금속광택으로 깊이가 비는 자리가 많다 — 점마다 주변 7×7 의 유효 깊이 중앙값을 쓰고, 그것도 없으면 선 전체의
/// 유효 깊이 중앙값, 그것도 없으면 스냅샷 투영 깊이(<see cref="ManualBeadSnapshot.ProjectionZ"/>)로 채운다.
/// 선은 일정 간격으로 촘촘히 나눠 옮긴다(면 굴곡에 따른 투영 휨 반영). 대상 영상 밖으로 나간 점은 버린다.
/// </summary>
public static class ManualBeadLabelTransfer
{
    /// <summary>옮기기 전 원본 선을 나누는 간격 [원본 px].</summary>
    public const double DensifyStepPx = 8.0;

    /// <summary>
    /// <paramref name="fromModality"/>("ir"/"rgb") 영상의 선 점들을 반대 영상 좌표로 옮긴다.
    /// 컬러 영상·Depth·캘리브레이션이 없거나 옮긴 점이 2개 미만이면 null.
    /// 반환: (대상 영상 이름, 점들, 대상 영상 크기, 두께 배율 = 대상 px / 원본 px).
    /// </summary>
    public static (string Modality, List<PixelPoint> Points, int Width, int Height, double ThicknessScale)? Transfer(
        ManualBeadSnapshot s, string fromModality, IReadOnlyList<PixelPoint> points)
    {
        if (points.Count < 2 || s.Color is null || s.Depth is null || s.Frames.D2C is not { IsValid: true } d2c) return null;
        var depth = s.Depth;
        int irW = s.Ir.Width, irH = s.Ir.Height;
        int cW = s.Color.Width, cH = s.Color.Height;
        var mapper = new DepthColorMapper(d2c, depth.Width, depth.Height, cW, cH);
        double sx = (double)depth.Width / irW, sy = (double)depth.Height / irH;   // IR → Depth 좌표(보통 1:1)

        // 두께 배율: 같은 거리에서 컬러 px / depth px (초점거리 비, 프레임 해상도 기준).
        double dfx = d2c.DepthFx * depth.Width / Math.Max(1, d2c.DepthW);
        double cfx = d2c.ColorFx * cW / Math.Max(1, d2c.ColorW);
        double irToColor = dfx > 0 ? cfx / (dfx / sx) : 1.0;

        var dense = Densify(points, DensifyStepPx);
        var result = new List<PixelPoint>(dense.Count);

        if (fromModality == "rgb")
        {
            foreach (var p in dense)
            {
                // 깊이를 모르므로 투영 깊이로 한 번 옮긴 자리의 깊이로 다시 옮긴다(2회 반복).
                double z = s.ProjectionZ > 0 ? s.ProjectionZ : 300;
                (double u, double v)? d = null;
                for (int it = 0; it < 2; it++)
                {
                    d = mapper.ColorToDepth(p.X, p.Y, z);
                    if (d is null) break;
                    var zz = SampleDepthMm(depth, d.Value.u, d.Value.v);
                    if (zz <= 0) break;
                    z = zz;
                }
                if (d is not { } q) continue;
                double x = q.u / sx, y = q.v / sy;
                if (x >= 0 && y >= 0 && x <= irW - 1 && y <= irH - 1) result.Add(new PixelPoint(x, y));
            }
            return result.Count >= 2 ? ("ir", result, irW, irH, 1.0 / irToColor) : null;
        }

        var zs = dense.Select(p => SampleDepthMm(depth, p.X * sx, p.Y * sy)).ToList();
        var valid = zs.Where(z => z > 0).OrderBy(z => z).ToList();
        double fallbackZ = valid.Count > 0 ? valid[valid.Count / 2] : s.ProjectionZ;
        if (fallbackZ <= 0) return null;
        for (int i = 0; i < dense.Count; i++)
        {
            double z = zs[i] > 0 ? zs[i] : fallbackZ;
            if (mapper.DepthToColor(dense[i].X * sx, dense[i].Y * sy, z) is not { } c) continue;
            if (c.u >= 0 && c.v >= 0 && c.u <= cW - 1 && c.v <= cH - 1) result.Add(new PixelPoint(c.u, c.v));
        }
        return result.Count >= 2 ? ("rgb", result, cW, cH, irToColor) : null;
    }

    /// <summary>폴리라인을 <paramref name="stepPx"/> 이하 간격으로 나눈 점들(원래 꼭짓점 포함).</summary>
    public static List<PixelPoint> Densify(IReadOnlyList<PixelPoint> pts, double stepPx)
    {
        var r = new List<PixelPoint> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            int n = Math.Max(1, (int)Math.Ceiling(len / stepPx));
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n;
                r.Add(new PixelPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        return r;
    }

    /// <summary>16bit 깊이(mm) 프레임에서 (u,v) 주변 7×7 유효값 중앙값. 없으면 0.</summary>
    public static double SampleDepthMm(CameraFrame depth, double u, double v)
    {
        int x = (int)Math.Round(u), y = (int)Math.Round(v);
        var span = MemoryMarshal.Cast<byte, ushort>(depth.Pixels);
        var vals = new List<int>(49);
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= depth.Width || yy >= depth.Height) continue;
                int idx = yy * depth.Width + xx;
                if ((uint)idx >= (uint)span.Length) continue;
                if (span[idx] > 0) vals.Add(span[idx]);
            }
        if (vals.Count == 0) return 0;
        vals.Sort();
        return vals[vals.Count / 2];
    }
}
