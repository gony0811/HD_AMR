using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 중심선 히트맵 모델(<c>weld_heatmap_{ir|rgb}.onnx</c>, <c>weld_heatmap.py</c> 로 학습) 추론.
/// 입력 [1,3,H,W] RGB 0~1(ImageNet 정규화는 모델 안), 출력 [1,1,H,W] logit — 값이 클수록 비드 중심선.
///
/// 학습은 전체 영상을 가로 <c>train_width</c>(ONNX 메타데이터, 기본 640)로 줄여서 했으므로, 추론도 같은 배율로 ROI 를
/// 줄여 넣는다(배율 = train_width / 전체 프레임 가로). 출력은 <b>진행축 슬라이스마다 최댓값 위치</b>(부분 픽셀 보정)를 찾아
/// 그 자리에 얇은 띠를 칠한 마스크로 돌려준다 — 이후 중심선·RANSAC·d·오버레이는 U-Net 과 같은 공유 분석기가 만든다.
/// 학습 데이터의 용접선은 거의 가로라서, 진행축이 세로면 ROI 를 전치(x↔y)해 가로선으로 만든 뒤 추론하고 되돌린다.
/// OpenCV 불필요(배열 연산만) — 단위 테스트 가능.
/// </summary>
public static class HeatmapSegmenter
{
    public const int DefaultTrainWidth = 640;

    /// <summary>모델이 히트맵 형식인지(입력 rank4·채널 3, 출력 1개 rank4).</summary>
    public static bool LooksLikeHeatmap(InferenceSession s)
    {
        var inDims = s.InputMetadata.Values.First().Dimensions;
        var outDims = s.OutputMetadata.Values.First().Dimensions;
        return inDims.Length == 4 && inDims[1] == 3 && outDims.Length == 4 && s.OutputMetadata.Count == 1;
    }

    /// <summary>학습 가로 해상도(ONNX 메타데이터 train_width). 없으면 <see cref="DefaultTrainWidth"/>.</summary>
    public static int TrainWidth(InferenceSession s)
    {
        try
        {
            if (s.ModelMetadata.CustomMetadataMap.TryGetValue("train_width", out var v) && int.TryParse(v, out var w) && w > 0)
                return w;
        }
        catch { /* 메타데이터 없음 */ }
        return DefaultTrainWidth;
    }

    /// <summary>
    /// ROI RGB(row-major w×h×3) → 중심선 띠 마스크(255/0, w×h).
    /// </summary>
    /// <param name="fullWidth">전체 프레임 가로 — 학습 배율을 맞추는 기준.</param>
    /// <param name="horizontal">진행축이 가로(용접선이 가로)인지. false 면 전치해서 추론.</param>
    /// <param name="threshold">슬라이스 최댓값 확률이 이 값 미만이면 그 슬라이스는 비운다.</param>
    /// <param name="halfWidth">칠할 띠 반폭 [px] — 공유 분석기의 최소 run 길이보다 넓게.</param>
    /// <param name="prob">주면 ROI 크기 확률맵(0~1)을 채운다(오버레이·디버그용).</param>
    public static byte[] RidgeMask(InferenceSession session, byte[] rgb, int w, int h, int fullWidth,
        bool horizontal, float threshold, int halfWidth = 2, float[]? prob = null)
    {
        // 진행축 기준 좌표(가로선)로 바꾼 영상 tw×th.
        int tw = horizontal ? w : h, th = horizontal ? h : w;
        var src = horizontal ? rgb : Transpose(rgb, w, h);

        double scale = Math.Clamp((double)TrainWidth(session) / Math.Max(1, fullWidth), 0.05, 4.0);
        int rw = Math.Max(8, (int)Math.Round(tw * scale)), rh = Math.Max(8, (int)Math.Round(th * scale));
        var small = ResizeBilinear(src, tw, th, rw, rh);

        var p = Infer(session, small, rw, rh);   // rw×rh 확률

        // 슬라이스(원래 해상도 열)마다: 해당 축소 열의 최댓값 행 → 원래 좌표로.
        var mask = new byte[w * h];
        float[]? probT = prob is null ? null : new float[tw * th];
        for (int x = 0; x < tw; x++)
        {
            int sx = Math.Clamp((int)Math.Round((x + 0.5) * scale - 0.5), 0, rw - 1);
            int best = 0;
            float bestV = -1;
            for (int y = 0; y < rh; y++)
            {
                float v = p[y * rw + sx];
                if (v > bestV) { bestV = v; best = y; }
            }
            if (probT is not null)
                for (int y = 0; y < th; y++)
                    probT[y * tw + x] = p[Math.Clamp((int)Math.Round((y + 0.5) * scale - 0.5), 0, rh - 1) * rw + sx];
            if (bestV < threshold) continue;
            double yr = best;
            if (best > 0 && best < rh - 1)
            {
                float l = p[(best - 1) * rw + sx], c = p[best * rw + sx], r = p[(best + 1) * rw + sx];
                float den = l - 2 * c + r;
                if (den < 0) yr = best + 0.5 * (l - r) / den;
            }
            int yc = (int)Math.Round((yr + 0.5) / scale - 0.5);
            for (int y = Math.Max(0, yc - halfWidth); y <= Math.Min(th - 1, yc + halfWidth); y++)
            {
                if (horizontal) mask[y * w + x] = 255;
                else mask[x * w + y] = 255;   // 전치 좌표 (x=진행, y=cross) → 원래 (col=y, row=x)
            }
        }
        if (prob is not null && probT is not null)
        {
            if (horizontal) Array.Copy(probT, prob, probT.Length);
            else
                for (int y = 0; y < th; y++)
                    for (int x = 0; x < tw; x++)
                        prob[x * w + y] = probT[y * tw + x];
        }
        return mask;
    }

