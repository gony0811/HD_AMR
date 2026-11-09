using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// CLAHE(대비 제한 적응형 히스토그램 평활화) 전처리 — 학습 데이터셋 변환과 모든 추론(④ 추론·검증, 운영 YOLO·U-Net)이
/// <b>이 구현 하나</b>를 써서 학습/추론 입력이 항상 같다(OS·OpenCV 유무와 무관). 알고리즘은 OpenCV <c>createCLAHE</c>
/// (8-bit) 와 같게 구현했다 — 타일별 히스토그램을 clip 한도로 자르고 넘친 양을 고르게 재분배한 누적 LUT 를 만든 뒤,
/// 픽셀마다 이웃 4 타일 LUT 를 양선형 보간한다.
/// <list type="bullet">
/// <item>IR(흑백): 그대로 적용.</item>
/// <item>RGB: 밝기(Y = 0.299R + 0.587G + 0.114B)에만 적용하고 R·G·B 에 같은 변화량을 더한다 → 색차(Cb·Cr) 유지.
/// R=G=B 인 흑백 영상에서는 흑백 적용과 결과가 같다.</item>
/// </list>
/// 설정은 고정(<see cref="ClipLimit"/>, <see cref="Tiles"/>) — 값이 바뀌면 학습/추론이 어긋나므로 모델 이름의 <c>_clahe</c>
/// 표식 하나로 켜고 끈다. <b>항상 전체 프레임에 적용한 뒤 ROI 를 자른다</b>(타일 격자가 영상 기준이라 ROI 만 처리하면 결과가 다르다).
/// </summary>
public static class ImageClahe
{
    public const double ClipLimit = 2.0;
    public const int Tiles = 8;
    private const int HistSize = 256;

