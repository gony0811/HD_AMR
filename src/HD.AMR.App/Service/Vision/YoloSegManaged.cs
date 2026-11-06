using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// OpenCvSharp 네이티브(win-x64 전용)를 쓸 수 없는 환경(macOS 등)용 YOLOv8-seg 추론. 출력 해석·NMS·proto 합성은
/// <see cref="YoloSeg"/> 공용 코드를 그대로 쓰고, 픽셀 연산만 관리 코드로 한다. 크기 조정은 OpenCV 규칙과 같게
/// 구현했다 — 양선형(INTER_LINEAR, 픽셀 중심 정렬)·최근접(INTER_NEAREST, floor(x·scale)).
/// </summary>
internal static class YoloSegManaged
{
    /// <summary>원본 해상도 이진 마스크(W×H, 0/255)·검출 수·최고 점수.</summary>
    public static (byte[] Mask, int Count, float MaxScore) Decode(
        InferenceSession sess, Image<Rgb24> img, float conf, float iou, float maskThr)
    {
        var (inName, Sh, Sw) = YoloSeg.InputSize(sess);
        int W = img.Width, H = img.Height;
        double r = Math.Min((double)Sw / W, (double)Sh / H);
        int newW = (int)Math.Round(W * r), newH = (int)Math.Round(H * r);
        int dx = (Sw - newW) / 2, dy = (Sh - newH) / 2;

        // letterbox: 양선형 축소/확대 후 회색(114) 여백. 텐서 [1,3,Sh,Sw] — R,G,B 평면, /255.
        var rgb = new byte[W * H * 3];
        img.CopyPixelDataTo(rgb);
        var resized = ResizeLinearRgb(rgb, W, H, newW, newH);
        var data = new float[3 * Sh * Sw];
        int plane = Sh * Sw;
        const float pad = 114f / 255f;
        Array.Fill(data, pad);
        for (int y = 0; y < newH; y++)
            for (int x = 0; x < newW; x++)
            {
                int s = (y * newW + x) * 3, o = (y + dy) * Sw + (x + dx);
                data[o] = resized[s] / 255f;
                data[plane + o] = resized[s + 1] / 255f;
                data[2 * plane + o] = resized[s + 2] / 255f;
            }
        var input = new DenseTensor<float>(data, new[] { 1, 3, Sh, Sw });

        using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });
        var mask = new byte[W * H];
        if (YoloSeg.Parse(results, conf, iou) is not { } parsed) return (mask, 0, 0f);
        var (kept, pr, pc, ph, pw) = parsed;

        // 검출별 proto 확률을 입력 해상도로 양선형 확대 → 임계 초과 & 박스 안만 누적.
        // (박스 밖은 버리므로 확대도 박스 영역만 계산 — 결과는 전체 확대와 같다.)
        var comb = new byte[Sh * Sw];
        double sx = (double)pw / Sw, sy = (double)ph / Sh;
        foreach (var c in kept)
        {
            var mm = YoloSeg.ProtoProb(c, pr, pc, ph * pw);
            int bx1 = Math.Clamp((int)Math.Floor(c.X1), 0, Sw - 1);
            int by1 = Math.Clamp((int)Math.Floor(c.Y1), 0, Sh - 1);
            int bx2 = Math.Clamp((int)Math.Ceiling(c.X2), 1, Sw);
            int by2 = Math.Clamp((int)Math.Ceiling(c.Y2), 1, Sh);
            if (bx2 <= bx1 || by2 <= by1) continue;
            for (int y = by1; y < by2; y++)
            {
                var (y0, y1, fy) = LinearTap(y, sy, ph);
                for (int x = bx1; x < bx2; x++)
                {
                    var (x0, x1, fx) = LinearTap(x, sx, pw);
                    float top = mm[y0 * pw + x0] * (1 - fx) + mm[y0 * pw + x1] * fx;
                    float bot = mm[y1 * pw + x0] * (1 - fx) + mm[y1 * pw + x1] * fx;
                    if (top * (1 - fy) + bot * fy > maskThr) comb[y * Sw + x] = 255;
                }
            }
        }

        // letterbox 제거 → 원본 해상도(최근접).
        double nsx = (double)newW / W, nsy = (double)newH / H;
        for (int y = 0; y < H; y++)
        {
            int syi = Math.Min((int)Math.Floor(y * nsy), newH - 1) + dy;
            for (int x = 0; x < W; x++)
            {
                int sxi = Math.Min((int)Math.Floor(x * nsx), newW - 1) + dx;
                mask[y * W + x] = comb[syi * Sw + sxi];
            }
        }
        return (mask, kept.Count, kept[0].Score);
    }

    /// <summary>원본 위 비드 마스크를 반투명 빨강으로 채우고 노란 외곽선(2px)을 그린 JPEG(품질 85).</summary>
    public static byte[] BuildOverlay(Image<Rgb24> img, byte[] mask)
    {
        int W = img.Width, H = img.Height;
        using var ov = img.Clone();
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && mask[y * W + x] != 0;
        ov.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < H; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < W; x++)
                {
                    if (mask[y * W + x] == 0) continue;
                    var p = row[x];
                    row[x] = new Rgb24(
                        (byte)Math.Round(p.R * 0.55 + 255 * 0.45),
                        (byte)Math.Round(p.G * 0.55),
                        (byte)Math.Round(p.B * 0.55));
                }
            }
            // 외곽선: 마스크 경계 픽셀과 그 바깥 한 칸(≈두께 2).
            var yellow = new Rgb24(255, 255, 0);
            for (int y = 0; y < H; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < W; x++)
                {
                    bool m = Inside(x, y);
                    bool edge = m
                        ? !Inside(x - 1, y) || !Inside(x + 1, y) || !Inside(x, y - 1) || !Inside(x, y + 1)
                        : Inside(x - 1, y) || Inside(x + 1, y) || Inside(x, y - 1) || Inside(x, y + 1);
                    if (edge) row[x] = yellow;
                }
            }
        });
        using var ms = new MemoryStream();
        ov.Save(ms, new JpegEncoder { Quality = 85 });
        return ms.ToArray();
    }

    /// <summary>OpenCV INTER_LINEAR 의 표본 위치: src = (dst+0.5)·scale − 0.5, 경계 복제.</summary>
    private static (int I0, int I1, float F) LinearTap(int dst, double scale, int srcLen)
    {
        double s = (dst + 0.5) * scale - 0.5;
        int i0 = (int)Math.Floor(s);
        float f = (float)(s - i0);
        if (i0 < 0) { i0 = 0; f = 0; }
        if (i0 >= srcLen - 1) { i0 = srcLen - 1; f = 0; }
        return (i0, Math.Min(i0 + 1, srcLen - 1), f);
    }

    /// <summary>RGB24 양선형 크기 조정(OpenCV INTER_LINEAR 규칙, 반올림).</summary>
    private static byte[] ResizeLinearRgb(byte[] src, int w, int h, int nw, int nh)
    {
        var dst = new byte[nw * nh * 3];
        double sx = (double)w / nw, sy = (double)h / nh;
        var xt = new (int, int, float)[nw];
        for (int x = 0; x < nw; x++) xt[x] = LinearTap(x, sx, w);
        for (int y = 0; y < nh; y++)
        {
            var (y0, y1, fy) = LinearTap(y, sy, h);
            for (int x = 0; x < nw; x++)
            {
                var (x0, x1, fx) = xt[x];
                for (int c = 0; c < 3; c++)
                {
                    float top = src[(y0 * w + x0) * 3 + c] * (1 - fx) + src[(y0 * w + x1) * 3 + c] * fx;
                    float bot = src[(y1 * w + x0) * 3 + c] * (1 - fx) + src[(y1 * w + x1) * 3 + c] * fx;
                    dst[(y * nw + x) * 3 + c] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bot * fy), 0, 255);
                }
            }
        }
        return dst;
    }
}