    /// <summary>
    /// 직선 맞춤 허용 기울기 [°, 진행축 대비]. 진행축이 가로·세로 둘 다 시도되므로 실제 판독 대상은 0~45° —
    /// 대각선(30~45°) 용접선도 받아들이도록 여유를 둔다.
    /// </summary>
    public const double MaxLineAngleDeg = 50.0;

    /// <summary>중심선 직선: 진행 좌표 s(가로면 x, 세로면 y)에서 cross = A·s + B. S0~S1 = inlier 가 걸친 진행 구간.</summary>
    public readonly record struct RidgeLine(double A, double B, double S0, double S1, int Inliers, int Points);

    /// <summary><see cref="RidgeMask"/> 띠 마스크 → 슬라이스마다 (진행 s, 띠 중심 cross) 한 점.</summary>
    public static List<(double S, double C)> RidgePoints(byte[] mask, int w, int h, bool horizontal)
    {
        int n = horizontal ? w : h, c = horizontal ? h : w;
        var pts = new List<(double, double)>();
        for (int s = 0; s < n; s++)
        {
            double sum = 0;
            int cnt = 0;
            for (int k = 0; k < c; k++)
                if (mask[horizontal ? k * w + s : s * w + k] != 0) { sum += k; cnt++; }
            if (cnt > 0) pts.Add((s, sum / cnt));
        }
        return pts;
    }

    /// <summary>
    /// 슬라이스 점들에 RANSAC 직선 맞춤(결정적 시드). 슬라이스 최댓값은 선이 없는 자리에서도 잡음 조각을 만드므로,
    /// 가장 많은 점이 지나는 직선 하나만 남기고 나머지는 버린다. 점이 모자라거나 맞춤에 실패하면 null.
    /// </summary>
    /// <param name="inlierPx">inlier 허용 cross 거리 [원래 px].</param>
    /// <param name="sliceCount">진행축 슬라이스 수 — 최소 점 수·두 표본 간 최소 간격의 기준.</param>
    public static RidgeLine? FitLine(IReadOnlyList<(double S, double C)> pts, double inlierPx, int sliceCount,
        double maxAngleDeg = MaxLineAngleDeg, int iters = 300, int seed = 0)
    {
        if (pts.Count < Math.Max(8, 0.05 * sliceCount)) return null;
        double maxSlope = Math.Tan(maxAngleDeg * Math.PI / 180.0);
        var rng = new Random(seed);
        double bestA = 0, bestB = 0;
        int bestIn = -1;
        for (int it = 0; it < iters; it++)
        {
            var (s1, c1) = pts[rng.Next(pts.Count)];
            var (s2, c2) = pts[rng.Next(pts.Count)];
            if (Math.Abs(s2 - s1) < sliceCount * 0.1) continue;
            double a = (c2 - c1) / (s2 - s1);
            if (Math.Abs(a) > maxSlope) continue;
            double b = c1 - a * s1;
            int inl = 0;
            foreach (var (s, c) in pts) if (Math.Abs(c - (a * s + b)) <= inlierPx) inl++;
            if (inl > bestIn) { bestIn = inl; bestA = a; bestB = b; }
        }
        if (bestIn < 2) return null;

        // inlier 로 최소제곱 다시 맞춘 뒤 inlier·구간을 다시 센다.
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int m = 0;
        foreach (var (s, c) in pts)
            if (Math.Abs(c - (bestA * s + bestB)) <= inlierPx) { sx += s; sy += c; sxx += s * s; sxy += s * c; m++; }
        double den = m * sxx - sx * sx;
        if (m >= 2 && Math.Abs(den) > 1e-9)
        {
            double a = (m * sxy - sx * sy) / den;
            if (Math.Abs(a) <= maxSlope) { bestA = a; bestB = (sy - a * sx) / m; }
        }
        int inliers = 0;
        double s0 = double.MaxValue, s1v = double.MinValue;
        foreach (var (s, c) in pts)
        {
            if (Math.Abs(c - (bestA * s + bestB)) > inlierPx) continue;
            inliers++;
            s0 = Math.Min(s0, s);
            s1v = Math.Max(s1v, s);
        }
        return inliers >= 2 ? new RidgeLine(bestA, bestB, s0, s1v, inliers, pts.Count) : null;
    }