    /// <summary>흑백 영상(row-major w×h) CLAHE 결과(새 배열).</summary>
    public static byte[] ApplyGray(byte[] src, int w, int h, double clipLimit = ClipLimit, int tilesX = Tiles, int tilesY = Tiles)
    {
        // 타일로 나누어떨어지지 않으면 OpenCV 처럼 BORDER_REFLECT_101 로 오른쪽·아래를 채워 LUT 를 계산한다.
        int pw = w % tilesX == 0 ? w : w + tilesX - w % tilesX;
        int ph = h % tilesY == 0 ? h : h + tilesY - h % tilesY;
        byte[] lutSrc = pw == w && ph == h ? src : PadReflect101(src, w, h, pw, ph);
        int tw = pw / tilesX, th = ph / tilesY;
        int tileTotal = tw * th;

        int clip = clipLimit > 0 ? Math.Max((int)(clipLimit * tileTotal / HistSize), 1) : 0;
        float lutScale = (float)(HistSize - 1) / tileTotal;
        var luts = new byte[tilesX * tilesY * HistSize];
        var hist = new int[HistSize];

        for (int ty = 0; ty < tilesY; ty++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                Array.Clear(hist);
                for (int y = ty * th; y < (ty + 1) * th; y++)
                {
                    int row = y * pw;
                    for (int x = tx * tw; x < (tx + 1) * tw; x++) hist[lutSrc[row + x]]++;
                }
                if (clip > 0)
                {
                    int clipped = 0;
                    for (int i = 0; i < HistSize; i++)
                        if (hist[i] > clip) { clipped += hist[i] - clip; hist[i] = clip; }
                    int batch = clipped / HistSize, residual = clipped - batch * HistSize;
                    for (int i = 0; i < HistSize; i++) hist[i] += batch;
                    if (residual != 0)
                    {
                        int step = Math.Max(HistSize / residual, 1);
                        for (int i = 0; i < HistSize && residual > 0; i += step, residual--) hist[i]++;
                    }
                }
                int sum = 0, o = (ty * tilesX + tx) * HistSize;
                for (int i = 0; i < HistSize; i++)
                {
                    sum += hist[i];
                    luts[o + i] = Sat(sum * lutScale);
                }
            }

        // 보간 — 픽셀 중심이 속한 이웃 4 타일 LUT 의 양선형 가중합(가장자리는 가까운 타일로 고정).
        var dst = new byte[w * h];
        float invTw = 1.0f / tw, invTh = 1.0f / th;
        var x1 = new int[w]; var x2 = new int[w]; var xa = new float[w];
        for (int x = 0; x < w; x++)
        {
            float txf = x * invTw - 0.5f;
            int a = (int)MathF.Floor(txf);
            xa[x] = txf - a;
            x1[x] = Math.Max(a, 0) * HistSize;
            x2[x] = Math.Min(a + 1, tilesX - 1) * HistSize;
        }
        for (int y = 0; y < h; y++)
        {
            float tyf = y * invTh - 0.5f;
            int b = (int)MathF.Floor(tyf);
            float ya = tyf - b, ya1 = 1.0f - ya;
            int r1 = Math.Max(b, 0) * tilesX * HistSize, r2 = Math.Min(b + 1, tilesY - 1) * tilesX * HistSize;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int v = src[row + x];
                float xr = xa[x], xl = 1.0f - xr;
                float res = (luts[r1 + x1[x] + v] * xl + luts[r1 + x2[x] + v] * xr) * ya1
                          + (luts[r2 + x1[x] + v] * xl + luts[r2 + x2[x] + v] * xr) * ya;
                dst[row + x] = Sat(res);
            }
        }
        return dst;
    }

    /// <summary>
    /// 인터리브 컬러(채널 3 이상, row-major) 제자리 처리 — 밝기 Y 에 CLAHE 를 걸고 R·G·B 에 같은 변화량을 더한다.
    /// <paramref name="bgr"/>=true 면 채널 순서가 B,G,R(OpenCV Mat).
    /// </summary>
    public static void ApplyColorInPlace(byte[] px, int w, int h, int channels, bool bgr)
    {
        int ri = bgr ? 2 : 0, bi = bgr ? 0 : 2;
        var y = new byte[w * h];
        for (int i = 0; i < y.Length; i++)
        {
            int o = i * channels;
            y[i] = Sat(0.299f * px[o + ri] + 0.587f * px[o + 1] + 0.114f * px[o + bi]);
        }
        var y2 = ApplyGray(y, w, h);
        for (int i = 0; i < y.Length; i++)
        {
            int d = y2[i] - y[i];
            if (d == 0) continue;
            int o = i * channels;
            for (int c = 0; c < 3; c++) px[o + c] = (byte)Math.Clamp(px[o + c] + d, 0, 255);
        }
    }

    /// <summary>
    /// 이미지 파일 바이트(PNG/JPEG) → CLAHE 적용 PNG 바이트. 흑백 PNG(L8 계열)는 흑백으로, 그 외는 컬러(밝기)로 처리.
    /// </summary>
    public static byte[] ApplyEncoded(byte[] encoded)
    {
        var info = Image.Identify(encoded);
        bool gray = info.PixelType.BitsPerPixel <= 16 && IsGrayPng(info);
        using var ms = new MemoryStream();
        if (gray)
        {
            using var img = Image.Load<L8>(encoded);
            var g = new byte[img.Width * img.Height];
            img.CopyPixelDataTo(g);
            var o = ApplyGray(g, img.Width, img.Height);
            using var outImg = Image.LoadPixelData<L8>(o, img.Width, img.Height);
            outImg.SaveAsPng(ms);
        }
        else
        {
            using var img = Image.Load<Rgb24>(encoded);
            var px = new byte[img.Width * img.Height * 3];
            img.CopyPixelDataTo(px);
            ApplyColorInPlace(px, img.Width, img.Height, 3, bgr: false);
            using var outImg = Image.LoadPixelData<Rgb24>(px, img.Width, img.Height);
            outImg.SaveAsPng(ms);
        }
        return ms.ToArray();
    }

    /// <summary>이미지 파일 → CLAHE 적용 PNG 로 저장(데이터셋 변환용).</summary>
    public static void ApplyFile(string src, string dst) => File.WriteAllBytes(dst, ApplyEncoded(File.ReadAllBytes(src)));

    private static bool IsGrayPng(ImageInfo info)
        => info.Metadata.GetPngMetadata().ColorType is SixLabors.ImageSharp.Formats.Png.PngColorType.Grayscale
            or SixLabors.ImageSharp.Formats.Png.PngColorType.GrayscaleWithAlpha;

    // OpenCV saturate_cast<uchar>(float): 가장 가까운 정수(짝수 반올림) 후 0~255 로 자름.
    private static byte Sat(float v)
    {
        int r = (int)MathF.Round(v, MidpointRounding.ToEven);
        return (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
    }

    private static byte[] PadReflect101(byte[] src, int w, int h, int pw, int ph)
    {
        static int Refl(int i, int n) => n == 1 ? 0 : i < n ? i : 2 * n - 2 - i;
        var d = new byte[pw * ph];
        for (int y = 0; y < ph; y++)
        {
            int sy = Refl(y, h);
            for (int x = 0; x < pw; x++) d[y * pw + x] = src[sy * w + Refl(x, w)];
        }
        return d;
    }
}
