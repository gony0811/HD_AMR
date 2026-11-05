using HD.AMR.App.Models;

namespace HD.AMR.App.Communication.Weld;

/// <summary>
/// 헤시안 기반 <b>방향 제한 ridge(가는 선) 필터</b>. 학습 없이 IR 영상에서 가는 용접선을 강조한다.
/// <para>
/// 각 스케일 σ 에서 가우시안 평활 후 2차 미분(헤시안)의 고유값 λ1(작은 |λ|), λ2(큰 |λ|)를 구한다.
/// 선(ridge)은 가로지르는 방향으로만 곡률이 커서 |λ2| ≫ |λ1| 이고, 도트·점 반사(blob)는 |λ1|≈|λ2|,
/// 넓은 밝기 변화는 둘 다 작다. 응답 R = |λ2|·(1 − |λ1|/|λ2|)²·σ² 로 선만 남기고 blob 을 누른다(Frangi 단순화).
/// 또한 λ2 고유벡터(=선을 가로지르는 방향)가 진행축의 cross 방향과 <c>maxAngleDeg</c> 이내일 때만
/// 응답을 인정해, 진행축과 다른 방향의 코로게이션 모서리를 걸러낸다. 여러 σ 중 최대값을 쓴다.
/// </para>
/// 순수 C#(OpenCV 불필요) — 단위 테스트 가능. 848×480·3스케일 기준 수십~100ms 수준.
/// </summary>
public static class RidgeFilter
{
    /// <summary>
    /// ridge 응답 맵(row-major, w×h, ≥0). <paramref name="polarity"/>: Bright=밝은 선(λ2&lt;0), Dark=어두운 선(λ2&gt;0), Both=둘 다.
    /// </summary>
    public static float[] Response(byte[] gray, int w, int h, IReadOnlyList<double> sigmas,
        WeldRidgePolarity polarity, bool horizontal, double maxAngleDeg)
    {
        var src = new float[w * h];
        for (int i = 0; i < src.Length; i++) src[i] = gray[i];
        var best = new float[w * h];
        // λ2 고유벡터와 cross 축 사이 각도 ≤ maxAngle ⇔ |cross 성분| ≥ cos(maxAngle).
        double minCos = Math.Cos(Math.Clamp(maxAngleDeg, 0, 90) * Math.PI / 180.0);

        foreach (var sigma in sigmas)
        {
            if (sigma <= 0) continue;
            var g = GaussianBlur(src, w, h, sigma);
            float s2 = (float)(sigma * sigma);
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    float c = g[i];
                    float dxx = g[i - 1] - 2 * c + g[i + 1];
                    float dyy = g[i - w] - 2 * c + g[i + w];
                    float dxy = (g[i - w - 1] + g[i + w + 1] - g[i - w + 1] - g[i + w - 1]) * 0.25f;

                    // 2×2 대칭행렬 고유값.
                    float tr = dxx + dyy;
                    float disc = MathF.Sqrt((dxx - dyy) * (dxx - dyy) + 4 * dxy * dxy);
                    float l1 = 0.5f * (tr + disc), l2 = 0.5f * (tr - disc);
                    if (MathF.Abs(l1) > MathF.Abs(l2)) (l1, l2) = (l2, l1);   // |l1| ≤ |l2|
                    if (polarity == WeldRidgePolarity.Bright && l2 >= 0) continue;
                    if (polarity == WeldRidgePolarity.Dark && l2 <= 0) continue;

                    // l2 의 고유벡터 (vx,vy): (dxy, l2-dxx) 또는 (l2-dyy, dxy) 중 수치적으로 큰 쪽.
                    float vx = dxy, vy = l2 - dxx;
                    float ux = l2 - dyy, uy = dxy;
                    if (ux * ux + uy * uy > vx * vx + vy * vy) { vx = ux; vy = uy; }
                    float n = MathF.Sqrt(vx * vx + vy * vy);
                    if (n < 1e-6f) continue;   // 등방(dxy=0 이고 dxx==dyy) — 방향 없음
                    float crossComp = MathF.Abs(horizontal ? vy : vx) / n;
                    if (crossComp < minCos) continue;

                    // |λ2|·(1−|λ1|/|λ2|)² — 이방성 제곱으로 blob 가장자리(약한 이방성)를 더 강하게 누른다.
                    float a2 = MathF.Abs(l2);
                    float aniso = 1 - MathF.Abs(l1) / a2;
                    float r = a2 * aniso * aniso * s2;
                    if (r > best[i]) best[i] = r;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// 응답 맵 → 이진 마스크(255/0). 두 조건을 모두 만족하는 픽셀만 남긴다.
    /// <list type="number">
    /// <item><b>전역 잡음 하한</b>: max(중앙값 + k·1.4826·MAD, 0.1·전역 최대). 선이 없는 평탄·잡음 영상에서 후보를 막는다.</item>
    /// <item><b>슬라이스 상대 임계</b>: 진행축 슬라이스(가로면 열)마다 그 슬라이스 최대 응답의 <paramref name="relFrac"/> 이상.
    /// 조명·대비가 화면 좌우로 달라도 슬라이스마다 가장 선다운 픽셀이 남아, RANSAC 이 일직선 후보를 고를 수 있다.</item>
    /// </list>
    /// </summary>
    public static byte[] Threshold(float[] resp, int w, int h, bool horizontal, double k, double relFrac = 0.5)
    {
        var mask = new byte[resp.Length];
        if (resp.Length == 0) return mask;

        // 전역 하한 — 중앙값/MAD 는 표본(최대 ~40k)으로 근사해 정렬 비용을 줄인다.
        int step = Math.Max(1, resp.Length / 40000);
        var sample = new List<float>(resp.Length / step + 1);
        float gmax = 0;
        for (int i = 0; i < resp.Length; i++)
        {
            if (resp[i] > gmax) gmax = resp[i];
            if (i % step == 0) sample.Add(resp[i]);
        }
        if (gmax <= 0) return mask;
        sample.Sort();
        float med = sample[sample.Count / 2];
        for (int i = 0; i < sample.Count; i++) sample[i] = Math.Abs(sample[i] - med);
        sample.Sort();
        float mad = sample[sample.Count / 2];
        float floor = (float)Math.Max(med + k * 1.4826 * mad, 0.1 * gmax);

        int sCount = horizontal ? w : h, cCount = horizontal ? h : w;
        float rel = (float)Math.Clamp(relFrac, 0, 1);
        for (int s = 0; s < sCount; s++)
        {
            float smax = 0;
            for (int c = 0; c < cCount; c++)
            {
                float v = resp[horizontal ? c * w + s : s * w + c];
                if (v > smax) smax = v;
            }
            if (smax <= floor) continue;
            float thr = Math.Max(floor, rel * smax);
            for (int c = 0; c < cCount; c++)
            {
                int i = horizontal ? c * w + s : s * w + c;
                if (resp[i] >= thr) mask[i] = 255;
            }
        }
        return mask;
    }

    /// <summary>σ_min~σ_max 를 n 개로 등비 분할한 스케일 목록.</summary>
    public static double[] Scales(double min, double max, int count)
    {
        min = Math.Max(0.5, min); max = Math.Max(min, max); count = Math.Max(1, count);
        if (count == 1 || max <= min) return new[] { min };
        var r = new double[count];
        double ratio = Math.Pow(max / min, 1.0 / (count - 1));
        for (int i = 0; i < count; i++) r[i] = min * Math.Pow(ratio, i);
        return r;
    }

    /// <summary>분리형 가우시안 블러(경계 복제).</summary>
    public static float[] GaussianBlur(float[] src, int w, int h, double sigma)
    {
        int rad = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var k = new float[2 * rad + 1];
        double ks = 0;
        for (int i = -rad; i <= rad; i++) { k[i + rad] = (float)Math.Exp(-i * i / (2 * sigma * sigma)); ks += k[i + rad]; }
        for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / ks);

        var tmp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int j = -rad; j <= rad; j++) acc += k[j + rad] * src[row + Math.Clamp(x + j, 0, w - 1)];
                tmp[row + x] = acc;
            }
        }
        var dst = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int j = -rad; j <= rad; j++) acc += k[j + rad] * tmp[Math.Clamp(y + j, 0, h - 1) * w + x];
                dst[y * w + x] = acc;
            }
        return dst;
    }
}
