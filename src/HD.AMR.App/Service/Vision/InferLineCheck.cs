using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 추론·검증 화면용 — 모델 마스크에서 <b>용접선</b>을 뽑아(운영 검출과 같은 <see cref="WeldCenterlineScanner"/>)
/// 정답 선 라벨이 있으면 오차(<see cref="WeldLineMetrics.Compare"/>, 비교평가와 같은 기준)를 계산하고,
/// 원본 위에 마스크(옅게)·정답 선(초록)·검출 선(노랑)을 그린 JPEG 을 만든다. OpenCV 없이 동작(macOS 포함).
/// </summary>
public static class InferLineCheck
{
    /// <param name="image">원본 이미지(PNG/JPEG 바이트).</param>
    /// <param name="mask">모델 마스크(w×h, 비드=nonzero).</param>
    /// <param name="baseParams">운영 검출 파라미터(직선 피팅 방식 등). 진행축은 여기서 자동으로 정한다.</param>
    /// <param name="gt">정답 선 라벨(없으면 오차 계산 안 함).</param>
    public static InferLineResult Run(byte[] image, byte[] mask, int w, int h, WeldDetectionParams baseParams, WeldLineLabel? gt)
    {
        if (gt is not null && (!gt.IsValid || gt.Width != w || gt.Height != h)) gt = null;   // 크기 다르면 비교 불가
        // 진행축: 정답 선 방향 → 없으면 마스크 주방향. 용접선이 가로에 가까우면 가로 진행.
        bool horiz = gt is not null ? IsHorizontal(gt) : IsHorizontal(mask, w, h);
        var p = baseParams.Clone();
        p.ProgressAxis = horiz ? WeldProgressAxis.Horizontal : WeldProgressAxis.Vertical;
        // 운영은 ② 에서 용접선을 영상 가로로 맞춘 뒤 검출해 RANSAC 각도 제한(기본 20°)이 작다. 여기 이미지는
        // 정렬 전 촬영본이라 대각선이 많다 — 진행축을 선 방향으로 골랐으므로 진행축 대비 최대 45° 까지 허용.
        p.RansacMaxAngleDeg = Math.Max(p.RansacMaxAngleDeg, 45);
        var roi = RoiRect.Full(w, h);

        var pts = new List<PixelPoint>();
        (PixelPoint A, PixelPoint B)? ext = null;   // 맞춘 직선을 마스크 전체 길이까지 연장(표시용 점선)
        string? msg = null;
        bool any = mask.Any(b => b != 0);
        if (!any) msg = "비드 검출 없음";
        else
        {
            var sc = WeldCenterlineScanner.Scan(mask, roi, p);
            if (!sc.Ok) msg = $"중심선 추출 실패 — 비드 후보 부족(커버리지 {sc.Coverage:P0})";
            else
            {
                pts = WeldCenterlineScanner.Points(sc, roi);
                if (sc.FitOk) ext = Extend(mask, w, h, sc);
            }
        }

        LineErrors? err = gt is null ? null : pts.Count >= 2 ? WeldLineMetrics.Compare(gt, pts, horiz) : LineErrors.Missed;
        double tilt = pts.Count >= 2 ? TiltDeg(pts[0], pts[^1]) : double.NaN;
        var overlay = Render(image, mask, w, h, pts, ext, gt);
        return new InferLineResult(pts.Count >= 2, horiz, pts, tilt, err, msg, overlay);
    }

    /// <summary>
    /// 연장 범위 — 진행축 슬라이스 중 맞춘 직선(cross = A·s + B) ±<see cref="ExtendBandPx"/> 안에 마스크가 있는 첫·끝 슬라이스.
    /// 직선에서 먼 잡음 마스크(U-Net 의 다른 모서리 등)는 길이에 넣지 않는다.
    /// </summary>
    private static (PixelPoint, PixelPoint)? Extend(byte[] mask, int w, int h, CenterlineScan sc)
    {
        int first = -1, last = -1;
        for (int s = 0; s < sc.SCount; s++)
        {
            double c = sc.A * s + sc.B;
            int c0 = Math.Max(0, (int)Math.Floor(c - ExtendBandPx)), c1 = Math.Min((sc.Horizontal ? h : w) - 1, (int)Math.Ceiling(c + ExtendBandPx));
            for (int k = c0; k <= c1; k++)
            {
                int x = sc.Horizontal ? s : k, y = sc.Horizontal ? k : s;
                if (mask[y * w + x] == 0) continue;
                if (first < 0) first = s;
                last = s;
                break;
            }
        }
        if (first < 0 || last <= first) return null;
        PixelPoint At(int s) => sc.Horizontal ? new PixelPoint(s, sc.A * s + sc.B) : new PixelPoint(sc.A * s + sc.B, s);
        return (At(first), At(last));
    }