    /// <summary>맞춘 직선을 S0~S1 구간에 반폭 <paramref name="halfWidth"/> 띠로 칠한 마스크(w×h).</summary>
    public static byte[] LineMask(int w, int h, bool horizontal, RidgeLine line, int halfWidth = 2)
    {
        var mask = new byte[w * h];
        int n = horizontal ? w : h, cMax = horizontal ? h : w;
        // 기운 선도 끊기지 않게: 인접 슬라이스 사이 cross 변화만큼 띠를 넓힌다.
        int extra = (int)Math.Ceiling(Math.Abs(line.A) / 2.0);
        for (int s = Math.Max(0, (int)Math.Floor(line.S0)); s <= Math.Min(n - 1, (int)Math.Ceiling(line.S1)); s++)
        {
            int cc = (int)Math.Round(line.A * s + line.B);
            for (int k = Math.Max(0, cc - halfWidth - extra); k <= Math.Min(cMax - 1, cc + halfWidth + extra); k++)
                mask[horizontal ? k * w + s : s * w + k] = 255;
        }
        return mask;
    }

    /// <summary>모델 실행: RGB(w×h×3) → 확률(w×h). 32 배수 반사 패딩 후 잘라낸다.</summary>
    public static float[] Infer(InferenceSession session, byte[] rgb, int w, int h)
    {
        int H = UnetSegmenter.PadTo32(h), W = UnetSegmenter.PadTo32(w);
        var input = new DenseTensor<float>(new[] { 1, 3, H, W });
        var buf = input.Buffer.Span;
        int plane = H * W;
        for (int y = 0; y < H; y++)
        {
            int sy = UnetSegmenter.Reflect(y, h);
            for (int x = 0; x < W; x++)
            {
                int si = (sy * w + UnetSegmenter.Reflect(x, w)) * 3;
                int di = y * W + x;
                buf[di] = rgb[si] / 255f;
                buf[plane + di] = rgb[si + 1] / 255f;
                buf[2 * plane + di] = rgb[si + 2] / 255f;
            }
        }
        var inName = session.InputMetadata.Keys.First();
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });
        var outT = results.First().AsTensor<float>();
        if (outT.Dimensions.Length != 4 || outT.Dimensions[2] != H || outT.Dimensions[3] != W)
            throw new InvalidOperationException(
                $"히트맵 모델 출력 형태가 예상과 다릅니다: [{string.Join(',', outT.Dimensions.ToArray())}] (기대 [1,1,{H},{W}])");
        var flat = outT is DenseTensor<float> dt ? dt.Buffer.Span : outT.ToArray().AsSpan();
        var p = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                p[y * w + x] = 1f / (1f + MathF.Exp(-flat[y * W + x]));
        return p;
    }

    /// <summary>RGB(w×h×3) 전치 → (h×w×3).</summary>
    public static byte[] Transpose(byte[] rgb, int w, int h)
    {
        var t = new byte[rgb.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 3, d = (x * h + y) * 3;
                t[d] = rgb[s]; t[d + 1] = rgb[s + 1]; t[d + 2] = rgb[s + 2];
            }
        return t;
    }

    /// <summary>RGB 쌍선형 크기 변경(픽셀 중심 정렬 — cv2.INTER_LINEAR 와 같은 좌표 규약).</summary>
    public static byte[] ResizeBilinear(byte[] rgb, int w, int h, int nw, int nh)
    {
        if (nw == w && nh == h) return rgb;
        var o = new byte[nw * nh * 3];
        double sx = (double)w / nw, sy = (double)h / nh;
        for (int y = 0; y < nh; y++)
        {
            double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, h - 1);
            int y0 = (int)fy, y1 = Math.Min(h - 1, y0 + 1);
            double ay = fy - y0;
            for (int x = 0; x < nw; x++)
            {
                double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, w - 1);
                int x0 = (int)fx, x1 = Math.Min(w - 1, x0 + 1);
                double ax = fx - x0;
                for (int c = 0; c < 3; c++)
                {
                    double v = (1 - ay) * ((1 - ax) * rgb[(y0 * w + x0) * 3 + c] + ax * rgb[(y0 * w + x1) * 3 + c])
                             + ay * ((1 - ax) * rgb[(y1 * w + x0) * 3 + c] + ax * rgb[(y1 * w + x1) * 3 + c]);
                    o[(y * nw + x) * 3 + c] = (byte)Math.Clamp(Math.Round(v), 0, 255);
                }
            }
        }
        return o;
    }
}