    private const double ExtendBandPx = 20;

    private static bool IsHorizontal(WeldLineLabel gt)
    {
        var a = gt.Points[0]; var b = gt.Points[^1];
        return Math.Abs(b[0] - a[0]) >= Math.Abs(b[1] - a[1]);
    }

    private static bool IsHorizontal(byte[] mask, int w, int h)
    {
        int step = Math.Max(1, (int)Math.Sqrt(w * h / 20000.0));
        var pts = new List<(double X, double Y)>();
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
                if (mask[y * w + x] != 0) pts.Add((x, y));
        var f = WeldLineMetrics.FitLine(pts);
        return f is null || Math.Abs(f.Value.Dx) >= Math.Abs(f.Value.Dy);
    }

    /// <summary>가로 기준 기울기(°, -90~90, 화면 아래쪽이 +).</summary>
    private static double TiltDeg(PixelPoint a, PixelPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (dx < 0) { dx = -dx; dy = -dy; }
        return Math.Atan2(dy, dx) * 180 / Math.PI;
    }

    // 원본 + 마스크(옅은 빨강) + 정답 선(초록, 끊긴 구간 제외) + 연장선(노랑 점선) + 검출 선(노랑 실선) → JPEG.
    private static byte[] Render(byte[] image, byte[] mask, int w, int h, List<PixelPoint> det,
        (PixelPoint A, PixelPoint B)? ext, WeldLineLabel? gt)
    {
        using var img = Image.Load<Rgb24>(image);
        bool sameSize = img.Width == w && img.Height == h;
        img.ProcessPixelRows(acc =>
        {
            if (sameSize)
                for (int y = 0; y < h; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        if (mask[y * w + x] == 0) continue;
                        var q = row[x];
                        row[x] = new Rgb24((byte)(q.R * 0.7 + 255 * 0.3), (byte)(q.G * 0.7), (byte)(q.B * 0.7));
                    }
                }
            if (gt is not null)
                foreach (var (a, b) in gt.Edges())
                    Line(acc, a[0], a[1], b[0], b[1], 1.2, new Rgb24(0, 230, 0));
            var yellow = new Rgb24(255, 230, 0);
            if (ext is { } e)   // 연장선(점선) 먼저 — 실선이 위에 겹쳐 인정 구간은 실선으로 보인다.
                Line(acc, e.A.X, e.A.Y, e.B.X, e.B.Y, 1.2, yellow, dashOn: 8, dashOff: 6);
            for (int i = 0; i + 1 < det.Count; i++)
                Line(acc, det[i].X, det[i].Y, det[i + 1].X, det[i + 1].Y, 1.6, yellow);
        });
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
        return ms.ToArray();
    }

    // 반지름 r 원을 선분 따라 찍는 굵은 선(픽셀 접근 — ImageSharp.Drawing 없이).
    private static void Line(SixLabors.ImageSharp.PixelAccessor<Rgb24> acc, double x0, double y0, double x1, double y1, double r, Rgb24 c,
        int dashOn = 0, int dashOff = 0)
    {
        int W = acc.Width, H = acc.Height;
        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int n = Math.Max(1, (int)Math.Ceiling(len));
        int ri = (int)Math.Ceiling(r);
        for (int k = 0; k <= n; k++)
        {
            if (dashOn > 0 && k % (dashOn + dashOff) >= dashOn) continue;   // 점선: on px 그리고 off px 건너뜀
            double cx = x0 + (x1 - x0) * k / n, cy = y0 + (y1 - y0) * k / n;
            for (int dy = -ri; dy <= ri; dy++)
            {
                int y = (int)Math.Round(cy) + dy;
                if (y < 0 || y >= H) continue;
                var row = acc.GetRowSpan(y);
                for (int dx = -ri; dx <= ri; dx++)
                {
                    int x = (int)Math.Round(cx) + dx;
                    if (x < 0 || x >= W || dx * dx + dy * dy > r * r + 0.5) continue;
                    row[x] = c;
                }
            }
        }
    }
}

/// <param name="Found">검출 용접선(2점 이상)을 얻었는지.</param>
/// <param name="Horizontal">사용한 진행축(가로면 true).</param>
/// <param name="TiltDeg">검출 선 기울기(가로 기준 °). 없으면 NaN.</param>
/// <param name="Errors">정답 선 대비 오차. 정답이 없으면 null.</param>
/// <param name="Message">실패 사유(성공이면 null).</param>
public sealed record InferLineResult(bool Found, bool Horizontal, IReadOnlyList<PixelPoint> Centerline, double TiltDeg,
    LineErrors? Errors, string? Message, byte[] OverlayJpeg);
